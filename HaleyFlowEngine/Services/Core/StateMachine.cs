using Haley.Abstractions;
using Haley.Enums;
using Haley.Internal;
using static Haley.Internal.KeyConstants;
using Haley.Models;
using Haley.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Services {
    // StateMachine is responsible for two things:
    //   1. EnsureInstanceAsync — create-or-retrieve the instance row for an entity.
    //      "Instance" is one running occurrence of a workflow definition for a specific entity (e.g. loan #42).
    //   2. ApplyTransitionAsync — move the instance from one state to another.
    //      Validates the event, checks the transition table, applies a CAS update, and writes the lc_data row.
    internal sealed class StateMachine : IStateMachine {
        private readonly IWorkFlowDAL _dal;
        private readonly IBlueprintManager _bp;

        public StateMachine(IWorkFlowDAL dal, IBlueprintManager bp) { _dal = dal ?? throw new ArgumentNullException(nameof(dal)); _bp = bp ?? throw new ArgumentNullException(nameof(bp)); }

        // Creates the instance row on first call (with initial state + metadata), or returns the
        // existing row if it already exists. Idempotent — UNIQUE(def_version_id, entity_id) in the DB.
        // Metadata is set here and never changed afterwards — it's the immutable instance-level string.
        // The initial state is taken from the blueprint (the state marked IsInitial=true).
        public async Task<DbRow> EnsureInstanceAsync(long defVersionId, string entityId, long policyId, string? metadata, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentNullException(nameof(entityId));

            var bp = await _bp.GetBlueprintByVersionIdAsync(defVersionId, load.Ct);
            var initStateId = bp.InitialStateId;

            var normalizedMetadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata.Trim();
            var guid = await _dal.Instance.UpsertByKeyReturnGuidAsync(defVersionId, entityId, initStateId, null, policyId, (uint)LifeCycleInstanceFlag.Active, normalizedMetadata, load);
            if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("Instance upsert failed (guid null).");

            var row = await _dal.Instance.GetByGuidAsync(guid, load);
            if (row == null) throw new InvalidOperationException("Instance row missing after upsert.");
            return row;
        }

        // Attempts to move the instance to a new state by firing an event.
        // Returns a result with Applied=false (and a Reason) if the transition cannot proceed:
        //   UnknownEvent      — event name/code not found in this blueprint version
        //   InvalidTransition — no transition defined from current state + this event
        //   ConcurrencyConflict — another process moved the state between our read and our update (CAS miss)
        //
        // The CAS (Compare-And-Swap) on current_state is the concurrency guard:
        //   UPDATE instance SET current_state=@new WHERE id=@id AND current_state=@expected
        // If another trigger fires concurrently and changes the state, our update returns 0 rows affected
        // and we return ConcurrencyConflict rather than writing a corrupt lifecycle entry.
        //
        // On success, writes a lifecycle row (lc_data) recording the transition permanently.
        // The actor and payload are written to lc_data_ext — audit trail only, not forwarded to consumers.
        public async Task<ApplyTransitionResult> ApplyTransitionAsync(LifeCycleBlueprint bp, DbRow instance, string eventName, string? actor, IReadOnlyDictionary<string, object?>? payload, DateTimeOffset? occurredAt = null, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            if (bp == null) throw new ArgumentNullException(nameof(bp));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentNullException(nameof(eventName));

            var res = new ApplyTransitionResult { Applied = false, EventName = string.Empty, Reason = string.Empty };

            var instanceId = instance.GetLong(KEY_ID);
            var fromStateId = instance.GetLong(KEY_CURRENT_STATE);
            var ev = ResolveEvent(bp, eventName);

            if (ev == null) { res.Reason = "UnknownEvent"; res.ToStateId = fromStateId; return res; }

            res.FromStateId = fromStateId;
            res.EventId = ev?.Id ?? 0;
            res.EventCode = ev?.Code ?? 0;
            res.EventName = ev?.Name ?? string.Empty;

            if (!bp.Transitions.TryGetValue(Tuple.Create(fromStateId, ev.Id), out var t)) {
                res.Reason = "InvalidTransition";
                res.ToStateId = fromStateId;
                return res;
            }

            res.ToStateId = t.ToStateId;


            var cas = await _dal.Instance.UpdateCurrentStateCasAsync(instanceId, fromStateId, res.ToStateId, ev.Id, load);
            if (cas != 1) { res.Reason = "ConcurrencyConflict"; return res; }

            var lcId = await _dal.LifeCycle.InsertAsync(instanceId, fromStateId, res.ToStateId, ev.Id, occurredAt?.UtcDateTime, load);
            res.Applied = true;
            res.LifeCycleId = lcId;

            // Instance lifecycle flag alignment:
            // - entering a final state => Completed ON, Active OFF
            // - entering a non-final state => Active ON, Completed OFF (supports reopen flows)
            if (bp.StatesById.TryGetValue(res.ToStateId, out var toState)) {
                var isFinalState = (toState.Flags & (uint)LifeCycleStateFlag.IsFinal) != 0;
                if (isFinalState) {
                    await _dal.Instance.AddFlagsAsync(instanceId, (uint)LifeCycleInstanceFlag.Completed, load);
                    await _dal.Instance.RemoveFlagsAsync(instanceId, (uint)LifeCycleInstanceFlag.Active, load);
                } else {
                    await _dal.Instance.AddFlagsAsync(instanceId, (uint)LifeCycleInstanceFlag.Active, load);
                    await _dal.Instance.RemoveFlagsAsync(instanceId, (uint)LifeCycleInstanceFlag.Completed, load);
                }
            }

            var payloadJson = payload != null && payload.Count > 0 ? JsonSerializer.Serialize(payload) : null;
            await _dal.LifeCycleData.UpsertAsync(lcId, actor, payloadJson, load);

            return res;
        }

        public Task<int> SetInstanceMessageAsync(long instanceId, string? message, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return _dal.Instance.SetMessageAsync(instanceId, message, load);
        }

        public Task<int> ClearInstanceMessageAsync(long instanceId, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return _dal.Instance.ClearMessageAsync(instanceId, load);
        }

        public Task<int> SetInstanceFlagsWithMessageAsync(long instanceId, uint flagsToSet, string? message, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            // caller decides whether flags represent Suspended/Failed/Completed/Archive
            return _dal.Instance.SuspendWithMessageAsync(instanceId, flagsToSet, message, load); // or route to the specific DAL method you added
        }

        public Task<int> UnsetInstanceFlagsAsync(long instanceId, uint flagsToClear, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return _dal.Instance.UnsetFlagsAsync(instanceId, flagsToClear, load);
        }


        // Accepts event as either a string name ("submit") or a numeric code ("100").
        // Events may be referred to by name in application code for readability but by code in policy JSON
        // for stability (names can change, codes should not). Both are supported here.
        private static EventDef? ResolveEvent(LifeCycleBlueprint bp, string eventNameOrCode) {
            if (string.IsNullOrWhiteSpace(eventNameOrCode)) return null;
            var s = eventNameOrCode.Trim();
            if (int.TryParse(s, out var code) && bp.EventsByCode.TryGetValue(code, out var byCode)) return byCode;
            var key = s.ToLowerInvariant();
            if (bp.EventsByName.TryGetValue(key, out var byName)) return byName;
            return null;
        }
    }

}


