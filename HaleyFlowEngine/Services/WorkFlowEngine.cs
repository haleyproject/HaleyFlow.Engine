using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Services.Orchestrators;
using Haley.Utils;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Haley.Internal;
using static Haley.Internal.KeyConstants;
using static Haley.Internal.QueryFields;

namespace Haley.Services {
    // WorkFlowEngine is the central brain of the lifecycle system.
    // It is NOT created directly by the caller — the builder (WorkFlowEngineMaker) constructs it after
    // verifying and provisioning the DB schema. The caller only ever sees ILifeCycleEngine.
    //
    // Internally it is composed of specialised sub-engines (all internal, not exposed publicly):
    //   BlueprintManager  — caches and resolves definitions+versions+states+events by envCode+defName
    //   BlueprintImporter — writes definition JSON and policy JSON into the DB (idempotent, hash-guarded)
    //   StateMachine      — applies state transitions and writes lifecycle (lc_data) entries
    //   PolicyEnforcer    — evaluates policy rules; decides which hooks to emit per transition
    //   AckManager        — creates ACK entries, schedules resends, tracks delivery per consumer
    //   RuntimeEngine     — persists arbitrary runtime-log entries per activity/actor (side-channel data)
    //   Monitor           — background timer that drives the resend loop for undelivered events
    public sealed class WorkFlowEngine : IWorkFlowEngine {
        public IDALUtilBase Dal => _dal;
        private readonly IWorkFlowDAL _dal;
        private readonly WorkFlowEngineOptions _opt;
        internal IStateMachine StateMachine { get; }
        internal IBlueprintManager BlueprintManager { get; }
        internal IBlueprintImporter BlueprintImporter { get; }
        internal IPolicyEnforcer PolicyEnforcer { get; }
        internal IAckManager AckManager { get; }
        internal IRuntimeEngine Runtime { get; }
        internal IEngineCare Care { get; }
        internal ILifeCycleMonitor Monitor { get; }
        private readonly InstanceOrchestrator _instanceOrchestrator;
        private readonly AckOutcomeOrchestrator _ackOutcomeOrchestrator;
        private readonly MonitorOrchestrator _monitorOrchestrator;
        private readonly MaintenanceOrchestrator _maintenanceOrchestrator;

        // Two public events that the host application subscribes to:
        //   EventRaised  — a lifecycle transition or hook event that a consumer must process and ACK.
        //   NoticeRaised — informational / warning / error signals (ACK_RETRY, STATE_STALE, etc.) for monitoring.
        //                  These are fire-and-forget signals; they don't affect engine state.
        public event Func<ILifeCycleEvent, Task>? EventRaised;
        public event Func<LifeCycleNotice, Task>? NoticeRaised;

        internal WorkFlowEngine(IWorkFlowDAL dal, WorkFlowEngineOptions? options = null) {
            _dal = dal ?? throw new ArgumentNullException(nameof(dal));
            _opt = options ?? new WorkFlowEngineOptions();

            BlueprintManager = new BlueprintManager(_dal);
            BlueprintImporter = new BlueprintImporter(_dal);
            StateMachine = new StateMachine(_dal, BlueprintManager);
            PolicyEnforcer =  new PolicyEnforcer(_dal);

            // Consumer resolution at the boundary should use natural keys, not DB surrogate IDs.
            // Preferred contract:
            //   ResolveConsumerGuids(type, envCode, defName) -> consumer GUIDs
            // Engine maps GUIDs to numeric consumer IDs internally for ACK storage.
            var resolveConsumers = _opt.ResolveConsumers;
            if (_opt.ResolveConsumerGuids != null) {
                var resolveConsumerGuids = _opt.ResolveConsumerGuids;
                resolveConsumers = async (LifeCycleConsumerType ty, long? defVersionId, CancellationToken ct) => {
                    ct.ThrowIfCancellationRequested();

                    // Monitor list must come from the caller policy. We do not implicitly treat
                    // all alive consumers as monitors.
                    if (ty == LifeCycleConsumerType.Monitor) {
                        return await ResolveMonitorConsumerIdsByGuidsAsync(resolveConsumerGuids, ct);
                    }

                    //Transition and Hooks need definition
                    if (!defVersionId.HasValue || defVersionId.Value <= 0) return Array.Empty<long>();

                    var bp = await BlueprintManager.GetBlueprintByVersionIdAsync(defVersionId.Value, ct);
                    if (bp.EnvCode <= 0 || string.IsNullOrWhiteSpace(bp.DefName)) return Array.Empty<long>();

                    var guids = await resolveConsumerGuids(ty, bp.EnvCode, bp.DefName, ct);
                    return await ResolveConsumerIdsByGuidsAsync(bp.EnvCode, guids, ct);
                };
            }

            resolveConsumers ??= ((LifeCycleConsumerType ty, long? id /* DefVersionId */, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>()));

            // Keep legacy option populated so existing internals that read ResolveConsumers continue to work.
            _opt.ResolveConsumers = resolveConsumers;

            // Monitor uses the same callback without a defVersionId.
            // The resolver policy decides which consumers should be monitored.
            var resolveMonitors = (LifeCycleConsumerType ty, CancellationToken ct) => resolveConsumers.Invoke(ty, null, ct);

            AckManager = new AckManager(_dal, BlueprintManager, PolicyEnforcer, resolveConsumers, _opt.AckPendingResendAfter, _opt.AckDeliveredResendAfter, _opt.MaxRetryCount, FireNotice);

            Runtime = new RuntimeEngine(_dal);
            Care = new EngineCare(_dal.EngineCare, AckManager);
            _instanceOrchestrator = new InstanceOrchestrator(_dal, _opt, BlueprintManager, StateMachine, PolicyEnforcer, AckManager, DispatchEventsSafeAsync, FireNotice);
            _ackOutcomeOrchestrator = new AckOutcomeOrchestrator(_dal, AckManager, BlueprintManager, PolicyEnforcer, _opt, DispatchEventsSafeAsync, (req, ct) => _instanceOrchestrator.TriggerAsync(req, ct), FireNotice);
            _monitorOrchestrator = new MonitorOrchestrator(_dal, _opt, AckManager, FireEvent, FireNotice,
                (req, ct) => _instanceOrchestrator.TriggerAsync(req, ct),
                (ackId, consumerId, ackGuid, instanceGuid, hookRoute, ct) => _ackOutcomeOrchestrator.AbandonEffectHookAsync(ackId, consumerId, ackGuid, instanceGuid, hookRoute, ct));
            _maintenanceOrchestrator = new MaintenanceOrchestrator(_dal, _opt.MaxRetryCount);

            // The monitor is a background periodic loop. Every MonitorInterval it:
            //   1. Scans for stale instances that have been sitting in the same state too long (STATE_STALE notices)
            //   2. For each active consumer, resends any overdue Pending or Delivered ACK rows
            // Any uncaught exception fires a MONITOR_ERROR notice — the loop itself never crashes the process.
            Monitor = new LifeCycleMonitor(_opt.MonitorInterval, ct => RunMonitorOnceInternalAsync(resolveMonitors, ct), (ex) => FireNotice(LifeCycleNotice.Error("MONITOR_ERROR", "MONITOR_ERROR", ex.Message, ex)));
        }

        public Task StartMonitorAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Monitor.StartAsync(ct); }

        public Task StopMonitorAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Monitor.StopAsync(ct); }

        public async Task<WorkFlowEngineHealth> GetHealthAsync(CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var ttlSeconds = _opt.ConsumerTtlSeconds > 0 ? _opt.ConsumerTtlSeconds : 30;
            var health = await Care.GetHealthAsync(ttlSeconds, _opt.DefaultStateStaleDuration, ct);
            // Stamp the two fields that come from runtime state, not the DB.
            health.IsMonitorRunning = Monitor.IsRunning;
            health.MonitorInterval = _opt.MonitorInterval;
            return health;
        }

        public async ValueTask DisposeAsync() { try { await StopMonitorAsync(CancellationToken.None); } catch { } await Monitor.DisposeAsync(); await _dal.DisposeAsync(); }

        // -----------------------------------------------------------------------
        // TRIGGER — the main entry point. The caller says: "entity X just triggered event Y on definition Z."
        //
        // Everything happens in a single atomic transaction:
        //   1. Load the blueprint (definition + all states/events) — from cache, no DB round-trip usually
        //   2. Ask ResolveConsumers who should receive transition events (throws if nobody)
        //   3. Ensure the instance exists (creates it on first call); attach the current policy
        //   4. If instance is on an old def_version, reload blueprint for that locked version
        //   5. ACK gate check — optionally block if a prior transition still has unresolved consumers
        //   6. Apply the state machine transition → write lc_data row, update instance.current_state
        //   7. Create lifecycle ACK rows (one ack guid, one ack_consumer row per consumer)
        //   8. Evaluate policy hooks for the target state → create hook rows + hook ACK rows
        //   9. Commit all writes atomically
        //  10. Fire events AFTER commit — fire-and-forget; the monitor handles missed deliveries
        // -----------------------------------------------------------------------
        public Task<LifeCycleTriggerResult> TriggerAsync(LifeCycleTriggerRequest req, CancellationToken ct = default) {
            return _instanceOrchestrator.TriggerAsync(req, ct);
        }

        public Task AckAsync(long consumerId, string ackGuid, AckOutcome outcome, string? message = null, DateTimeOffset? retryAt = null, CancellationToken ct = default) {
            return _ackOutcomeOrchestrator.AckAsync(consumerId, ackGuid, outcome, message, retryAt, ct);
        }

        public async Task<LifeCycleInstanceData?> GetInstanceDataAsync(LifeCycleInstanceKey key, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var row = await ResolveInstanceRowByKeyAsync(key, new DbExecutionLoad(ct));
            if (row == null) return null;

            var definitionVersionId = row.GetLong(KEY_DEF_VERSION);
            var currentStateId = row.GetLong(KEY_CURRENT_STATE);
            var instanceFlags = unchecked((uint)row.GetLong(KEY_FLAGS));

            LifeCycleBlueprint? bp = null;
            var definitionVersion = 0;
            string? definitionName = null;
            string? currentStateName = null;
            try {
                bp = await BlueprintManager.GetBlueprintByVersionIdAsync(definitionVersionId, ct);
                definitionVersion = bp.Version;
                definitionName = string.IsNullOrWhiteSpace(bp.DefName) ? null : bp.DefName;
                if (bp.StatesById.TryGetValue(currentStateId, out var state)) {
                    currentStateName = string.IsNullOrWhiteSpace(state.DisplayName) ? state.Name : state.DisplayName;
                }
            } catch {
                // Keep core instance payload available even when blueprint lookup fails.
            }

            if (definitionVersion <= 0 || string.IsNullOrWhiteSpace(definitionName)) {
                var dv = await _dal.Blueprint.GetDefVersionByIdAsync(definitionVersionId, new DbExecutionLoad(ct));
                if (dv != null) {
                    if (definitionVersion <= 0) definitionVersion = dv.GetInt(KEY_VERSION);
                    if (string.IsNullOrWhiteSpace(definitionName))
                        definitionName = dv.GetString(KEY_DEF_NAME) ?? dv.GetString(KEY_NAME);
                }
            }

            return new LifeCycleInstanceData {
                InstanceId = row.GetLong(KEY_ID),
                InstanceGuid = row.GetString(KEY_GUID) ?? string.Empty,
                DefinitionId = row.GetLong(KEY_DEF_ID),
                DefinitionVersionId = definitionVersionId,
                DefinitionVersion = definitionVersion,
                DefinitionName = definitionName,
                EntityId = row.GetString(KEY_ENTITY_ID) ?? string.Empty,
                CurrentStateId = currentStateId,
                CurrentStateName = currentStateName,
                InstanceStatus = GetPrimaryInstanceStatus(instanceFlags),
                Metadata = row.GetString(KEY_METADATA),
                Context = row.GetString(KEY_CONTEXT),
                Created = row.GetDateTime(KEY_CREATED) ?? default
            };
        }

        public async Task<string?> GetInstanceContextAsync(LifeCycleInstanceKey key, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var row = await ResolveInstanceRowByKeyAsync(key, new DbExecutionLoad(ct));
            return row?.GetString(KEY_CONTEXT);
        }

        public async Task<int> SetInstanceContextAsync(LifeCycleInstanceKey key, string? context, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var load = new DbExecutionLoad(ct);
            var row = await ResolveInstanceRowByKeyAsync(key, load);
            if (row == null) return 0;
            var instanceId = row.GetLong(KEY_ID);
            if (instanceId <= 0) return 0;
            return await _dal.Instance.SetContextAsync(instanceId, context, load);
        }

        public async Task<string?> GetTimelineJsonAsync(LifeCycleInstanceKey key, TimelineDetail detail = TimelineDetail.Detailed, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var load = new DbExecutionLoad(ct);
            var row = await ResolveInstanceRowByKeyAsync(key, load);
            if (row == null) return null;
            return await TimelineBuilder.BuildAsync(_dal, row.GetLong(KEY_ID), detail, ct);
        }

        public async Task<IReadOnlyList<InstanceRefItem>> GetInstanceRefsAsync(int envCode, string defName, LifeCycleInstanceFlag flags, int skip, int take, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            var bp = await BlueprintManager.GetBlueprintLatestAsync(envCode, defName, ct);
            var rows = await _dal.Instance.ListByFlagsAndDefVersionPagedAsync(bp.DefVersionId, (uint)flags, skip, take);
            var result = new List<InstanceRefItem>(rows.Count);
            for (var i = 0; i < rows.Count; i++) {
                var r = rows[i];
                result.Add(new InstanceRefItem {
                    EntityId = r.GetString(KEY_ENTITY_ID) ?? string.Empty,
                    InstanceGuid = r.GetString(KEY_INSTANCE_GUID) ?? string.Empty,
                    Created = r.GetDateTime(KEY_CREATED)
                });
            }
            return result;
        }

        public Task<DbRows> ListInstancesAsync(int envCode, string? defName, bool runningOnly, int skip, int take, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            return _dal.Instance.ListByEnvAndDefPagedAsync(envCode, defName, runningOnly, skip, take, new DbExecutionLoad(ct));
        }

        public Task<DbRows> ListInstancesByStatusAsync(int envCode, string? defName, LifeCycleInstanceFlag statusFlags, int skip, int take, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            return _dal.Instance.ListByEnvDefAndStatusPagedAsync(envCode, defName, (uint)statusFlags, skip, take, new DbExecutionLoad(ct));
        }

        public Task<DbRows> ListPendingAcksAsync(int envCode, int skip, int take, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            return _dal.AckConsumer.ListPendingDetailPagedAsync(envCode, skip, take, new DbExecutionLoad(ct));
        }

        public async Task<WorkFlowEngineSummary> GetSummaryAsync(int envCode, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            return await Care.GetSummaryAsync(envCode, ct);
        }

        public Task ClearCacheAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); BlueprintManager.Clear(); PolicyEnforcer.ClearPolicyCache(); return Task.CompletedTask; }

        public Task InvalidateAsync(int envCode, string defName, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); BlueprintManager.Invalidate(envCode, defName); PolicyEnforcer.ClearPolicyCache(); return Task.CompletedTask; }

        public Task InvalidateAsync(long defVersionId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); BlueprintManager.Invalidate(defVersionId); PolicyEnforcer.ClearPolicyCache(); return Task.CompletedTask; }

        // Public entry point for a single monitor pass for ONE specific consumer.
        // Runs resend for both Pending rows (event never delivered) and Delivered rows (delivered but not ACKed).
        // Ignores consumers that are currently down (PushNextDueForDown handles postponing their rows).
        public async Task RunMonitorOnceAsync(long consumerId, CancellationToken ct = default) {
            await _ackOutcomeOrchestrator.RecoverAsync(ct);
            await _monitorOrchestrator.RunMonitorOnceAsync(consumerId, ct);
        }

        internal async Task RunMonitorOnceInternalAsync(Func<LifeCycleConsumerType, CancellationToken, Task<IReadOnlyList<long>>> consumersProvider, CancellationToken ct = default) {
            await _ackOutcomeOrchestrator.RecoverAsync(ct);
            await _monitorOrchestrator.RunMonitorOnceInternalAsync(consumersProvider, ct);
        }

        public Task<long> RegisterConsumerAsync(int envCode, string consumerGuid, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(consumerGuid)) throw new ArgumentNullException(nameof(consumerGuid));
            return BlueprintManager.EnsureConsumerIdAsync(envCode, consumerGuid, ct);
        }

        // Heartbeat — consumer calls this every N seconds to prove it is still alive.
        // The engine uses the last-beat timestamp to decide whether a consumer is "down" when the monitor
        // is deciding whether to postpone resending (no point retrying if the consumer is offline).
        public Task BeatConsumerAsync(int envCode, string consumerGuid, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(consumerGuid)) throw new ArgumentNullException(nameof(consumerGuid));
            return BlueprintManager.BeatConsumerAsync(envCode, consumerGuid, ct);
        }

        // Resolves the engine-assigned definition ID for a (envCode, definitionName) pair.
        // Used by the consumer library at startup to bind auto-discovered handler wrappers to their
        // internal def_id — so the consumer knows which definition it is subscribing to.
        public async Task<long?> GetDefinitionIdAsync(int envCode, string definitionName, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definitionName)) throw new ArgumentNullException(nameof(definitionName));
            var row = await _dal.Blueprint.GetLatestDefVersionByEnvCodeAndDefNameAsync(envCode, definitionName, new DbExecutionLoad(ct));
            var id = row?.GetLong(KEY_PARENT);
            return id > 0 ? id : null;
        }

        // Consumer-friendly overload: resolves the consumer by guid (envCode+guid → consumerId) then ACKs.
        // Useful for scenarios where the consumer only knows its guid, not its numeric ID.
        public async Task AckAsync(int envCode, string consumerGuid, string ackGuid, AckOutcome outcome, string? message = null, DateTimeOffset? retryAt = null, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(consumerGuid)) throw new ArgumentNullException(nameof(consumerGuid));
            var consumerId = await BlueprintManager.EnsureConsumerIdAsync(envCode, consumerGuid, ct);
            await AckAsync(consumerId, ackGuid, outcome, message, retryAt, ct);
        }

        public async Task<long> UpsertRuntimeAsync(RuntimeLogByNameRequest req, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (string.IsNullOrWhiteSpace(req.Activity)) throw new ArgumentNullException(nameof(req.Activity));
            if (string.IsNullOrWhiteSpace(req.Status)) throw new ArgumentNullException(nameof(req.Status));
            if (string.IsNullOrWhiteSpace(req.ActorId)) throw new ArgumentNullException(nameof(req.ActorId));

            // Normalize at the API boundary: trim, lowercase, collapse repeated whitespace.
            // Consumers send "Running", "APPROVED", "send approval" — we store the canonical form.
            var activity = InternalUtils.NormalizeRuntimeName(req.Activity);
            var status   = InternalUtils.NormalizeRuntimeName(req.Status);

            var load = new DbExecutionLoad(ct);
            var instanceRow = await ResolveInstanceRowByKeyAsync(req.Instance, load);
            if (instanceRow == null) throw new InvalidOperationException("Instance not found for the provided LifeCycleInstanceKey.");

            // StateId resolution:
            //   0. IsGeneral=true → state_id=0, lc_id=0. Entry is permanently "Other Activities".
            //      Use for instance-level side-effects that don't belong to any specific state.
            //   1. AckGuid provided → resolve via hook_ack → hook.state_id.
            //      Anchors the entry to the state the hook originally fired for — correct during replay.
            //   2. No AckGuid → fall back to instance.current_state_id (safe for direct/non-hook calls).
            long stateId;
            if (req.IsGeneral) {
                stateId = 0;
            } else if (!string.IsNullOrWhiteSpace(req.AckGuid)) {
                var resolvedStateId = await _dal.HookAck.GetStateIdByAckGuidAsync(req.AckGuid, load);
                stateId = resolvedStateId ?? instanceRow.GetLong(KEY_CURRENT_STATE_ID);
            } else {
                stateId = instanceRow.GetLong(KEY_CURRENT_STATE_ID);
            }

            var activityId = await Runtime.EnsureActivityAsync(activity, ct);
            var statusId   = await Runtime.EnsureActivityStatusAsync(status, ct);

            return await Runtime.UpsertAsync(new RuntimeLogByIdRequest {
                InstanceGuid = instanceRow.GetString(KEY_GUID) ?? string.Empty,
                ActivityId   = activityId,
                StateId      = stateId,
                ActorId      = req.ActorId,
                StatusId     = statusId,
                Data         = req.Data ?? new { },
                Payload      = req.Payload ?? new { }
            }, ct);
        }

        public Task<bool> SuspendInstanceAsync(string instanceGuid, string? message, CancellationToken ct = default)
            => _maintenanceOrchestrator.SuspendAsync(instanceGuid, message, ct);

        public Task<bool> ResumeInstanceAsync(string instanceGuid, CancellationToken ct = default)
            => _maintenanceOrchestrator.ResumeAsync(instanceGuid, ct);

        public Task<bool> FailInstanceAsync(string instanceGuid, string? message, CancellationToken ct = default)
            => _maintenanceOrchestrator.FailAsync(instanceGuid, message, ct);

        // Resets a terminal instance back to its initial state and immediately fires the auto-start event.
        // Terminal = Completed, Failed, or Archived. Suspended-only instances are NOT treated as terminal.
        //
        // Flow:
        //   1. Resolve instance by guid.
        //   2. Check that at least one terminal flag (Completed | Failed | Archived) is set.
        //   3. Load blueprint → read InitialStateId.
        //   4. ForceReset: single atomic UPDATE — sets current_state=initial, clears Suspended|Completed|Failed|Archived, sets Active, NULLs last_event and message.
        //   5. Find the auto-start event: the first defined transition out of the initial state.
        //   6. Call TriggerAsync with that event — creates the lifecycle row, fires hooks, fans out ACKs.
        public Task<LifeCycleTriggerResult> ReopenAsync(string instanceGuid, string actor, CancellationToken ct = default) {
            return _instanceOrchestrator.ReopenAsync(instanceGuid, actor, ct);
        }

        public Task<bool> UnsuspendAsync(string instanceGuid, string actor, CancellationToken ct = default)
            => _maintenanceOrchestrator.UnsuspendAsync(instanceGuid, actor, ct);

        // ── Backfill ──────────────────────────────────────────────────────────────────────────────

        // Returns a read-only projection of the definition so consumers can build and validate
        // WorkflowBackfillObjects client-side without accessing the engine DB.
        // Returns null if the definition is not found for (envCode, definitionName).
        public async Task<WorkflowDefinitionSnapshot?> GetDefinitionSnapshotAsync(int envCode, string definitionName, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definitionName)) throw new ArgumentNullException(nameof(definitionName));
            var load = new DbExecutionLoad(ct);
            var version = await _dal.Blueprint.GetLatestDefVersionByEnvCodeAndDefNameAsync(envCode, definitionName, load);
            if (version == null) return null;
            var definition = JsonNode.Parse(version.GetString(KEY_DATA) ?? "{}")!.AsObject();
            definition[KEY_NAME] = definitionName;
            var policy = await _dal.Blueprint.GetPolicyForDefVersion(version.GetLong(KEY_ID), load);
            return DefinitionJsonReader.ReadSnapshot(definition.ToJsonString(), policy?.GetString(KEY_CONTENT), envCode);
        }

        // Imports a pre-validated backfill object as read-only history.
        // Writes instance + lifecycle + hook rows. No ACKs, no hook dispatch events.
        // hook_lc rows are stamped dispatched=1 so the blocking hook gate does not treat them
        // as pending dispatch; ack_consumer count=0 on those rows is the backfill marker.
        public async Task<BackfillImportResult> ImportBackfillAsync(WorkflowBackfillObject obj, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (!obj.Validated) return BackfillImportResult.Fail("NotValidated");
            if (string.IsNullOrWhiteSpace(obj.WorkflowName)) return BackfillImportResult.Fail("WorkflowNameRequired");
            if (string.IsNullOrWhiteSpace(obj.EntityRef)) return BackfillImportResult.Fail("EntityRefRequired");
            if (obj.Transitions == null || obj.Transitions.Count == 0) return BackfillImportResult.Fail("NoTransitions");
            var version = await _dal.Blueprint.GetLatestDefVersionByEnvCodeAndDefNameAsync(obj.EnvCode, obj.WorkflowName, new DbExecutionLoad(ct));
            if (version == null) return BackfillImportResult.Fail("DefinitionNotFound");
            var bp = await BlueprintManager.GetBlueprintByVersionIdAsync(version.GetLong(KEY_ID), ct);
            var existingInstance = await _dal.Instance.GetByDefIdAndEntityIdAsync(bp.DefinitionId, obj.EntityRef, new DbExecutionLoad(ct));
            if (existingInstance != null)
                bp = await BlueprintManager.GetBlueprintByVersionIdAsync(existingInstance.GetLong(KEY_DEF_VERSION), ct);
            var statesByName = bp.StatesById.Values.ToDictionary(x => x.Name, x => (long)x.Id, StringComparer.OrdinalIgnoreCase);
            var expectedState = bp.InitialStateId;
            DateTime? previousTime = null;
            foreach (var tr in obj.Transitions) {
                if (tr == null || !statesByName.TryGetValue(tr.FromState ?? string.Empty, out var fromId) ||
                    !statesByName.TryGetValue(tr.ToState ?? string.Empty, out var toId))
                    return BackfillImportResult.Fail("UnknownState");
                if (fromId != expectedState) return BackfillImportResult.Fail("DisconnectedHistory");
                if (tr.Timestamp == default || (previousTime.HasValue && tr.Timestamp < previousTime.Value))
                    return BackfillImportResult.Fail("InvalidChronology");
                if (!bp.EventsByCode.TryGetValue(tr.EventCode, out var ev) ||
                    !bp.Transitions.TryGetValue(Tuple.Create(fromId, ev.Id), out var transition) || transition.ToStateId != toId)
                    return BackfillImportResult.Fail($"InvalidTransition:{tr.FromState}:{tr.EventCode}");
                expectedState = toId;
                previousTime = tr.Timestamp;
            }
            var content = JsonSerializer.Serialize(new {
                WorkflowName = obj.WorkflowName.Trim().ToLowerInvariant(), obj.EnvCode,
                EntityRef = obj.EntityRef.Trim().ToLowerInvariant(), obj.Metadata,
                Transitions = obj.Transitions.Select(t => new {
                    FromState = t.FromState.Trim().ToLowerInvariant(), ToState = t.ToState.Trim().ToLowerInvariant(),
                    t.EventCode, t.Timestamp, t.Actor, t.Payload, t.Hooks
                })
            });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            var policy = await PolicyEnforcer.ResolvePolicyAsync(bp.DefinitionId, new DbExecutionLoad(ct));
            var transaction = _dal.CreateNewTransaction();
            using var tx = transaction.Begin(false);
            var load = new DbExecutionLoad(ct, transaction);
            try {
                var guid = await _dal.Instance.UpsertByKeyReturnGuidAsync(bp.DefVersionId, obj.EntityRef,
                    bp.InitialStateId, null, policy.PolicyId ?? 0, (uint)LifeCycleInstanceFlag.Active, obj.Metadata, load);
                if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("Backfill instance could not be created.");
                var instanceId = await _dal.Instance.GetIdByGuidAsync(guid, load) ?? 0;
                var instance = await _dal.Execution.LockInstanceAsync(instanceId, load)
                    ?? throw new InvalidOperationException("Backfill instance disappeared.");
                var existingHash = await _dal.Execution.GetBackfillHashAsync(instanceId, load);
                if (existingHash != null) {
                    transaction.Rollback();
                    return existingHash == hash ? BackfillImportResult.Ok(guid) : BackfillImportResult.Fail("BackfillContentConflict");
                }
                if (instance.GetLong(KEY_DEF_VERSION) != bp.DefVersionId ||
                    await _dal.LifeCycle.GetLastByInstanceAsync(instanceId, load) != null) {
                    transaction.Rollback();
                    return BackfillImportResult.Fail("InstanceAlreadyHasHistory");
                }
                foreach (var tr in obj.Transitions) {
                    ct.ThrowIfCancellationRequested();
                    var fromId = statesByName[tr.FromState];
                    var toId = statesByName[tr.ToState];
                    var ev = bp.EventsByCode[tr.EventCode];
                    var lcId = await _dal.LifeCycle.InsertAsync(instanceId, fromId, toId, ev.Id, tr.Timestamp, load);
                    await _dal.LifeCycleData.UpsertAsync(lcId, tr.Actor, tr.Payload, load);
                    if (tr.Hooks != null) foreach (var hook in tr.Hooks) {
                        if (string.IsNullOrWhiteSpace(hook.Route)) continue;
                        var hookId = await _dal.Hook.UpsertByKeyReturnIdAsync(instanceId, toId, ev.Id, true,
                            hook.Route, HookType.Effect, null, 1, 0, false, load);
                        var hookLcId = await _dal.HookLc.InsertReturnIdAsync(hookId, lcId, load);
                        await _dal.HookLc.MarkDispatchedAsync(hookLcId, load);
                    }
                }
                var lastEvent = bp.EventsByCode[obj.Transitions[^1].EventCode].Id;
                await _dal.Instance.UpdateCurrentStateCasAsync(instanceId, instance.GetLong(KEY_CURRENT_STATE), expectedState, lastEvent, load);
                var isFinal = (bp.StatesById[expectedState].Flags & (uint)LifeCycleStateFlag.IsFinal) != 0;
                await _dal.Instance.AddFlagsAsync(instanceId, (uint)(isFinal ? LifeCycleInstanceFlag.Completed : LifeCycleInstanceFlag.Active), load);
                await _dal.Instance.RemoveFlagsAsync(instanceId, (uint)(isFinal ? LifeCycleInstanceFlag.Active : LifeCycleInstanceFlag.Completed), load);
                await _dal.Execution.SaveBackfillHashAsync(instanceId, hash, load);
                transaction.Commit();
                return BackfillImportResult.Ok(guid);
            } catch {
                transaction.Rollback();
                throw;
            }
        }

        private async Task<IReadOnlyList<long>> ResolveMonitorConsumerIdsByGuidsAsync(Func<LifeCycleConsumerType, int, string?, CancellationToken, Task<IReadOnlyList<string>>> resolveConsumerGuids, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            var ttlSeconds = _opt.ConsumerTtlSeconds > 0 ? _opt.ConsumerTtlSeconds : 30;
            var envCodes = await _dal.Consumer.ListAliveEnvCodesAsync(ttlSeconds, new DbExecutionLoad(ct));
            if (envCodes.Count == 0) return Array.Empty<long>();

            var monitorIds = new HashSet<long>();
            for (var i = 0; i < envCodes.Count; i++) {
                ct.ThrowIfCancellationRequested();

                var envCode = envCodes[i];
                if (envCode <= 0) continue;

                IReadOnlyList<string>? guids;
                try {
                    guids = await resolveConsumerGuids(LifeCycleConsumerType.Monitor, envCode, null, ct);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    FireNotice(LifeCycleNotice.Warn("MONITOR_CONSUMER_RESOLVE_ERROR", "MONITOR_CONSUMER_RESOLVE_ERROR",
                        $"Monitor consumer resolution failed for env={envCode}. Skipping this env.",
                        new Dictionary<string, object?> {
                            ["envCode"] = envCode,
                            ["error"] = ex.Message
                        }));
                    continue;
                }

                if (guids == null || guids.Count == 0) continue;

                var ids = await ResolveConsumerIdsByGuidsAsync(envCode, guids, ct);
                for (var j = 0; j < ids.Count; j++) {
                    var id = ids[j];
                    if (id > 0) monitorIds.Add(id);
                }
            }

            if (monitorIds.Count == 0) return Array.Empty<long>();
            var arr = new long[monitorIds.Count];
            monitorIds.CopyTo(arr);
            return arr;
        }
        private async Task<IReadOnlyList<long>> ResolveConsumerIdsByGuidsAsync(int envCode, IReadOnlyList<string>? consumerGuids, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (consumerGuids == null || consumerGuids.Count == 0) return Array.Empty<long>();

            var ids = new HashSet<long>();
            for (var i = 0; i < consumerGuids.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var guid = consumerGuids[i]?.Trim();
                if (string.IsNullOrWhiteSpace(guid)) continue;

                try {
                    var consumerId = await BlueprintManager.ResolveConsumerIdAsync(envCode, guid, ct);
                    if (consumerId > 0) ids.Add(consumerId);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    FireNotice(LifeCycleNotice.Warn("CONSUMER_RESOLVE_ERROR", "CONSUMER_RESOLVE_ERROR",
                        $"Failed to resolve consumer guid '{guid}' in env={envCode}. Skipping this guid.",
                        new Dictionary<string, object?> {
                            ["envCode"] = envCode,
                            ["consumerGuid"] = guid,
                            ["error"] = ex.Message
                        }));
                }
            }

            if (ids.Count == 0) return Array.Empty<long>();
            var arr = new long[ids.Count];
            ids.CopyTo(arr);
            return arr;
        }
        private static string GetPrimaryInstanceStatus(uint flags) {
            if ((flags & (uint)LifeCycleInstanceFlag.Archived) != 0) return "Archived";
            if ((flags & (uint)LifeCycleInstanceFlag.Failed) != 0) return "Failed";
            if ((flags & (uint)LifeCycleInstanceFlag.Completed) != 0) return "Completed";
            if ((flags & (uint)LifeCycleInstanceFlag.Suspended) != 0) return "Suspended";
            if ((flags & (uint)LifeCycleInstanceFlag.Active) != 0) return "Active";
            return "None";
        }

        async Task<DbRow?> ResolveInstanceRowByKeyAsync(LifeCycleInstanceKey key, DbExecutionLoad load) {
            if (key == null) throw new ArgumentNullException(nameof(key));

            if (!string.IsNullOrWhiteSpace(key.InstanceGuid))
                return await _dal.Instance.GetByGuidAsync(key.InstanceGuid, load);

            if (string.IsNullOrWhiteSpace(key.DefName))  throw new ArgumentException("LifeCycleInstanceKey requires InstanceGuid OR (EnvCode + DefName + EntityId).", nameof(key));
            if (string.IsNullOrWhiteSpace(key.EntityId)) throw new ArgumentException("LifeCycleInstanceKey requires InstanceGuid OR (EnvCode + DefName + EntityId).", nameof(key));

            // Use envCode + defName — the only unambiguous combination.
            var defVersionRow = await _dal.Blueprint.GetLatestDefVersionByEnvCodeAndDefNameAsync(key.EnvCode, key.DefName, load);
            if (defVersionRow == null) return null;
            var defId = defVersionRow.GetLong(KEY_PARENT);
            if (defId <= 0) return null;

            return await _dal.Instance.GetByDefIdAndEntityIdAsync(defId, key.EntityId, load);
        }

        // Iterates the event list and fires each one. Called after the DB transaction commits.
        async Task DispatchEventsSafeAsync(IReadOnlyList<ILifeCycleEvent> events, CancellationToken ct) {
            for (var i = 0; i < events.Count; i++) { ct.ThrowIfCancellationRequested(); FireEvent(events[i]); }
        }

        // Fires an event to all EventRaised subscribers. Each subscriber runs as an independent background
        // task (fire-and-forget). If a subscriber throws, it fires an EVENT_HANDLER_ERROR notice — it does
        // NOT propagate back to the caller. The monitor resends if the ACK row stays Pending.
        void FireEvent(ILifeCycleEvent e) {
            var h = EventRaised;
            if (h == null) return;
            foreach (Func<ILifeCycleEvent, Task> sub in h.GetInvocationList()) {
                _ = RunHandlerSafeAsync(() => sub(e));
            }
        }

        // Fires a notice to all NoticeRaised subscribers. Errors are swallowed (swallow=true) to prevent
        // an infinite loop where a broken notice handler causes another notice, which causes another...
        void FireNotice(LifeCycleNotice n) {
            var h = NoticeRaised;
            if (h == null) return;
            foreach (Func<LifeCycleNotice, Task> sub in h.GetInvocationList()) {
                _ = RunHandlerSafeAsync(() => sub(n), swallow: true);
            }
        }

        async Task RunHandlerSafeAsync(Func<Task> work, bool swallow = false) {
            try {
                await work().ConfigureAwait(false);
            } catch (Exception ex) {
                if (swallow) return;
                try {
                    FireNotice(LifeCycleNotice.Error("EVENT_HANDLER_ERROR", "EVENT_HANDLER_ERROR", ex.Message, ex));
                } catch { }
            }
        }

        // These delegate directly to BlueprintImporter — thin pass-through so the caller never needs to
        // know about internal sub-components. BlueprintImporter is idempotent (hash-guarded): re-importing
        // the same JSON is safe and a no-op if nothing changed. Call these every startup to stay in sync.
        public Task<long> ImportDefinitionJsonAsync(int envCode, string envDisplayName, string definitionJson, CancellationToken ct = default)=> BlueprintImporter.ImportDefinitionJsonAsync(envCode, envDisplayName, definitionJson, ct);

        public Task<long> ImportPolicyJsonAsync(int envCode, string envDisplayName, string policyJson, CancellationToken ct = default)=> BlueprintImporter.ImportPolicyJsonAsync(envCode , envDisplayName, policyJson, ct);

        // Ensures the environment row exists in the DB. Called by the consumer library at startup to
        // make sure envCode+displayName is registered before anything else runs.
        public Task<int> RegisterEnvironmentAsync(int envCode, string? envDisplayName, CancellationToken ct) => BlueprintManager.EnsureEnvironmentAsync(envCode, envDisplayName, new DbExecutionLoad(ct));
    }
}






