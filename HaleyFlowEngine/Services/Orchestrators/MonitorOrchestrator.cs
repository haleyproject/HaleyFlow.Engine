using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using System.Collections.Concurrent;
using static Haley.Internal.KeyConstants;

namespace Haley.Services.Orchestrators {
    // Executes monitor duties each tick, in order:
    //
    // A) STALE SCAN  (RaiseOverDueDefaultStateStaleNoticesAsync)
    //    Fires STATE_STALE notices for instances stuck in a state with no open ACKs and NO policy
    //    timeout rule. States that DO have a timeout rule are excluded — they are owned by job C.
    //    Read-only: no DB mutations. In-memory throttle prevents flooding on every tick.
    //
    // B) ACK RE-DISPATCH  (ResendDispatchKindAsync, per consumer)
    //    Resends overdue Pending and Delivered ACK rows. Handles down-consumer backoff (pushes
    //    next_due forward without burning trigger_count). Suspends instances when a blocking hook
    //    exhausts MaxRetryCount.
    //
    // C) POLICY TIMEOUT PROCESSING
    //    States with a timeouts policy rule are handled exclusively here; the stale scan skips them.
    //
    //    Case A — timeout_event IS set:
    // Trigger with a durable request receipt before recording the timeout audit marker.
    //
    //    Case B — no timeout_event (advisory escalation):
    //      Fires STATE_TIMEOUT_EXCEEDED notices on repeat schedule using DefaultStateStaleDuration
    //      as the repeat cadence (policy duration = initial grace period only).
    //      At max_retry → marks instance Failed + fires STATE_TIMEOUT_FAILED.
    //      Notice codes: TIMEOUT_FIRED (Case A), STATE_TIMEOUT_EXCEEDED (Case B), STATE_TIMEOUT_FAILED (Case B).
    internal sealed class MonitorOrchestrator {
        private readonly IWorkFlowDAL _dal;
        private readonly WorkFlowEngineOptions _opt;
        private readonly IAckManager _ackManager;
        private readonly Action<ILifeCycleEvent> _fireEvent;
        private readonly Action<LifeCycleNotice> _fireNotice;
        private readonly Func<LifeCycleTriggerRequest, CancellationToken, Task<LifeCycleTriggerResult>> _triggerAsync;
        // Called when an effect hook exceeds EffectTimeoutSeconds — abandons it and advances ordering.
        private readonly Func<long, long, string, string, string, CancellationToken, Task> _abandonEffectHookAsync;

        // In-memory throttle so stale-state notices do not flood every monitor tick.
        private readonly ConcurrentDictionary<string, DateTimeOffset> _overDueLastAt = new(StringComparer.Ordinal);

        // fireEvent and fireNotice delegate to WorkFlowEngine's subscriber pipeline.
        public MonitorOrchestrator(IWorkFlowDAL dal, WorkFlowEngineOptions opt, IAckManager ackManager, Action<ILifeCycleEvent> fireEvent, Action<LifeCycleNotice> fireNotice, Func<LifeCycleTriggerRequest, CancellationToken, Task<LifeCycleTriggerResult>> triggerAsync, Func<long, long, string, string, string, CancellationToken, Task> abandonEffectHookAsync) {
            _dal = dal ?? throw new ArgumentNullException(nameof(dal));
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
            _ackManager = ackManager ?? throw new ArgumentNullException(nameof(ackManager));
            _fireEvent = fireEvent ?? throw new ArgumentNullException(nameof(fireEvent));
            _fireNotice = fireNotice ?? throw new ArgumentNullException(nameof(fireNotice));
            _triggerAsync = triggerAsync ?? throw new ArgumentNullException(nameof(triggerAsync));
            _abandonEffectHookAsync = abandonEffectHookAsync ?? throw new ArgumentNullException(nameof(abandonEffectHookAsync));
        }

        // Single-consumer resend pass. Handles both:
        // - Pending (never delivered)
        // - Delivered (delivered but not ACKed yet)
        public async Task RunMonitorOnceAsync(long consumerId, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            await ResendDispatchKindAsync(consumerId, (int)AckStatus.Pending, ct);
            await ResendDispatchKindAsync(consumerId, (int)AckStatus.Delivered, ct);
        }

        // Full monitor tick runs three jobs in order (see class-level comment for full design):
        //   A) Stale scan — STATE_STALE notices for states with no timeout rule
        //   B) Per-consumer ACK re-dispatch — resend overdue Pending/Delivered ACK rows
        //   C) Policy timeout processing — Case A auto-transitions + Case B advisory escalation
        public async Task RunMonitorOnceInternalAsync(Func<LifeCycleConsumerType, CancellationToken, Task<IReadOnlyList<long>>> consumersProvider, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();

            // A) Stale scan — read-only, fires STATE_STALE notices
            await RaiseOverDueDefaultStateStaleNoticesAsync(ct);

            // B) ACK re-dispatch — per consumer
            var consumerList = await consumersProvider(LifeCycleConsumerType.Monitor, ct);
            for (var i = 0; i < consumerList.Count; i++) {
                var consumerId = consumerList[i];
                await RunMonitorOnceAsync(consumerId, ct);
            }

            // C) Policy timeout processing
            await ProcessCaseATimeoutsAsync(ct);
            await ProcessCaseBTimeoutsAsync(ct);
        }

        // Case A: timeout_event IS set — engine auto-transitions the instance.
        // Trigger with a durable request receipt before recording the timeout audit marker.
        private async Task ProcessCaseATimeoutsAsync(CancellationToken ct) {
            var excluded = (uint)(LifeCycleInstanceFlag.Suspended | LifeCycleInstanceFlag.Completed | LifeCycleInstanceFlag.Failed | LifeCycleInstanceFlag.Archived);
            var take = _opt.MonitorPageSize > 0 ? _opt.MonitorPageSize : 200;
            var skip = 0;

            while (!ct.IsCancellationRequested) {
                var rows = await _dal.LcTimeout.ListDueCaseAPagedAsync(excluded, skip, take, new DbExecutionLoad(ct));
                if (rows.Count == 0) break;

                for (var i = 0; i < rows.Count; i++) {
                    ct.ThrowIfCancellationRequested();
                    var row = rows[i];

                    var instanceGuid = row.GetString(KEY_INSTANCE_GUID) ?? string.Empty;
                    var lcId         = row.GetLong("entry_lc_id");
                    var eventCode    = row.GetInt("timeout_event_code");
                    var policyMaxRetry = row.GetInt("timeout_max_retry");

                    if (eventCode <= 0 || string.IsNullOrWhiteSpace(instanceGuid)) continue;

                    // Trigger with a durable request receipt before recording the timeout audit marker.
                    try {
                        var result = await _triggerAsync(new LifeCycleTriggerRequest {
                            InstanceGuid = instanceGuid,
                            Event        = eventCode.ToString(),
                            Actor        = "engine.monitor",
                            SkipAckGate  = true,
                            RequestId = $"timeout:{lcId}",
                            ExpectedLifeCycleId = lcId
                        }, ct);
                        if (!result.Applied && result.Reason != "StaleContinuation") {
                            _fireNotice(LifeCycleNotice.Warn("TIMEOUT_PENDING", "TIMEOUT_PENDING",
                                $"Timeout remains pending. instance={instanceGuid} reason={result.Reason}"));
                            continue;
                        }
                        await _dal.LcTimeout.InsertCaseAAsync(lcId, policyMaxRetry > 0 ? policyMaxRetry : (int?)null, new DbExecutionLoad(ct));
                        _fireNotice(LifeCycleNotice.Info("TIMEOUT_FIRED", "TIMEOUT_FIRED",
                            $"Policy timeout fired. instance={instanceGuid} event={eventCode}"));
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        _fireNotice(LifeCycleNotice.Error("TIMEOUT_ERROR", "TIMEOUT_ERROR",
                            $"Policy timeout trigger failed. instance={instanceGuid} event={eventCode}: {ex.Message}", ex));
                    }
                }

                if (rows.Count < take) break;
                skip += take;
            }
        }

        // Case B: no timeout_event — fires STATE_TIMEOUT_EXCEEDED advisory notices on a schedule.
        // Policy duration = initial grace period. DefaultStateStaleDuration = repeat cadence.
        // If trigger_count >= max_retry: marks instance Failed + fires STATE_TIMEOUT_FAILED.
        private async Task ProcessCaseBTimeoutsAsync(CancellationToken ct) {
            var dur = _opt.DefaultStateStaleDuration;
            var staleSeconds = dur > TimeSpan.Zero ? (int)Math.Max(1, dur.TotalSeconds) : 86400;
            var excluded = (uint)(LifeCycleInstanceFlag.Suspended | LifeCycleInstanceFlag.Completed | LifeCycleInstanceFlag.Failed | LifeCycleInstanceFlag.Archived);
            var take = _opt.MonitorPageSize > 0 ? _opt.MonitorPageSize : 200;
            var skip = 0;

            while (!ct.IsCancellationRequested) {
                var rows = await _dal.LcTimeout.ListDueCaseBPagedAsync(excluded, skip, take, new DbExecutionLoad(ct));
                if (rows.Count == 0) break;

                for (var i = 0; i < rows.Count; i++) {
                    ct.ThrowIfCancellationRequested();
                    var row = rows[i];

                    var instanceGuid   = row.GetString(KEY_INSTANCE_GUID) ?? string.Empty;
                    var entityId       = row.GetString(KEY_ENTITY_ID)     ?? string.Empty;
                    var instanceId     = row.GetLong(KEY_INSTANCE_ID);
                    var lcId           = row.GetLong("entry_lc_id");
                    var stateName      = row.GetString(KEY_STATE_NAME)    ?? string.Empty;
                    var currentCount   = row.GetInt(KEY_TRIGGER_COUNT);   // 0 = first occurrence
                    var policyMaxRetry = row.GetInt("timeout_max_retry");

                    if (string.IsNullOrWhiteSpace(instanceGuid)) continue;

                    var effectiveMax = policyMaxRetry > 0 ? policyMaxRetry
                                     : (_opt.MaxRetryCount > 0 ? _opt.MaxRetryCount : 0);
                    var newCount = currentCount + 1;

                    // Persist scheduling state before firing notice (idempotent on first occurrence).
                    if (currentCount == 0) {
                        await _dal.LcTimeout.InsertCaseBFirstAsync(lcId, policyMaxRetry > 0 ? policyMaxRetry : (int?)null, staleSeconds, new DbExecutionLoad(ct));
                    } else {
                        await _dal.LcTimeout.UpdateCaseBNextAsync(lcId, staleSeconds, new DbExecutionLoad(ct));
                    }

                    // Retry limit check: fail instance and stop.
                    if (effectiveMax > 0 && newCount >= effectiveMax) {
                        var failMsg = $"Policy timeout limit reached (max={effectiveMax}). instance={instanceGuid} state={stateName}";
                        await _dal.Instance.SuspendWithMessageAsync(instanceId, (uint)(LifeCycleInstanceFlag.Failed | LifeCycleInstanceFlag.Suspended), failMsg, new DbExecutionLoad(ct));
                        _fireNotice(LifeCycleNotice.Warn("STATE_TIMEOUT_FAILED", "STATE_TIMEOUT_FAILED", failMsg,
                            new Dictionary<string, object?> { ["instanceGuid"] = instanceGuid, ["entityId"] = entityId, ["stateName"] = stateName, ["triggerCount"] = newCount }));
                        continue;
                    }

                    _fireNotice(new LifeCycleNotice(LifeCycleNoticeKind.OverDue, "STATE_TIMEOUT_EXCEEDED", "STATE_TIMEOUT_EXCEEDED",
                        $"Policy state timeout exceeded. instance={instanceGuid} state={stateName} count={newCount}", null,
                        new Dictionary<string, object?> { ["instanceGuid"] = instanceGuid, ["entityId"] = entityId, ["stateName"] = stateName, ["triggerCount"] = newCount }));
                }

                if (rows.Count < take) break;
                skip += take;
            }
        }

        // Resend for one status class (Pending or Delivered) for one consumer.
        // Includes down-consumer postponement and max-retry handling.
        private async Task ResendDispatchKindAsync(long consumerId, int ackStatus, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            var load = new DbExecutionLoad(ct);
            var nowUtc = DateTime.UtcNow;
            var nextDueUtc = ackStatus == (int)AckStatus.Pending ? nowUtc.Add(_opt.AckPendingResendAfter) : nowUtc.Add(_opt.AckDeliveredResendAfter);
            var ttlSeconds = _opt.ConsumerTtlSeconds > 0 ? _opt.ConsumerTtlSeconds : 30;
            var recheckSeconds = _opt.ConsumerDownRecheckSeconds;

            // If consumer heartbeat is stale, push due rows forward instead of repeatedly sending now.
            await _dal.AckConsumer.PushNextDueForDownAsync(consumerId, ackStatus, ttlSeconds, recheckSeconds, load);

            // Lifecycle dispatch retries.
            var lc = await _ackManager.ListDueLifecycleDispatchAsync(consumerId, ackStatus, ttlSeconds, 0, _opt.MonitorPageSize, load);
            for (var i = 0; i < lc.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var item = lc[i];

                if (item.MaxTrigger > 0 && item.TriggerCount >= item.MaxTrigger) {
                    // Per-row retry budget exhausted: mark failed and suspend instance.
                    await _dal.AckConsumer.SetStatusAndDueAsync(item.AckId, item.ConsumerId, (int)AckStatus.Failed, null, null, load);

                    var instanceId = await _dal.Instance.GetIdByGuidAsync(item.Event.InstanceGuid, load) ?? 0;
                    if (instanceId > 0) {
                        var msg = $"Suspended: ack max retries exceeded (max={item.MaxTrigger}) kind=lifecycle status={ackStatus} ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}";
                        await _dal.Instance.SuspendWithMessageAsync(instanceId, (uint)LifeCycleInstanceFlag.Suspended, msg, load);
                        _fireNotice(LifeCycleNotice.Warn("ACK_SUSPEND", "ACK_SUSPEND", msg));
                    } else {
                        _fireNotice(LifeCycleNotice.Warn("ACK_FAIL", "ACK_FAIL", $"Ack marked failed (max retries) but instance not found. kind=lifecycle ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}"));
                    }

                    continue;
                }

                // Still retryable: bump trigger counter/next_due and re-dispatch.
                await _dal.AckConsumer.MarkTriggerAsync(item.AckId, item.ConsumerId, nextDueUtc, load);
                _fireNotice(LifeCycleNotice.Warn("ACK_RETRY", "ACK_RETRY", $"kind=lifecycle status={ackStatus} ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}"));
                _fireEvent(item.Event);
            }

            // Hook dispatch retries. Gate and effect failure handling differs.
            var effectTimeoutSec = _opt.EffectTimeoutSeconds > 0 ? _opt.EffectTimeoutSeconds : 60;
            var hk = await _ackManager.ListDueHookDispatchAsync(consumerId, ackStatus, ttlSeconds, 0, _opt.MonitorPageSize, load);
            for (var i = 0; i < hk.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var item = hk[i];

                var isGate = item.Event is ILifeCycleHookEvent hev && hev.HookType == HookType.Gate;
                var hookRoute = item.Event is ILifeCycleHookEvent hr ? hr.Route : string.Empty;

                // Effect hook timeout: single fixed window — no retries after EffectTimeoutSeconds.
                if (!isGate) {
                    var elapsedSec = (DateTime.UtcNow - item.CreatedAt).TotalSeconds;
                    if (elapsedSec >= effectTimeoutSec) {
                        // Time window expired — abandon and advance hook ordering.
                        try {
                            await _abandonEffectHookAsync(item.AckId, item.ConsumerId, item.AckGuid, item.Event.InstanceGuid, hookRoute, ct);
                        } catch (OperationCanceledException) {
                            throw;
                        } catch (Exception ex) {
                            _fireNotice(LifeCycleNotice.Warn("EFFECT_HOOK_ABANDON_ERROR", "EFFECT_HOOK_ABANDON_ERROR",
                                $"Effect hook abandon failed. ack={item.AckGuid}: {ex.Message}"));
                        }
                        continue;
                    }
                    // Effect hook still within window — resend.
                    await _dal.AckConsumer.MarkTriggerAsync(item.AckId, item.ConsumerId, nextDueUtc, load);
                    _fireNotice(LifeCycleNotice.Warn("ACK_RETRY", "ACK_RETRY", $"kind=hook(effect) status={ackStatus} ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}"));
                    _fireEvent(item.Event);
                    continue;
                }

                // Gate hook max-retry exhaustion: suspend instance.
                if (item.MaxTrigger > 0 && item.TriggerCount >= item.MaxTrigger) {
                    await _dal.AckConsumer.SetStatusAndDueAsync(item.AckId, item.ConsumerId, (int)AckStatus.Failed, null, null, load);
                    var instanceId = await _dal.Instance.GetIdByGuidAsync(item.Event.InstanceGuid, load) ?? 0;
                    if (instanceId > 0) {
                        var msg = $"Suspended: gate hook max retries exceeded (max={item.MaxTrigger}) ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}";
                        await _dal.Instance.SuspendWithMessageAsync(instanceId, (uint)LifeCycleInstanceFlag.Suspended, msg, load);
                        _fireNotice(LifeCycleNotice.Warn("ACK_SUSPEND", "ACK_SUSPEND", msg));
                    } else {
                        _fireNotice(LifeCycleNotice.Warn("ACK_FAIL", "ACK_FAIL", $"Gate hook failed (max retries) but instance not found. ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}"));
                    }
                    continue;
                }

                // Gate hook still retryable: bump trigger counter and re-dispatch.
                await _dal.AckConsumer.MarkTriggerAsync(item.AckId, item.ConsumerId, nextDueUtc, load);
                _fireNotice(LifeCycleNotice.Warn("ACK_RETRY", "ACK_RETRY", $"kind=hook(gate) status={ackStatus} ack={item.AckGuid} consumer={item.ConsumerId} instance={item.Event.InstanceGuid}"));
                _fireEvent(item.Event);
            }
        }

        // Detects instances staying in default state longer than allowed duration and emits notices.
        // This is read-only monitoring; no instance state mutation happens here.
        private async Task RaiseOverDueDefaultStateStaleNoticesAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            var dur = _opt.DefaultStateStaleDuration;
            if (dur <= TimeSpan.Zero) return;

            var staleSeconds = (int)Math.Max(1, dur.TotalSeconds);
            var processed = (int)AckStatus.Processed;
            var excluded = (uint)(LifeCycleInstanceFlag.Suspended | LifeCycleInstanceFlag.Completed | LifeCycleInstanceFlag.Failed | LifeCycleInstanceFlag.Archived);

            var take = _opt.MonitorPageSize > 0 ? _opt.MonitorPageSize : 200;
            var skip = 0;
            var now = DateTimeOffset.UtcNow;

            var resolver = _opt.ResolveConsumers;
            var consumerCache = new Dictionary<long, IReadOnlyList<long>>();

            // Safety valve for long-running processes.
            if (_overDueLastAt.Count > 200_000) _overDueLastAt.Clear();

            while (!ct.IsCancellationRequested) {
                var rows = await _dal.Instance.ListStaleByDefaultStateDurationPagedAsync(staleSeconds, processed, excluded, skip, take, new DbExecutionLoad(ct));
                if (rows.Count == 0) break;

                for (var i = 0; i < rows.Count; i++) {
                    ct.ThrowIfCancellationRequested();

                    var r = rows[i];
                    var instanceId = Convert.ToInt64(r[KEY_INSTANCE_ID]);
                    var instanceGuid = (string)r[KEY_INSTANCE_GUID];
                    var entityId = r[KEY_ENTITY_ID] as string ?? string.Empty;
                    var defVersionId = Convert.ToInt64(r[KEY_DEF_VERSION_ID]);
                    var stateId = Convert.ToInt64(r[KEY_CURRENT_STATE_ID]);
                    var stateName = r[KEY_STATE_NAME] as string ?? string.Empty;
                    var lcId = Convert.ToInt64(r[KEY_LC_ID]);
                    var staleSec = Convert.ToInt64(r[KEY_STALE_SECONDS]);

                    // Resolve transition consumers once per defVersionId per pass.
                    IReadOnlyList<long> consumers;
                    if (resolver == null) {
                        consumers = Array.Empty<long>();
                    } else if (!consumerCache.TryGetValue(defVersionId, out consumers!)) {
                        consumers = InternalUtils.NormalizeConsumers(await resolver(LifeCycleConsumerType.Transition, defVersionId, ct));
                        consumerCache[defVersionId] = consumers;
                    }

                    if (consumers.Count == 0) {
                        // No consumers mapped: emit one generic notice (consumerId=0) with throttling.
                        var k0 = $"0:{instanceId}:{stateId}";
                        if (_overDueLastAt.TryGetValue(k0, out var last0) && (now - last0) < dur) continue;
                        _overDueLastAt[k0] = now;

                        _fireNotice(new LifeCycleNotice(LifeCycleNoticeKind.OverDue, "STATE_STALE", "DEFAULT_STATE_STALE", $"Instance stale (no consumers resolved). instance={instanceGuid} entity={entityId} state={stateName} stale={TimeSpan.FromSeconds(staleSec)}", null, new Dictionary<string, object?> {
                            ["consumerId"] = 0L,
                            ["instanceId"] = instanceId,
                            ["instanceGuid"] = instanceGuid,
                            ["entityId"] = entityId,
                            ["defVersionId"] = defVersionId,
                            ["currentStateId"] = stateId,
                            ["stateName"] = stateName,
                            ["lcId"] = lcId,
                            ["staleSeconds"] = staleSec
                        }));
                        continue;
                    }

                    for (var c = 0; c < consumers.Count; c++) {
                        var consumerId = consumers[c];
                        var key = $"{consumerId}:{instanceId}:{stateId}";
                        // Throttle per consumer-instance-state triple.
                        if (_overDueLastAt.TryGetValue(key, out var last) && (now - last) < dur) continue;
                        _overDueLastAt[key] = now;

                        _fireNotice(new LifeCycleNotice(LifeCycleNoticeKind.OverDue, "STATE_STALE", "DEFAULT_STATE_STALE", $"Instance stale. consumer={consumerId} instance={instanceGuid} entity={entityId} state={stateName} stale={TimeSpan.FromSeconds(staleSec)}", null, new Dictionary<string, object?> {
                            ["consumerId"] = consumerId,
                            ["instanceId"] = instanceId,
                            ["instanceGuid"] = instanceGuid,
                            ["entityId"] = entityId,
                            ["defVersionId"] = defVersionId,
                            ["currentStateId"] = stateId,
                            ["stateName"] = stateName,
                            ["lcId"] = lcId,
                            ["staleSeconds"] = staleSec
                        }));
                    }
                }

                if (rows.Count < take) break;
                skip += take;
            }
        }

    }
}
