using Haley.Abstractions;
using Haley.Enums;
using Haley.Internal;
using static Haley.Internal.KeyConstants;
using Haley.Models;
using Haley.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Services {
    // AckManager owns the full acknowledgement lifecycle for both lifecycle transitions and hooks.
    // It creates ack rows, attaches consumer rows, maps consumer outcomes to status+next_due,
    // and exposes dispatch-listing queries that the monitor uses to find overdue deliveries.
    // AckManager does NOT trigger events or drive workflow progress — it only tracks delivery state.
    // Resend scheduling is implicit: next_due drives the monitor's "is this due?" query. When a
    // consumer ACKs with Retry, the next_due is pushed forward; with Processed/Failed, it goes NULL.
    internal sealed class AckManager : IAckManager {
        private readonly IWorkFlowDAL _dal;

        private readonly Func<LifeCycleConsumerType, long?, CancellationToken, Task<IReadOnlyList<long>>> _consumers;

        // scheduling policy
        private readonly TimeSpan _pendingNextDue;    // T + 40s
        private readonly TimeSpan _deliveredNextDue;  // T + 4m
        private readonly int _maxTrigger;             // initial per-row budget stamped at INSERT time

        //Purpose of the below fields is to resolve policies/hooks from json if needed.
        private readonly IBlueprintManager _bp;
        private readonly IPolicyEnforcer _policy; // concrete (so we can call Resolve*FromJson)

        // Optional notice pipeline — injected by WorkFlowEngine so AckAsync can fire
        // STALE_ACK_RECEIVED when a consumer tries to ACK an already-terminal ack_consumer row.
        private readonly Action<LifeCycleNotice>? _fireNotice;

        public AckManager(IWorkFlowDAL dal, IBlueprintManager bp, IPolicyEnforcer policy, Func<LifeCycleConsumerType, long?, CancellationToken, Task<IReadOnlyList<long>>> consumers = null, TimeSpan? pendingNextDue = null, TimeSpan? deliveredNextDue = null, int maxTrigger = 10, Action<LifeCycleNotice>? fireNotice = null) {
            _dal = dal ?? throw new ArgumentNullException(nameof(dal));
            _consumers = consumers ?? ((dv, iid, ct) => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>()));

            _pendingNextDue = pendingNextDue ?? TimeSpan.FromSeconds(40);
            _deliveredNextDue = deliveredNextDue ?? TimeSpan.FromMinutes(4);
            _maxTrigger = maxTrigger > 0 ? maxTrigger : 10;
            _bp = bp ?? throw new ArgumentNullException(nameof(bp));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _fireNotice = fireNotice;
        }

        // Resolves which consumer IDs should receive transition events for the given def_version.
        // Delegates to the _consumers callback injected at construction — the callback is owned by
        // WorkFlowEngine, which knows how to query the registered consumer list for a definition.
        // AckManager intentionally doesn't query consumers directly; this keeps DAL coupling out of here.
        // The responsibility to maintain the consumer and definition subscription is for HaleyFlow.Hub. Engine should not maintain the subscriptions
        public Task<IReadOnlyList<long>> GetTransitionConsumersAsync(long defVersionId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return _consumers(LifeCycleConsumerType.Transition, defVersionId, ct); }

        // Same as GetTransitionConsumersAsync but scoped to hook-type consumers.
        // Hook consumers receive hook emission events; they may differ from transition consumers
        // if the deployment separates concern (one service for state changes, another for side-effects).
        public Task<IReadOnlyList<long>> GetHookConsumersAsync(long defVersionId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return _consumers(LifeCycleConsumerType.Hook, defVersionId, ct); }

        // Creates (or retrieves) an ack record for a lifecycle transition and attaches consumers to it.
        // If an ack already exists for this lifecycle row, we skip creating a new one and only insert
        // any missing consumers via EnsureConsumersInsertOnly — we never reschedule existing consumers,
        // because that would reset their next_due and effectively re-deliver an already-in-progress event.
        public async Task<IWorkFlowAckRef> CreateLifecycleAckAsync(long lifecycleId, IReadOnlyList<long> consumerIds, int initialAckStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (lifecycleId <= 0) throw new ArgumentOutOfRangeException(nameof(lifecycleId));
            return await CreateAckAsync(consumerIds, initialAckStatus, load, getExistingAckIdAsync: () => _dal.LcAck.GetAckIdByLcIdAsync(lifecycleId, load), attachAsync: ackId => _dal.LcAck.AttachAsync(ackId, lifecycleId, load));
        }

        // Complete events reuse the same ack/ack_consumer delivery model, but their lifecycle mapping
        // is stored in lcn_ack (not lc_ack). This keeps the original transition ack and the later
        // completion ack separate for the same lifecycle row.
        public async Task<IWorkFlowAckRef> CreateCompleteAckAsync(long lifecycleId, IReadOnlyList<long> consumerIds, int initialAckStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (lifecycleId <= 0) throw new ArgumentOutOfRangeException(nameof(lifecycleId));
            return await CreateAckAsync(
                consumerIds,
                initialAckStatus,
                load,
                getExistingAckIdAsync: () => _dal.LcNext.GetDispatchedAckIdByLcIdAsync(lifecycleId, load),
                attachAsync: ackId => _dal.LcNext.MarkDispatchedAsync(lifecycleId, ackId, load));
        }

        // Same pattern as CreateLifecycleAckAsync but for hook_lc rows (one ack per hook per lifecycle entry).
        // hookLcId is hook_lc.id — hook_ack.hook_id FKs to hook_lc.id in the new schema.
        // Idempotent on the ack itself; only inserts missing consumer rows.
        public async Task<IWorkFlowAckRef> CreateHookAckAsync(long hookLcId, IReadOnlyList<long> consumerIds, int initialAckStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (hookLcId <= 0) throw new ArgumentOutOfRangeException(nameof(hookLcId));
            return await CreateAckAsync(consumerIds, initialAckStatus, load, getExistingAckIdAsync: () => _dal.HookAck.GetAckIdByHookLcIdAsync(hookLcId, load), attachAsync: ackId => _dal.HookAck.AttachAsync(ackId, hookLcId, load));
        }

        private async Task<IWorkFlowAckRef> CreateAckAsync(IReadOnlyList<long> consumerIds, int initialAckStatus, DbExecutionLoad load, Func<Task<long?>> getExistingAckIdAsync, Func<long, Task<int>> attachAsync) {
            var existingAckId = await getExistingAckIdAsync();
            if (existingAckId.HasValue && existingAckId.Value > 0) {
                // IMPORTANT: do NOT reschedule existing consumers; only insert missing ones.
                await EnsureConsumersInsertOnlyAsync(existingAckId.Value, consumerIds, initialAckStatus, load);
                return await GetAckRefByIdAsync(existingAckId.Value, load);
            }

            var ack = await _dal.Ack.InsertReturnRowAsync(load);
            if (ack == null) throw new InvalidOperationException("Ack insert failed.");

            var ackId = ack.GetLong(KEY_ID);
            var ackGuid = ack.GetString(KEY_GUID);
            if (ackId <= 0 || string.IsNullOrWhiteSpace(ackGuid)) throw new InvalidOperationException("Ack insert failed (id/guid missing).");

            await attachAsync(ackId);
            await EnsureConsumersInsertOnlyAsync(ackId, consumerIds, initialAckStatus, load);

            return new WorkFlowAckRef { AckId = ackId, AckGuid = ackGuid! };
        }

        // Records the consumer's acknowledgement outcome for a specific ack_guid.
        // Outcome maps to status + next_due:
        //   Delivered → status=Delivered, next_due=now+deliveredWindow (monitor will re-fire if not processed)
        //   Processed → status=Processed, next_due=NULL    (terminal — no further dispatch)
        //   Failed    → status=Failed,    next_due=NULL    (terminal — consumer gave up)
        //   Retry     → status=Pending,   next_due=retryAt or now+pendingWindow (consumer requests retry)
        //   (default) → status=Pending,   next_due=now+pendingWindow
        // Only the consumer's own row is updated — keyed by (ackGuid, consumerId). Other consumers
        // tracking the same ack are unaffected.
        public async Task<bool> AckAsync(long consumerId, string ackGuid, AckOutcome outcome, string? message = null, DateTimeOffset? retryAt = null, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (consumerId <= 0) throw new ArgumentOutOfRangeException(nameof(consumerId));
            if (string.IsNullOrWhiteSpace(ackGuid)) throw new ArgumentNullException(nameof(ackGuid));

            // Reject ACKs for already-terminal ack_consumer rows.
            // Terminal statuses: Processed=3, Failed=4, Cancelled=5.
            // Cancelled happens when the engine forcibly closes hook ACKs before a timeout transition.
            // Accepting a late ACK from a consumer would reopen a closed row and could trigger
            // spurious hook-group completion or post-ACK logic on an instance that has already moved on.
            var existing = await _dal.AckConsumer.GetByAckGuidAndConsumerAsync(ackGuid, consumerId, load);
            if (existing != null) {
                var currentStatus = existing.GetInt(KEY_STATUS);
                if (currentStatus >= (int)AckStatus.Processed) {
                    // Row is terminal — reject the ACK and emit a notice so operators can observe late consumers.
                    _fireNotice?.Invoke(LifeCycleNotice.Warn("STALE_ACK_RECEIVED", "STALE_ACK_RECEIVED",
                        $"ACK rejected: ack_consumer is already terminal. ack_guid={ackGuid} consumer={consumerId} current_status={currentStatus} attempted_outcome={outcome}",
                        new Dictionary<string, object?> {
                            ["ackGuid"]         = ackGuid,
                            ["consumerId"]      = consumerId,
                            ["currentStatus"]   = currentStatus,
                            ["attemptedOutcome"] = outcome.ToString()
                        }));
                    return false;
                }
            }

            // Decide status + due scheduling
            var (status, nextDueUtc) = ComputeOutcomeStatusAndDue(outcome, retryAt);

            // Single DB call; no ackId fetch needed.
            var affected = await _dal.AckConsumer.SetStatusAndDueByGuidAsync(ackGuid, consumerId, (int)status, nextDueUtc, message, load);
            if (affected <= 0) throw new InvalidOperationException($"AckConsumer not found. guid={ackGuid}, consumer={consumerId}");
            return true;

        }

        public async Task MarkRetryAsync(long ackId, long consumerId, DateTimeOffset? retryAt = null, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (ackId <= 0) throw new ArgumentOutOfRangeException(nameof(ackId));
            if (consumerId <= 0) throw new ArgumentOutOfRangeException(nameof(consumerId));

            var nextDueUtc = (retryAt?.UtcDateTime) ?? (DateTime.UtcNow + _pendingNextDue);
            await _dal.AckConsumer.SetStatusAndDueAsync(ackId, consumerId, (int)AckStatus.Pending, nextDueUtc, null, load);
        }

        public Task SetStatusAsync(long ackId, long consumerId, int ackStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();

            // NOTE: status-only is dangerous now; caller must decide due semantics.
            // Keep it for compatibility but do NOT touch next_due (pass NULL only when moving to terminal states).
            DateTime? nextDueUtc = null;
            if (ackStatus == (int)AckStatus.Pending) nextDueUtc = DateTime.UtcNow + _pendingNextDue;
            else if (ackStatus == (int)AckStatus.Delivered) nextDueUtc = DateTime.UtcNow + _deliveredNextDue;
            else nextDueUtc = null;

            return _dal.AckConsumer.SetStatusAndDueAsync(ackId, consumerId, ackStatus, nextDueUtc, null, load);
        }

        // Returns lifecycle transition events that are "due" for a given consumer.
        // "Due" means: ack_status matches the requested status AND next_due <= NOW AND
        // the consumer process is considered alive (within ttlSeconds of its last heartbeat).
        // The monitor calls this periodically and re-fires events found here.
        // Policy params and completion events are resolved from the stored policy JSON at list time —
        // so the consumer always gets the policy snapshot that was active when the instance was created.
        public async Task<IReadOnlyList<ILifeCycleDispatchItem>> ListDueLifecycleDispatchAsync(long consumerId, int ackStatus, int ttlSeconds, int skip, int take, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            var lifecycleRows = await _dal.AckDispatch.ListDueLifecyclePagedAsync(consumerId, ackStatus, ttlSeconds, skip, take, load);
            var completeRows = await _dal.AckDispatch.ListDueCompletePagedAsync(consumerId, ackStatus, ttlSeconds, skip, take, load);
            var list = new List<ILifeCycleDispatchItem>(lifecycleRows.Count + completeRows.Count);

            foreach (var r in lifecycleRows) {
                load.Ct.ThrowIfCancellationRequested();
                var evt = new LifeCycleTransitionEvent {
                    DefinitionId = r.GetLong(KEY_DEF_ID),
                    DefinitionVersionId = r.GetLong(KEY_DEF_VERSION_ID),
                    ConsumerId = r.GetLong(KEY_CONSUMER),
                    InstanceGuid = r.GetString(KEY_INSTANCE_GUID),
                    EntityId = r.GetString(KEY_ENTITY_ID) ?? string.Empty,
                    OccurredAt = r.GetDateTimeOffset(KEY_LC_CREATED) ?? DateTimeOffset.UtcNow,
                    AckGuid = r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    Metadata = r.GetString(KEY_METADATA),
                    LifeCycleId = r.GetLong(KEY_LC_ID),
                    FromStateId = r.GetLong(KEY_FROM_STATE),
                    ToStateId = r.GetLong(KEY_TO_STATE),
                    EventCode = r.GetNullableInt(KEY_EVENT_CODE) ?? 0,
                    EventName = r.GetString(KEY_EVENT_NAME) ?? string.Empty,
                    DispatchMode = (TransitionDispatchMode)(r.GetNullableInt(KEY_DISPATCH_MODE) ?? 0),
                    PrevStateMeta = null
                };

                var policyJson = r.GetString(KEY_POLICY_JSON);
                if (!string.IsNullOrWhiteSpace(policyJson) && evt.DefinitionVersionId > 0) {
                    var bp = await _bp.GetBlueprintByVersionIdAsync(evt.DefinitionVersionId, load.Ct);

                    // best: use event_id (already in query) so no ambiguity
                    var eventId = r.GetLong(KEY_EVENT_ID);
                    bp.EventsById.TryGetValue(eventId, out var viaEvent);

                    if (bp.StatesById.TryGetValue(evt.ToStateId, out var toState)) {
                        var ctx = _policy.ResolveRuleContextFromJson(policyJson!, toState, viaEvent, load.Ct);
                        evt.Params = ctx.Params;
                        evt.OnSuccessEvent = ctx.OnSuccessEvent;
                        evt.OnFailureEvent = ctx.OnFailureEvent;
                    }
                }

                list.Add(new LifeCycleDispatchItem {
                    Kind = LifeCycleEventKind.Transition,
                    AckId = r.GetLong(KEY_ACK_ID),
                    AckGuid = r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    ConsumerId = r.GetLong(KEY_CONSUMER),
                    AckStatus = r.GetInt(KEY_STATUS),
                    TriggerCount = r.GetInt(KEY_TRIGGER_COUNT),
                    MaxTrigger = r.GetInt(KEY_MAX_TRIGGER),
                    LastTrigger = r.GetDateTime(KEY_LAST_TRIGGER) ?? DateTime.UtcNow,
                    CreatedAt = r.GetDateTime("ack_created") ?? throw new InvalidOperationException("ACK creation timestamp is missing."),
                    NextDue = r.GetDateTime(KEY_NEXT_DUE),
                    Event = evt
                });
            }

            foreach (var r in completeRows) {
                load.Ct.ThrowIfCancellationRequested();
                var evt = new LifeCycleCompleteEvent {
                    DefinitionId = r.GetLong(KEY_DEF_ID),
                    DefinitionVersionId = r.GetLong(KEY_DEF_VERSION_ID),
                    ConsumerId = r.GetLong(KEY_CONSUMER),
                    InstanceGuid = r.GetString(KEY_INSTANCE_GUID),
                    EntityId = r.GetString(KEY_ENTITY_ID) ?? string.Empty,
                    OccurredAt = r.GetDateTimeOffset(KEY_LC_CREATED) ?? DateTimeOffset.UtcNow,
                    AckGuid = r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    Metadata = r.GetString(KEY_METADATA),
                    LifeCycleId = r.GetLong(KEY_LC_ID),
                    HooksSucceeded = (r.GetNullableInt(KEY_HOOKS_SUCCEEDED) ?? 0) == 1,
                    NextEvent = r.GetNullableInt(KEY_NEXT_EVENT) ?? 0
                };

                list.Add(new LifeCycleDispatchItem {
                    Kind = LifeCycleEventKind.Complete,
                    AckId = r.GetLong(KEY_ACK_ID),
                    AckGuid = r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    ConsumerId = r.GetLong(KEY_CONSUMER),
                    AckStatus = r.GetInt(KEY_STATUS),
                    TriggerCount = r.GetInt(KEY_TRIGGER_COUNT),
                    MaxTrigger = r.GetInt(KEY_MAX_TRIGGER),
                    LastTrigger = r.GetDateTime(KEY_LAST_TRIGGER) ?? DateTime.UtcNow,
                    CreatedAt = r.GetDateTime("ack_created") ?? throw new InvalidOperationException("ACK creation timestamp is missing."),
                    NextDue = r.GetDateTime(KEY_NEXT_DUE),
                    Event = evt
                });
            }

            return list
                .OrderBy(x => x.NextDue ?? DateTime.MaxValue)
                .ThenBy(x => x.AckId)
                .ThenBy(x => x.ConsumerId)
                .Take(take)
                .ToList();
        }

        // Same as ListDueLifecycleDispatchAsync but for hook events.
        // Hook dispatch items carry the route, blocking flag, group, and timing (notBefore/deadline)
        // resolved from the policy JSON. The consumer uses these to decide how to execute the hook.
        public async Task<IReadOnlyList<ILifeCycleDispatchItem>> ListDueHookDispatchAsync(long consumerId, int ackStatus, int ttlSeconds, int skip, int take, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            var rows = await _dal.AckDispatch.ListDueHookPagedAsync(consumerId, ackStatus, ttlSeconds, skip, take, load);
            var list = new List<ILifeCycleDispatchItem>(rows.Count);
            foreach (var r in rows) {
                load.Ct.ThrowIfCancellationRequested();
                var hctx = new HookContext();
                var policyJson = r.GetString(KEY_POLICY_JSON);
                var defVersionId = r.GetLong(KEY_DEF_VERSION_ID);
                if (!string.IsNullOrWhiteSpace(policyJson) && defVersionId > 0) {
                    var bp = await _bp.GetBlueprintByVersionIdAsync(defVersionId, load.Ct);
                    bp.EventsById.TryGetValue(r.GetLong(KEY_VIA_EVENT), out var viaEvent);
                    if (bp.StatesById.TryGetValue(r.GetLong(KEY_STATE_ID), out var toState))
                        hctx = _policy.ResolveHookContextFromJson(policyJson, toState, viaEvent,
                            r.GetString(KEY_ROUTE) ?? string.Empty, load.Ct);
                }
                var evt = HookEventFactory.Create(r, r, hctx, r.GetLong(KEY_CONSUMER),
                    r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    r.GetDateTimeOffset(KEY_HOOK_CREATED) ?? DateTimeOffset.UtcNow, r.GetInt(KEY_RUN_COUNT));

                list.Add(new LifeCycleDispatchItem {
                    Kind = LifeCycleEventKind.Hook,
                    AckId = r.GetLong(KEY_ACK_ID),
                    AckGuid = r.GetString(KEY_ACK_GUID) ?? string.Empty,
                    ConsumerId = r.GetLong(KEY_CONSUMER),
                    AckStatus = r.GetInt(KEY_STATUS),
                    TriggerCount = r.GetInt(KEY_TRIGGER_COUNT),
                    MaxTrigger = r.GetInt(KEY_MAX_TRIGGER),
                    LastTrigger = r.GetDateTime(KEY_LAST_TRIGGER) ?? DateTime.UtcNow,
                    CreatedAt = r.GetDateTime("ack_created") ?? throw new InvalidOperationException("ACK creation timestamp is missing."),
                    NextDue = r.GetDateTime(KEY_NEXT_DUE),
                    Event = evt
                });
            }
            return list;
        }

        public async Task<int> CountDueLifecycleDispatchAsync(int ackStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            var lifecycle = (await _dal.AckDispatch.CountDueLifecycleAsync(ackStatus, load)) ?? 0;
            var complete = (await _dal.AckDispatch.CountDueCompleteAsync(ackStatus, load)) ?? 0;
            return lifecycle + complete;
        }

        public async Task<int> CountDueHookDispatchAsync(int ackStatus, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return (await _dal.AckDispatch.CountDueHookAsync(ackStatus, load)) ?? 0;
        }

        private (AckStatus status, DateTime? nextDueUtc) ComputeOutcomeStatusAndDue(AckOutcome outcome, DateTimeOffset? retryAt) {
            // Use next_due to drive monitor. Terminal states => next_due NULL.
            // Pending/Delivered => schedule next_due (either retryAt or defaults).
            if (outcome == AckOutcome.Delivered)
                return (AckStatus.Delivered, DateTime.UtcNow + _deliveredNextDue);

            if (outcome == AckOutcome.Processed)
                return (AckStatus.Processed, null);

            if (outcome == AckOutcome.Failed)
                return (AckStatus.Failed, null);

            if (outcome == AckOutcome.Retry)
                return (AckStatus.Pending, retryAt?.UtcDateTime ?? (DateTime.UtcNow + _pendingNextDue));

            return (AckStatus.Pending, DateTime.UtcNow + _pendingNextDue);
        }

        private async Task EnsureConsumersInsertOnlyAsync(long ackId, IReadOnlyList<long> consumerIds, int initialAckStatus, DbExecutionLoad load) {
            load.Ct.ThrowIfCancellationRequested();
            var ids = InternalUtils.NormalizeConsumers(consumerIds);
            if (ids.Count == 0) return;

            // Only INSERT missing rows. Do not overwrite existing status/next_due.
            // (This prevents rescheduling acks on every startup or re-attach call.)
            for (var i = 0; i < ids.Count; i++) {
                load.Ct.ThrowIfCancellationRequested();
                var consumerId = ids[i];

                var existing = await _dal.AckConsumer.GetByKeyAsync(ackId, consumerId, load);
                if (existing != null) continue;

                var nextDueUtc = ComputeInitialNextDueUtc(initialAckStatus);
                await _dal.AckConsumer.UpsertByAckIdAndConsumerAsync(ackId, consumerId, initialAckStatus, nextDueUtc, _maxTrigger, load);
            }
        }

        private DateTime? ComputeInitialNextDueUtc(int ackStatus) {
            if (ackStatus == (int)AckStatus.Pending) return DateTime.UtcNow + _pendingNextDue;
            if (ackStatus == (int)AckStatus.Delivered) return DateTime.UtcNow + _deliveredNextDue;
            return null; // Processed/Failed etc.
        }

        private async Task<IWorkFlowAckRef> GetAckRefByIdAsync(long ackId, DbExecutionLoad load) {
            load.Ct.ThrowIfCancellationRequested();
            var row = await _dal.Ack.GetByIdAsync(ackId, load);
            if (row == null) throw new InvalidOperationException($"Ack not found. id={ackId}");

            var guid = row.GetString(KEY_GUID);
            if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException($"Ack guid missing. id={ackId}");

            return new WorkFlowAckRef { AckId = ackId, AckGuid = guid! };
        }

    }
}


