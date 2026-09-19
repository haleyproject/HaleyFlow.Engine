# HaleyFlow Workflow Engine & Consumer — Test Checklist

## Executable regression coverage

The xUnit suite in [WFE.Test](../WFE.Test/README.md) now executes protocol, Engine, Consumer, recovery, migration, and historical-import regressions. Use the disposable MariaDB runner described there for the complete suite. Database tests are skipped explicitly when no test connection is configured.

The remaining checklist is a broader manual and planned scenario matrix. An unchecked item below does not describe the status of the focused automated regressions. See [implemented recovery behavior](PROTOCOL_RECOVERY_CHANGES.md) for the current contract.

> **How to use this checklist**: Each test case has a **Scenario**, **Input** (setup / trigger), and **Expected outcome**. Work through these when building a production application or regression suite. Check off cases as you verify them.

---

## 1. Relay Execution Contract — Transition Handler and Hook Sequencing

### 1.1 Transition Handler Routing (Tier 1: Policy JSON, Tier 2: Code Override, Tier 3: Callback)

**TC-R01** Handler registered with Tier 3 callback
- Input: `On(eventCode, handler)` with Tier 3 callback that returns next event code
- Expected: Callback result overrides policy codes; transition advances per callback decision

**TC-R02** Handler registered with Tier 2 override (no Tier 3)
- Input: `On(eventCode, handler, successCode=101, failureCode=102)` with policy codes also defined
- Expected: Code overrides (101/102) used instead of policy codes

**TC-R03** Handler registered with Tier 1 only (policy JSON codes)
- Input: Transition with policy-defined `onSuccess=201`, `onFailure=202`; no code-side override
- Expected: Policy codes (201/202) resolved and fired correctly

### 1.2 Handler Execution Success Path

**TC-R04** Transition handler returns true (success)
- Input: Handler returning success; transition has gate and effect hooks
- Expected: State advances to ToState; hooks evaluated in order; success code fires if defined

**TC-R05** Transition handler returns false (failure)
- Input: Handler returning false with `failureCode` defined
- Expected: Transition fails; failureCode fired immediately; all hooks skipped; state reset to FromState

### 1.3 No Handler Registered for Event

**TC-R06** Event fired with no transition handler registered
- Input: `NextAsync` called for `eventCode` with no `On()` handler registered
- Expected: Transition applies; hooks fire normally; completion code from policy fires if available

### 1.4 Monitor Intercept

**TC-R07** Monitor callback returns false (blocking)
- Input: `SetMonitor()` returns false for a specific event/entity pair
- Expected: Handler not called; `NextAsync` returns Blocked; current state unchanged

**TC-R08** Monitor callback throws exception
- Input: Monitor callback throws during evaluation
- Expected: Exception propagated; execution stops; current state unchanged

---

## 2. Engine Trigger Tests — Instance Creation and State Transition

### 2.1 New Instance Creation

**TC-E01** First trigger for a new entity+definition pair
- Input: `TriggerAsync` with new `entityId`, definition name, event code
- Expected: Instance created; stamped with latest `policy_id`; lifecycle entry + ACK created; initial state applied

**TC-E02** Instance already exists, pinned to older blueprint version
- Input: `TriggerAsync` on existing instance with newer blueprint available
- Expected: Blueprint reloaded to match instance's pinned version; transition applies using pinned version

### 2.2 Terminal Instance Rejection

**TC-E03** Trigger on Completed instance
- Input: `TriggerAsync(instanceGuid, event)` where instance has `LifeCycleInstanceFlag.Completed`
- Expected: Returns `Applied=false` with `Reason="InstanceIsTerminal"`; no state change; no events dispatched

**TC-E04** Trigger on Suspended instance
- Input: `TriggerAsync` on instance with `LifeCycleInstanceFlag.Suspended`
- Expected: Returns `Applied=false` with `Reason="InstanceNotActive"`; no dispatch

**TC-E05** Trigger on instance without Active flag
- Input: Instance exists but Active flag is cleared
- Expected: Trigger blocked with `Applied=false`; no state transition attempted

### 2.3 Duplicate Trigger (Idempotency)

**TC-E06** Same event fired twice in rapid succession
- Input: `TriggerAsync(instance, event=100)` called twice with identical params
- Expected: First call creates lifecycle + ACKs; second call blocked by ACK gate or state validation

---

## 3. Hook Ordering Tests — Batching, Sequencing, Gate/Effect Advancement

### 3.1 Same-OrderSeq Gate Hooks (Batched Evaluation)

**TC-H01** Two gate hooks at `order_seq=1` for same transition
- Input: Hooks: `{route:"gate_a", type:Gate, order:1}`, `{route:"gate_b", type:Gate, order:1}`
- Expected: Both dispatch together; both ACKs must complete before advancing to order 2

**TC-H02** Gate at `order=1` and effect at `order=1`
- Input: Gate hook and effect hook both with `order=1`
- Expected: Both dispatch; effect result ignored; gate controls advancement

### 3.2 Effect Advancement

**TC-H03** Effect hook batch ACK all Processed
- Input: Effect hook order=2; all consumers ACK Processed
- Expected: `AdvanceNextHookOrderAsync` called; next undispatched order found and dispatched

**TC-H04** Effect hook ACK fails/denied
- Input: Effect hook ACK marked Failed
- Expected: Effect abandoned (no retry); ordering still advances; failure does not block progress

### 3.3 Multi-Order Hook Sequencing

**TC-H05** Hooks at order=1 and order=2; only order=1 dispatched initially
- Input: `EmitHooksAsync` returns order=1 hooks; order=2 rows in DB with `dispatched=0`
- Expected: Only order=1 ACKs created; order=2 rows exist but no events fired yet

**TC-H06** Order=1 hooks complete; advance to order=2
- Input: All order=1 ACKs Processed; `AdvanceNextHookOrderAsync` called
- Expected: Query finds order=2 hooks; ACKs created; events dispatched; returns false (more hooks exist)

---

## 4. Gate-Success Drain Contract

### 4.1 Gate Succeeds with OnSuccessEvent

**TC-G01** Gate hook at order=1 succeeds with `CompleteSuccessCode=301`
- Input: Gate hook executes; handler returns true; `CompleteSuccessCode=301`
- Expected: Success code stored in `lc_next`; remaining undispatched gate hooks marked Skipped; effect hooks still fire; after effects drain, `Complete` event dispatched with `NextEvent=301`

**TC-G02** Gate succeeds with code; multiple effects at higher orders
- Input: Gate at order=1 succeeds (code=301); effects at order=2, order=3
- Expected: Effects dispatch in order; only after all effects processed does `Complete` fire

### 4.2 Effect Drain After Gate Success

**TC-G03** Gate succeeds; undispatched effect hooks exist at higher orders
- Input: Gate at order=1 marks success; effects at order=2 and order=3 undispatched
- Expected: `SkipUndispatchedGateHooks` marks remaining gates; effects at order=2 dispatched first; after ACKs, order=3 dispatched; `Complete` queued only after order=3 completes

### 4.3 No Code on Gate Success

**TC-G04** Gate hook succeeds but has no `CompleteSuccessCode`
- Input: Gate returns true; `CompleteSuccessCode=null`
- Expected: Gate continues to next hook normally; no termination; no effect drain early exit

### 4.4 Gate Failure Path

**TC-G05** Gate hook fails with `CompleteFailureCode=302`
- Input: Gate handler returns false; `CompleteFailureCode=302`
- Expected: Failure code fired immediately; all remaining hooks skipped or cancelled; no `Complete` dispatch for the old lifecycle

**TC-G06** Gate hook fails with no failure code
- Input: Gate handler returns false; no failure code defined
- Expected: State rolled back; `NextAsync` returns Blocked; no code fired

---

## 5. ACK Outcome Tests — Processed, Retry, Failed

### 5.1 Lifecycle ACK Outcomes

**TC-A01** Consumer ACKs lifecycle transition with Processed
- Input: `AckAsync(consumerId, lcAckGuid, AckOutcome.Processed)`
- Expected: ACK status updated; dispatch completes; no further hooks triggered

**TC-A02** Lifecycle ACK Retry
- Input: `AckAsync(consumerId, lcAckGuid, AckOutcome.Retry, retryAt=futureTime)`
- Expected: Status set to Retry; `nextDue` updated; monitor will re-dispatch at specified time

**TC-A03** Lifecycle ACK Failed
- Input: `AckAsync(consumerId, lcAckGuid, AckOutcome.Failed, message="error")`
- Expected: Status set to Failed; message persisted; instance may be suspended if retries exhausted

### 5.2 Gate Hook ACK Outcomes

**TC-A04** Gate hook ACK Processed; order fully resolved
- Input: All consumers for gate at order=1 ACK Processed; no incomplete records remain
- Expected: `TrySkipRemainingGatesIfSuccessCodeAsync` called; completion/next-order logic runs; next order dispatched

**TC-A05** Gate hook ACK Processed; order still has pending ACKs
- Input: One of multiple consumers ACKs Processed; another still Pending
- Expected: No advancement; order remains incomplete; gate holds until all ACKs arrive

**TC-A06** Gate hook ACK Retry
- Input: `AckAsync(consumerId, gateAckGuid, AckOutcome.Retry)`
- Expected: Status updated; `nextDue` set for resend; no order advancement; monitor retries delivery

**TC-A07** Gate hook ACK exhausts max retries
- Input: `trigger_count >= max_trigger_count` for gate hook ACK
- Expected: Status set to Failed; instance suspended; HOOK_ACK_SUSPEND notice fired

### 5.3 Effect Hook ACK Outcomes

**TC-A08** Effect hook all consumers ACK Processed
- Input: All effect hook consumers at order=2 ACK Processed
- Expected: completion/next-order logic runs; next undispatched order located and dispatched

**TC-A09** Effect hook ACK timeout (abandoned by monitor)
- Input: Effect hook pending > `EffectTimeoutSeconds`; monitor calls `AbandonEffectHookAsync`
- Expected: `ack_consumer` marked Failed; hook marked Skipped; ordering advanced; no retry; next order dispatched

### 5.4 AckMode=Any Fan-Out

**TC-A10** Multiple hook consumers, `ackMode=Any`, one ACKs Processed
- Input: Hook has `AckMode=Any`; 3 consumers; first consumer ACKs Processed
- Expected: All sibling `ack_consumer` rows marked Processed; ordering checks see order complete; advancement triggered

---

## 6. TransitionDispatchMode Tests

### 6.1 NormalRun (No Hooks)

**TC-D01** Transition has no hooks emitted
- Input: `TriggerAsync` with transition that has no hook rules in policy
- Expected: `DispatchMode=NormalRun` set on event; consumer can fire next event immediately after ACK

**TC-D02** Consumer receives NormalRun event
- Input: Inbox record with `DispatchMode=NormalRun`; `DispatchTransitionAsync` called
- Expected: Business logic runs; `AutoTransitionAsync` allowed; `nextEvent` set for post-ACK fire

### 6.2 ValidationMode (Hooks Present)

**TC-D03** Transition emits hooks during state entry
- Input: `TriggerAsync` with transition that emits gate/effect hooks
- Expected: `DispatchMode=ValidationMode` set on all transition events; consumer receives ValidationMode

**TC-D04** Consumer receives ValidationMode event
- Input: `DispatchMode=ValidationMode`; `DispatchTransitionAsync` called
- Expected: Business logic runs; `AutoTransitionAsync` suppressed (`_nextEvent = null`); consumer waits for the engine's later `Complete` handoff

### 6.3 Complete Event (Engine-Driven Completion Handoff)

**TC-D05** All hooks completed; engine dispatches Complete event
- Input: `AckOutcomeOrchestrator.DispatchCompleteEventAsync` called with `resolvedNextEvent`
- Expected: Consumer receives `Complete`; `OnTransitionCompleteAsync` runs; default path sets `_nextEvent` to `evt.NextEvent`; no transition business logic reruns

**TC-D06** Complete event ACKed
- Input: Consumer ACKs `Complete` event with Processed
- Expected: Engine receives ACK; no further hook advancement for that lifecycle; next event fires if wrapper accepted or resolved one

---

## 7. Monitor Loop Tests

### 7.1 Stale State Detection

**TC-M01** Instance stuck in default state > `DefaultStateStaleDuration`; no ACKs pending; no policy timeout rule
- Input: `state_entry_at < now - DefaultStateStaleDuration`; no open lifecycle ACKs
- Expected: STATE_STALE notice fired (throttled per consumer-instance-state); no state mutation

**TC-M02** Stale notice throttling
- Input: STATE_STALE already fired within `DefaultStateStaleDuration` for same instance-state-consumer
- Expected: Notice suppressed (prevents flood)

**TC-M03** Instance with policy timeout rule excluded from stale scan
- Input: State has `timeout_event` or `timeout_duration` rule
- Expected: Stale scan skips this instance; Case A or B timeout handler owns it

### 7.2 ACK Retry

**TC-M04** Lifecycle ACK Pending longer than `AckPendingResendAfter`
- Input: `ack_consumer.status=Pending`; `created_at < now - AckPendingResendAfter`
- Expected: `trigger_count` incremented; `next_due` updated; event re-dispatched

**TC-M05** Lifecycle ACK retry exhausts max attempts
- Input: `trigger_count >= max_trigger_count`; still Pending
- Expected: Status set to Failed; instance suspended; ACK_SUSPEND notice fired

**TC-M06** Lifecycle ACK Delivered longer than `AckDeliveredResendAfter`
- Input: `ack_consumer.status=Delivered`; `last_trigger < now - AckDeliveredResendAfter`
- Expected: `trigger_count` incremented; `next_due` updated; event re-fired

**TC-M07** Down consumer backoff (stale heartbeat)
- Input: `consumer.last_heartbeat < now - ConsumerTtlSeconds`
- Expected: `PushNextDueForDownAsync` advances `next_due` by `ConsumerDownRecheckSeconds`; event not re-dispatched this tick

### 7.3 Effect Hook Timeout

**TC-M08** Effect hook ACK pending > `EffectTimeoutSeconds`
- Input: Hook is Effect type; elapsed since last trigger >= `EffectTimeoutSeconds`
- Expected: `AbandonEffectHookAsync` called; status set to Failed; completion/next-order logic triggered; next order dispatched

**TC-M09** Effect hook within timeout window
- Input: Effect hook Pending < `EffectTimeoutSeconds`
- Expected: `trigger_count` incremented; `next_due` updated; re-dispatched normally (no retry count limit for effects)

### 7.4 Gate Hook Max Retries

**TC-M10** Gate hook ACK `trigger_count >= max_trigger_count`
- Input: Hook `type=Gate`; `trigger_count >= max_trigger`
- Expected: Status set to Failed; instance suspended; ACK_SUSPEND notice fired

**TC-M11** Gate hook retry still available
- Input: Hook `type=Gate`; `trigger_count < max_trigger`
- Expected: `trigger_count` incremented; `next_due` updated; re-dispatched

### 7.5 Case A Timeout (timeout_event set)

**TC-M12** Policy rule has `timeout_event` and `timeout_duration`; grace period expires
- Input: `MonitorOrchestrator.ProcessCaseATimeoutsAsync`; `timeout_event=401`, `duration=30 mins`
- Expected: Idempotency marker inserted; `TriggerAsync(event=401)` called; blocking hook ACKs cancelled; TIMEOUT_FIRED notice

**TC-M13** Case A timeout crash recovery
- Input: Marker inserted but trigger failed; next monitor tick
- Expected: Query skips already-processed timeout (marker prevents duplicate trigger)

### 7.6 Case B Timeout (advisory, no timeout_event)

**TC-M14** Policy rule has `timeout_duration` but no `timeout_event`; grace period expires first time
- Input: `timeout_duration=1 hour`; `now >= entry_time + 1 hour`; `trigger_count=0`
- Expected: `LcTimeout.InsertCaseBFirstAsync` records state; STATE_TIMEOUT_EXCEEDED notice fired

**TC-M15** Case B timeout repeat notice
- Input: Timeout already recorded (`trigger_count > 0`); repeat cadence exceeded
- Expected: `LcTimeout.UpdateCaseBNextAsync` updates due time; STATE_TIMEOUT_EXCEEDED notice fired again

**TC-M16** Case B max retry exhaustion
- Input: `trigger_count >= max_retry` (policy max or fallback from option)
- Expected: Instance marked Failed + Suspended; STATE_TIMEOUT_FAILED notice; no further timeouts

---

## 8. Consumer Dispatch Tests

### 8.1 Handler Lookup by Version

**TC-C01** Handler registered with `[MinVersion(2)]`; event dispatched with `handler_version=3`
- Input: Method has `MinVersion=2`; event `handler_version=3`
- Expected: `PickBestHandler` selects method; handler executes

**TC-C02** Event dispatched with `handler_version < method MinVersion`
- Input: `handler_version=1`; method requires `MinVersion=2`
- Expected: Method skipped; `OnUnhandledTransitionAsync`/Hook called instead

### 8.2 Handler Not Found

**TC-C03** No wrapper registered for definition_id
- Input: Dispatch item arrives; `_registry.TryGetRegistration` fails
- Expected: `RejectDeliveryAsync` called; `AckAsync(Failed, reason)` sent; notice fired

**TC-C04** Wrapper found but no handler for event code
- Input: Wrapper registered; transition handler for event code not defined
- Expected: `OnUnhandledTransitionAsync` called; handler implements fallback behavior

### 8.3 Instance Mirror Sync

**TC-C05** New event for instance (first time on consumer)
- Input: `ProcessItemAsync` with new `instance_guid`
- Expected: `EnsureInstanceMirrorAsync` fetches instance data from engine; creates mirror record; idempotent via UNIQUE(guid)

**TC-C06** Instance mirror already exists
- Input: Dispatch for existing `instance_guid`
- Expected: Mirror lookup succeeds; `UpsertAsync` returns existing Id; no duplicate created

### 8.4 Handler Version Pinning

**TC-C07** First delivery for instance pins handler version
- Input: Instance never seen before; `inbox.handler_version=null`; instance `def_version=5`
- Expected: `SetHandlerVersionAsync` pins `handler_version=5`; subsequent deliveries use pinned version

**TC-C08** Retry delivery respects pinned version
- Input: Inbox retry; `handler_version` already pinned to 3
- Expected: `DispatchAsync` uses pinned 3; not instance's current `def_version` (may differ)

### 8.5 Step Tracking (Inbox Idempotency)

**TC-C09** Same delivery processed twice (duplicate dispatch)
- Input: `Inbox.UpsertAsync` called with same `ack_guid` twice
- Expected: UNIQUE(ack_guid) constraint prevents duplicate; second upsert returns existing `inbox_id`

**TC-C10** Step counter and attempt tracking
- Input: Same inbox item dispatched 3 times (retry path)
- Expected: `IncrementAttemptAsync` increments for each attempt; audit trail shows 3 attempts

---

## 9. Business Action Tests

**TC-B01** Action already completed for this instance (SkipIfCompleted mode)
- Input: `ExecuteBusinessActionAsync(actionCode=100, mode=SkipIfCompleted)` called twice
- Expected: First call executes; marks Completed; second call detects Completed; returns immediately without re-executing; `AlreadyCompleted=true`

**TC-B02** Action not yet completed
- Input: `ExecuteBusinessActionAsync(actionCode=100)` called; action not in DB
- Expected: Business action row created; action executed; marked Completed

**TC-B03** Action throws exception
- Input: `ExecuteBusinessActionAsync` with action that throws
- Expected: Status set to Failed; message logged; exception re-thrown; caller decides ACK outcome

**TC-B04** Action returns complex object
- Input: `ExecuteBusinessActionAsync` with action returning `{id: 123, status: "done"}`
- Expected: `ResultJson` serialized and stored; persists across retries

**TC-B05** Same handler triggered twice with same `actionCode`
- Input: Same `InstanceId + ActionCode` combination
- Expected: First execution completes; second detects Completed; skipped without re-running

---

## 10. Policy Validation Tests

### 10.1 UNREACHABLE_HOOK

**TC-V01** Gate hook at order=2 follows gate at order=1 that has success code
- Input: `{route:"gate_a", order:1, success_code:301}`, `{route:"gate_b", order:2, type:Gate}`
- Expected: Validation warning UNREACHABLE_HOOK; gate_b unreachable on success path

**TC-V02** Effect hook after gate terminator (should NOT be flagged)
- Input: `{route:"gate_a", order:1, success_code:301}`, `{route:"effect_a", order:2, type:Effect}`
- Expected: Validation passes; effect_a still runs (effects drain after gate success)

### 10.2 NO_FAILURE_PATH

**TC-V03** Gate hook with no failure code; transition has no failure code
- Input: Hook missing `CompleteFailureCode`; transition also missing failure code
- Expected: Validation warning NO_FAILURE_PATH; code-side handler must handle failure

**TC-V04** Gate has failure code
- Input: `hook.CompleteFailureCode=302`
- Expected: Validation passes; failure path available

### 10.3 AMBIGUOUS_ORDER

**TC-V05** Two gate hooks at same order, both with success codes
- Input: `{route:"gate_a", order:1, success_code:301}`, `{route:"gate_b", order:1, success_code:302}`
- Expected: Validation error AMBIGUOUS_ORDER; which code fires is undefined

**TC-V06** Two gates at order=1, only one has success code
- Input: `{route:"gate_a", order:1}`, `{route:"gate_b", order:1, success_code:301}`
- Expected: Validation warning AMBIGUOUS_ORDER (may be intentional but flagged)

### 10.4 EFFECT_WITH_COMPLETE

**TC-V07** Effect hook with success/failure code
- Input: Hook `type=Effect`; `CompleteSuccessCode=301`
- Expected: Validation warning; codes ignored at runtime; effect fires regardless

### 10.5 Structural Errors

**TC-V08** No initial state
- Input: States defined but none marked `is_initial`
- Expected: Validation error NoInitialState; definition rejected

**TC-V09** Multiple initial states
- Input: Two states both marked `is_initial`
- Expected: Validation error MultipleInitialStates; definition rejected

**TC-V10** Transition references non-existent state
- Input: Transition `FromState='Unknown'`; ToState exists
- Expected: Validation error UnknownFromState; definition rejected

**TC-V11** Terminal state with outgoing transitions
- Input: State marked `is_terminal=true`; transition from this state defined
- Expected: Validation error TerminalHasTransitions; definition rejected

**TC-V12** Non-terminal state with no exits
- Input: State `is_terminal=false`; no outgoing transitions
- Expected: Validation error StuckState; workflow can hang indefinitely

**TC-V13** Unreachable state
- Input: State not reachable from initial state via any transition path
- Expected: Validation warning UnreachableState; state never entered

**TC-V14** Duplicate event codes from same state
- Input: Two transitions from `State_A` both with `event_code=100`
- Expected: Validation error DuplicateEventCode; routing ambiguous

**TC-V15** Self-loop only (no exit)
- Input: State has only self-loop transitions; no path to terminal
- Expected: Validation error CircularOnly; infinite loop possible

**TC-V16** Policy rule for non-existent state
- Input: Policy rule `state="UnknownState"`; no such state in definition
- Expected: Validation warning PolicyUnknownState; rule will never fire

---

## 11. Edge Case Resilience Tests

### 11.1 Concurrency

**TC-X01** Two triggers for same instance in rapid succession
- Input: `TriggerAsync` called concurrently for same `instance_guid` with different events
- Expected: First acquires DB lock; second waits; ACK gate prevents conflict; one succeeds, other blocked

**TC-X02** Concurrent ACK processing for same order hooks
- Input: Multiple ACKs for same order hooks arriving simultaneously
- Expected: DB serialization ensures correctness; `AdvanceNextHookOrderAsync` idempotent

### 11.2 Missing ResolveConsumers Callback

**TC-X03** Monitor stale scan; `ResolveConsumers` is null
- Input: `RaiseOverDueDefaultStateStaleNoticesAsync` with resolver=null
- Expected: Generic notice emitted (`consumerId=0`); no per-consumer notices; loop continues

**TC-X04** `ResolveConsumers` throws exception
- Input: Resolver callback throws during scan
- Expected: Handled gracefully; notice skipped; loop continues; does not block entire scan

### 11.3 Relay with No Handlers Registered

**TC-X05** `Relay.NextAsync` called; event has no transition handler registered
- Input: `On(eventCode, handler)` never called for this `eventCode`
- Expected: Handler skipped; hooks still fire; completion code from policy fires if available

**TC-X06** Relay with no hook handlers registered
- Input: No `OnHook()` registrations for routes in policy
- Expected: Hook routes skipped silently; transition continues; effect hooks do not block

### 11.4 Backward Compatibility (blocking → type field)

**TC-X07** Old policy JSON with `"blocking": true/false`; new code expects `"type": "gate"/"effect"`
- Input: `PolicyEnforcer.ReadHookType` reads JSON with `blocking` field
- Expected: Tries `type` field first; falls back to `blocking` for backward compat; `!blocking` maps to Effect

### 11.5 Missing or Null Policy JSON

**TC-X08** Definition imported without policy JSON
- Input: `RelayFromJson(definitionJson, policyJson=null)`
- Expected: Relay runs transitions (handlers execute); no hooks emitted; validation passes (nothing to validate)

**TC-X09** Consumer with no policy for definition
- Input: Policy lookup returns null at trigger time
- Expected: No hooks emitted; `DispatchMode=NormalRun`; consumer can fire next event immediately

### 11.6 Blueprint Version Mismatch

**TC-X10** Instance pinned to version 1; definition now at version 3
- Input: `TriggerAsync`; `instance.def_version=1`; latest blueprint version=3
- Expected: Reload blueprint by `version_id`; use version 1 for transition; applies correctly

**TC-X11** Instance references version_id that no longer exists
- Input: `GetBlueprintByVersionIdAsync(defVersionId)` returns null
- Expected: Error thrown; trigger fails; instance left at current state

### 11.7 Empty Consumer List

**TC-X12** Definition has no registered transition consumers
- Input: `TriggerAsync`; `GetTransitionConsumersAsync` returns empty
- Expected: Validation error thrown; trigger rejected before state change

**TC-X13** Definition has transition consumers but no hook consumers
- Input: Hooks emitted; `hook_consumers` is empty
- Expected: Validation error when trying to dispatch hook events

### 11.8 Policy Hash Change Detection

**TC-X14** Policy re-imported with different content
- Input: New policy for same definition with different hash (changed route, type, or order)
- Expected: Cache cleared; new policy used for next trigger; existing instances keep old pinned policy

**TC-X15** Policy label/description change only (no hash change)
- Input: Only `label` or `description` fields changed in policy JSON
- Expected: Hash unchanged; no new policy row created; same policy continues

### 11.9 Idempotency Marker Race

**TC-X16** Timeout idempotency marker insertion fails
- Input: `InsertCaseAAsync` throws during timeout processing
- Expected: Exception caught; TIMEOUT_ERROR notice; next tick retries (marker already inserted if partial)

### 11.10 Stale ACK for Non-Existent Instance

**TC-X17** ACK arrives for deleted/missing instance
- Input: `AckAsync` for `instance_guid` that no longer exists in DB
- Expected: STALE_ACK notice; ACK status set to Failed gracefully; no exception propagates

### 11.11 Order Sentinel Value in UI

**TC-X18** Hook with no explicit order defined in policy JSON
- Input: Hook JSON with no `order` field
- Expected: `order_seq` set to `999` in DB; hook sorts last in its group; value visible in admin UI as 999 (not 32767)

---

## Summary — Critical Paths to Verify in Production

| Priority | Area | Key Scenario |
|----------|------|--------------|
| P0 | Gate success drain | Gate succeeds with code → effects drain → Complete fires with resolved next code (not immediate) |
| P0 | Gate failure | Gate fails → everything skipped immediately → failure code fires |
| P0 | Complete handoff | Consumer receives Complete → does not rerun transition logic → fires resolved next event |
| P0 | ValidationMode | Consumer in ValidationMode → suppresses auto-transition → waits for engine |
| P1 | Effect timeout | Effect pending > 60s → abandoned → ordering advances |
| P1 | ACK gate | Gate hook max retries → instance suspended → HOOK_ACK_SUSPEND |
| P1 | Case A timeout | Policy timeout_event fires → TriggerAsync called → TIMEOUT_FIRED |
| P1 | Policy pinning | Existing instance → uses pinned policy; new instance → uses latest policy |
| P2 | Backward compat | Old JSON with `blocking: true/false` still works |
| P2 | Handler versioning | `MinVersion` filter prevents old handler from running on new events |
