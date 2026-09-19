using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using static Haley.Internal.KeyConstants;

namespace Haley.Services.Orchestrators {
    internal sealed class AckOutcomeOrchestrator {
        private readonly IWorkFlowDAL _dal;
        private readonly IAckManager _ackManager;
        private readonly IBlueprintManager _blueprintManager;
        private readonly IPolicyEnforcer _policyEnforcer;
        private readonly WorkFlowEngineOptions _opt;
        private readonly Func<IReadOnlyList<ILifeCycleEvent>, CancellationToken, Task> _dispatchEventsAsync;
        private readonly Func<LifeCycleTriggerRequest, CancellationToken, Task<LifeCycleTriggerResult>> _triggerAsync;
        private readonly Action<LifeCycleNotice> _fireNotice;

        public AckOutcomeOrchestrator(IWorkFlowDAL dal, IAckManager ackManager, IBlueprintManager blueprintManager,
            IPolicyEnforcer policyEnforcer, WorkFlowEngineOptions opt,
            Func<IReadOnlyList<ILifeCycleEvent>, CancellationToken, Task> dispatchEventsAsync,
            Func<LifeCycleTriggerRequest, CancellationToken, Task<LifeCycleTriggerResult>> triggerAsync,
            Action<LifeCycleNotice> fireNotice) {
            _dal = dal;
            _ackManager = ackManager;
            _blueprintManager = blueprintManager;
            _policyEnforcer = policyEnforcer;
            _opt = opt;
            _dispatchEventsAsync = dispatchEventsAsync;
            _triggerAsync = triggerAsync;
            _fireNotice = fireNotice;
        }

        public async Task AckAsync(long consumerId, string ackGuid, AckOutcome outcome, string? message = null,
            DateTimeOffset? retryAt = null, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (consumerId <= 0) throw new ArgumentOutOfRangeException(nameof(consumerId));
            if (string.IsNullOrWhiteSpace(ackGuid)) throw new ArgumentNullException(nameof(ackGuid));
            var readLoad = new DbExecutionLoad(ct);
            var lcId = await _dal.Execution.GetLifecycleByAckAsync(ackGuid, readLoad)
                ?? throw new InvalidOperationException("ACK does not belong to a lifecycle.");
            var context = await _dal.LifeCycle.GetContextByLcIdAsync(lcId, readLoad)
                ?? throw new InvalidOperationException("ACK lifecycle is missing.");
            bool applied;
            var transaction = _dal.CreateNewTransaction();
            using (transaction.Begin(false)) {
                var load = new DbExecutionLoad(ct, transaction);
                try {
                    // All ACK and trigger writers take the instance lock first.
                    await _dal.Execution.LockInstanceAsync(context.GetLong(KEY_INSTANCE_ID), load);
                    applied = await _ackManager.AckAsync(consumerId, ackGuid, outcome, message, retryAt, load);
                    var acknowledged = await _dal.AckConsumer.GetByAckGuidAndConsumerAsync(ackGuid, consumerId, load);
                    if (acknowledged?.GetInt(KEY_STATUS) == (int)AckStatus.Processed) {
                        var hook = await _dal.Hook.GetContextByAckGuidAsync(ackGuid, load);
                        if (hook?.GetInt(KEY_ACK_MODE) == 1)
                            await _dal.AckConsumer.MarkAllProcessedByAckIdAsync(hook.GetLong(KEY_ACK_ID), load);
                    }
                    await _dal.Execution.EnsureAsync(lcId, load);
                    transaction.Commit();
                } catch {
                    transaction.Rollback();
                    throw;
                }
            }
            // A crash here is recoverable: lc_execution and terminal ACKs are durable.
            await ReconcileAsync(lcId, ct);
            if (applied && outcome == AckOutcome.Processed) await NotifyGroupCompletionAsync(ackGuid, ct);
        }

        private async Task NotifyGroupCompletionAsync(string ackGuid, CancellationToken ct) {
            var load = new DbExecutionLoad(ct);
            try {
                var ctx = await _dal.HookGroup.GetContextByAckGuidAsync(ackGuid, load);
                if (ctx != null) {
                    var pending = await _dal.HookGroup.CountUnresolvedInGroupAsync(
                        ctx.GetLong(KEY_INSTANCE_ID), ctx.GetLong(KEY_STATE_ID), ctx.GetLong(KEY_VIA_EVENT),
                        ctx.GetBool(KEY_ON_ENTRY), ctx.GetLong(KEY_LC_ID), ctx.GetLong(KEY_GROUP_ID), load);
                    if (pending == 0) {
                        var groupName = ctx.GetString(KEY_GROUP_NAME) ?? string.Empty;
                        var instanceGuid = ctx.GetString(KEY_INSTANCE_GUID) ?? string.Empty;
                        _fireNotice(LifeCycleNotice.Info("HOOK_GROUP_COMPLETE", "HOOK_GROUP_COMPLETE",
                            $"All hooks in group '{groupName}' are processed. instance={instanceGuid}",
                            new Dictionary<string, object?> { ["groupName"] = groupName, ["instanceGuid"] = instanceGuid }));
                    }
                }
            } catch (Exception ex) {
                _fireNotice(LifeCycleNotice.Warn("HOOK_GROUP_CHECK_ERROR", "HOOK_GROUP_CHECK_ERROR",
                    $"Group completion check failed for ack={ackGuid}: {ex.Message}"));
            }
        }

        public async Task AbandonEffectHookAsync(long ackId, long consumerId, string ackGuid,
            string instanceGuid, string hookRoute, CancellationToken ct = default) {
            await AckAsync(consumerId, ackGuid, AckOutcome.Failed, "Effect execution deadline exceeded.", ct: ct);
            _fireNotice(LifeCycleNotice.Warn("EFFECT_HOOK_ABANDONED", "EFFECT_HOOK_ABANDONED",
                $"Effect execution deadline exceeded. instance={instanceGuid} route={hookRoute} ack={ackGuid}"));
        }

        public async Task RecoverAsync(CancellationToken ct = default) {
            long afterId = 0;
            var take = Math.Max(1, _opt.MonitorPageSize);
            while (!ct.IsCancellationRequested) {
                var rows = await _dal.Execution.ListPendingAsync(afterId, take, new DbExecutionLoad(ct));
                if (rows.Count == 0) return;
                foreach (var row in rows) {
                    afterId = row.GetLong(KEY_LC_ID);
                    try { await ReconcileAsync(afterId, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) {
                        _fireNotice(LifeCycleNotice.Error("EXECUTION_RECOVERY_ERROR", "EXECUTION_RECOVERY_ERROR",
                            "Lifecycle progression remains pending and will be retried.", ex));
                    }
                }
            }
        }

        private async Task ReconcileAsync(long lcId, CancellationToken ct) {
            var context = await _dal.LifeCycle.GetContextByLcIdAsync(lcId, new DbExecutionLoad(ct));
            if (context == null) return;
            var toDispatch = new List<ILifeCycleEvent>();
            int? failureEvent = null;
            var transaction = _dal.CreateNewTransaction();
            using (transaction.Begin(false)) {
                var load = new DbExecutionLoad(ct, transaction);
                try {
                    var instance = await _dal.Execution.LockInstanceAsync(context.GetLong(KEY_INSTANCE_ID), load);
                    var execution = await _dal.Execution.GetAsync(lcId, load);
                    if (instance == null || execution == null) { transaction.Commit(); return; }
                    var flags = (uint)instance.GetLong(KEY_FLAGS);
                    if ((flags & (uint)(LifeCycleInstanceFlag.Suspended | LifeCycleInstanceFlag.Failed | LifeCycleInstanceFlag.Archived)) != 0) {
                        transaction.Commit();
                        return;
                    }
                    var status = execution.GetInt(KEY_STATUS);
                    if (status == 1 || status == 3) { transaction.Commit(); return; }
                    var latest = await _dal.LifeCycle.GetLastByInstanceAsync(context.GetLong(KEY_INSTANCE_ID), load);
                    if (latest?.GetLong(KEY_ID) != lcId) {
                        await _dal.Execution.SetAsync(lcId, 1, null, load);
                        await _dal.HookLc.SkipUndispatchedByLcIdAsync(lcId, load);
                    } else if (status == 2) {
                        failureEvent = execution.GetNullableInt("next_event");
                    } else {
                        failureEvent = await AdvanceAsync(context, toDispatch, load);
                    }
                    transaction.Commit();
                } catch {
                    transaction.Rollback();
                    throw;
                }
            }
            await _dispatchEventsAsync(toDispatch, ct);
            foreach (var order in toDispatch.OfType<ILifeCycleHookEvent>().GroupBy(h => h.OrderSeq))
                _fireNotice(LifeCycleNotice.Info("HOOK_ORDER_ADVANCED", "HOOK_ORDER_ADVANCED",
                    $"Hook phase dispatched. order={order.Key} instance={context.GetString(KEY_INSTANCE_GUID)}",
                    new Dictionary<string, object?> {
                        ["orderSeq"] = order.Key, ["instanceGuid"] = context.GetString(KEY_INSTANCE_GUID),
                        ["containsGate"] = order.Any(h => h.HookType == HookType.Gate)
                    }));
            var complete = toDispatch.OfType<ILifeCycleCompleteEvent>().FirstOrDefault();
            if (complete != null)
                _fireNotice(LifeCycleNotice.Info("COMPLETE_DISPATCHED", "COMPLETE_DISPATCHED",
                    $"Complete handoff dispatched. instance={context.GetString(KEY_INSTANCE_GUID)}",
                    new Dictionary<string, object?> {
                        ["instanceGuid"] = context.GetString(KEY_INSTANCE_GUID), ["nextEvent"] = complete.NextEvent
                    }));
            if (failureEvent.HasValue) {
                var result = await _triggerAsync(new LifeCycleTriggerRequest {
                    InstanceGuid = context.GetString(KEY_INSTANCE_GUID) ?? string.Empty,
                    Event = failureEvent.Value.ToString(),
                    Actor = "haleyflow.engine.ack",
                    RequestId = $"failure:{lcId}",
                    ExpectedLifeCycleId = lcId,
                    SkipAckGate = true
                }, ct);
                if (!result.Applied && result.Reason != "StaleContinuation")
                    throw new InvalidOperationException($"Failure continuation remains pending: {result.Reason}");
                await _dal.Execution.SetAsync(lcId, 1, null, new DbExecutionLoad(ct));
            }
        }

        private async Task<int?> AdvanceAsync(DbRow context, List<ILifeCycleEvent> toDispatch, DbExecutionLoad load) {
            var lcId = context.GetLong(KEY_LC_ID);
            var instanceId = context.GetLong(KEY_INSTANCE_ID);
            var validation = await _dal.Execution.ListValidationAcksAsync(lcId, load);
            if (validation.Count == 0) throw new InvalidOperationException("Lifecycle has no validation consumers.");
            var bp = await _blueprintManager.GetBlueprintByVersionIdAsync(context.GetLong(KEY_DEF_VERSION_ID), load.Ct);
            var policy = await _policyEnforcer.ResolvePolicyByIdAsync(context.GetLong(KEY_POLICY_ID), load);
            bp.StatesById.TryGetValue(context.GetLong(KEY_STATE_ID), out var state);
            bp.EventsById.TryGetValue(context.GetLong(KEY_VIA_EVENT), out var via);
            var rule = state == null ? new RuleContext() :
                _policyEnforcer.ResolveRuleContextFromJson(policy.PolicyJson ?? string.Empty, state, via, load.Ct, policy.PolicyId ?? 0);
            if (validation.Any(r => r.GetInt(KEY_STATUS) == (int)AckStatus.Failed))
                return await FailAsync(context, ParseEvent(rule.OnFailureEvent), load);
            if (validation.Any(r => r.GetInt(KEY_STATUS) != (int)AckStatus.Processed)) return null;

            var hooks = await _dal.Execution.ListHooksAsync(lcId, load);
            if (hooks.Count == 0) {
                await _dal.Execution.SetAsync(lcId, 1, null, load);
                return null; // NormalRun continuation belongs to the consumer outbox.
            }

            HookContext ResolveHook(DbRow hook) => state == null ? new HookContext() :
                _policyEnforcer.ResolveHookContextFromJson(policy.PolicyJson ?? string.Empty, state, via,
                    hook.GetString(KEY_ROUTE) ?? string.Empty, load.Ct, policy.PolicyId ?? 0);

            var terminated = false;
            foreach (var order in hooks.GroupBy(h => h.GetInt(KEY_ORDER_SEQ)).OrderBy(g => g.Key)) {
                var gates = order.Where(h => h.GetInt(KEY_HOOK_TYPE) == (int)HookType.Gate
                    && h.GetInt("hook_status") != 2 && !terminated).ToList();
                var failed = gates.FirstOrDefault(HookAckBarrier.HasFailed);
                if (failed != null) {
                    var failure = ParseEvent(ResolveHook(failed).OnFailureEvent) ?? ParseEvent(rule.OnFailureEvent);
                    return await FailAsync(context, failure, load);
                }
                var queuedGates = gates.Where(h => !h.GetBool("dispatched")).ToList();
                if (queuedGates.Count > 0) {
                    await DispatchHooksAsync(context, queuedGates, ResolveHook, toDispatch, load);
                    return null;
                }
                if (gates.Any(h => !HookAckBarrier.HasSucceeded(h))) return null;
                foreach (var gate in gates) {
                    var next = ParseEvent(ResolveHook(gate).OnSuccessEvent);
                    if (!next.HasValue) continue;
                    await _dal.Hook.SkipUndispatchedGateHooksAsync(instanceId, context.GetLong(KEY_STATE_ID),
                        context.GetLong(KEY_VIA_EVENT), true, lcId, load);
                    await _dal.Hook.SkipUndispatchedNonAlwaysEffectsAfterOrderAsync(instanceId, context.GetLong(KEY_STATE_ID),
                        context.GetLong(KEY_VIA_EVENT), true, lcId, order.Key, load);
                    await _dal.LcNext.InsertAsync(lcId, next.Value, gate.GetLong(KEY_ACK_ID), load);
                    // Same-order effects remain reachable; later orders retain only send=always effects.
                    terminated = true;
                    break;
                }
                var effects = order.Where(h => h.GetInt(KEY_HOOK_TYPE) == (int)HookType.Effect
                    && h.GetInt("hook_status") != 2).ToList();
                // The persistent skip operation above affects later orders, which this snapshot still contains.
                if (terminated && gates.Count == 0)
                    effects = effects.Where(h => h.GetInt("send_mode") == 1).ToList();
                var queuedEffects = effects.Where(h => !h.GetBool("dispatched")).ToList();
                if (queuedEffects.Count > 0) {
                    await DispatchHooksAsync(context, queuedEffects, ResolveHook, toDispatch, load);
                    return null;
                }
                if (effects.Any(h => !HookAckBarrier.IsTerminal(h))) return null;
            }

            var nextEvent = await _dal.LcNext.GetNextEventByLcIdAsync(lcId, load) ?? ParseEvent(rule.OnSuccessEvent) ?? 0;
            var existing = await _dal.LcNext.GetDispatchedAckIdByLcIdAsync(lcId, load);
            if (!existing.HasValue) {
                var consumers = validation.Select(r => r.GetLong(KEY_CONSUMER)).Distinct().ToList();
                await _dal.LcNext.InsertAsync(lcId, nextEvent, null, load);
                var ack = await _ackManager.CreateCompleteAckAsync(lcId, consumers, (int)AckStatus.Pending, load);
                foreach (var consumer in consumers)
                    toDispatch.Add(new LifeCycleCompleteEvent {
                        ConsumerId = consumer, InstanceGuid = context.GetString(KEY_INSTANCE_GUID) ?? string.Empty,
                        DefinitionId = context.GetLong(KEY_DEFINITION_ID), DefinitionVersionId = bp.DefVersionId,
                        EntityId = context.GetString(KEY_ENTITY_ID) ?? string.Empty, Metadata = context.GetString(KEY_METADATA),
                        OccurredAt = DateTimeOffset.UtcNow, AckGuid = ack.AckGuid, LifeCycleId = lcId,
                        HooksSucceeded = true, NextEvent = nextEvent
                    });
            }
            await _dal.Execution.SetAsync(lcId, 1, null, load);
            return null;
        }

        private async Task<int?> FailAsync(DbRow context, int? failureEvent, DbExecutionLoad load) {
            var lcId = context.GetLong(KEY_LC_ID);
            await _dal.Hook.CancelPendingHookAckConsumersAsync(context.GetLong(KEY_INSTANCE_ID), lcId, load);
            await _dal.HookLc.SkipUndispatchedByLcIdAsync(lcId, load);
            await _dal.Execution.SetAsync(lcId, failureEvent.HasValue ? 2 : 3, failureEvent, load);
            if (!failureEvent.HasValue)
                _fireNotice(LifeCycleNotice.Warn("EXECUTION_FAILED_NO_ROUTE", "EXECUTION_FAILED_NO_ROUTE",
                    $"Execution failed without a failure route. instance={context.GetString(KEY_INSTANCE_GUID)}"));
            return failureEvent;
        }

        private async Task DispatchHooksAsync(DbRow context, IReadOnlyList<DbRow> hooks,
            Func<DbRow, HookContext> resolve, List<ILifeCycleEvent> toDispatch, DbExecutionLoad load) {
            var consumers = InternalUtils.NormalizeConsumers(
                await _ackManager.GetHookConsumersAsync(context.GetLong(KEY_DEF_VERSION_ID), load.Ct));
            if (consumers.Count == 0) throw new InvalidOperationException("Hook plan has no hook consumers.");
            foreach (var hook in hooks) {
                var hookLcId = hook.GetLong(KEY_HOOK_LC_ID);
                var ack = await _ackManager.CreateHookAckAsync(hookLcId, consumers, (int)AckStatus.Pending, load);
                await _dal.HookLc.MarkDispatchedAsync(hookLcId, load);
                var runCount = await _dal.HookLc.CountDispatchedByHookIdAsync(hook.GetLong(KEY_ID), load);
                var hookContext = resolve(hook);
                foreach (var consumer in consumers)
                    toDispatch.Add(HookEventFactory.Create(context, hook, hookContext, consumer, ack.AckGuid,
                        hook.GetDateTimeOffset("hook_created") ?? DateTimeOffset.UtcNow, runCount));
            }
        }

        private static int? ParseEvent(string? value) => int.TryParse(value, out var code) && code > 0 ? code : null;
    }
}
