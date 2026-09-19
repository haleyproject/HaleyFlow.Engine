# HaleyFlow — Execution Order Fix + Complete Event Kind

> Historical design plan. The implementation is now covered by [protocol conformance and recovery changes](PROTOCOL_RECOVERY_CHANGES.md) and [automated regression tests](../WFE.Test/README.md). Statements below about the old implementation are retained as planning history, not current behavior.

---

## Part 1 — The Problem

### What the contract is supposed to be

WorkflowRelay (the in-process synchronous execution path) defines the canonical contract:

```
1. Run transition handler → consumer validates, executes business logic
2. If handler succeeds → run hooks in order (gate/effect contract)
3. After all hooks resolve → fire the transition's success event code
```

This is clear, sequential, and deterministic. The developer writes their handler once, and the same logic applies whether running in tests via Relay or in production via the Engine.

### What the Engine currently does (the bug)

The Engine violates this contract in `InstanceOrchestrator.TriggerAsync`:

```
Current (wrong):
  1. Apply state transition in DB
  2. Create lifecycle ACK
  3. Emit hooks to DB
  4. Dispatch BOTH transition event AND all hook events at the same time
```

This means the consumer **receives hook workload before its transition handler has even run**. The consumer sees:

- Hook events in inbox (routes to hook handlers)
- Transition event in inbox (routes to transition handler)

...simultaneously. In practice the hooks often arrive first (engine emits hooks first in `toDispatch`), so the consumer is already processing hook work for a state it hasn't confirmed entering yet.

### Why the current TransitionMode workaround is wrong

When hooks are present, the engine sets `DispatchMode = ValidationMode` on the transition event. The original intent was:
- `ValidationMode` → consumer runs business logic, must NOT call `TriggerAsync`
- `TransitionMode` → dispatched after all hooks, tells consumer to call `TriggerAsync` with the success code

The problem: hooks still run *concurrently* with the ValidationMode transition event. The consumer has no ordering guarantee. Additionally, `TransitionMode` required hooks to carry `OnSuccessEvent`/`OnFailureEvent` codes — meaning developers could accidentally fire the wrong next event from inside a hook handler. The gate success code was captured and re-dispatched, but the mechanism was fragile and confusing.

### What the fix must achieve

1. **Transition handler runs first, always** — no hooks dispatched until the ValidationMode ACK comes back `Processed`
2. **Hooks dispatch in order, after validation confirmed** — existing gate/effect ordering contract unchanged
3. **After all hooks resolve, engine dispatches a `Complete` event** — replaces `TransitionMode`
4. **Complete event is durable** — engine creates it inline; monitor only resends overdue deliveries
5. **Hook events do not expose consumer-owned routing decisions** — hooks do their work and ACK; terminal gate codes remain engine-owned policy data.

### Clarified design decisions

The following points were confirmed after review and take precedence over any older wording below:

- **`ValidationMode` does not change business logic.** The consumer still runs the same transition handler and business operations as `NormalRun`. The difference is in the consumer runtime: after the handler returns, the runtime must suppress post-ACK auto-trigger behavior while `DispatchMode == ValidationMode`.
- **Gate terminal complete codes remain valid.** A gate hook may still own a terminal `complete.success` / `complete.failure` code. Those codes are how gate-success skip and terminal gate failure remain expressible. This plan does **not** remove that capability from gate hooks.
- **Engine owns `lc_next` inline.** The moment the engine resolves the completion of a lifecycle (success/failure after hooks), it should write/update `lc_next`, create the complete-event ACK, write `lcn_ack`, mark `dispatched=1`, commit, and dispatch inline. Monitor should only resend overdue `ack_consumer` rows for the already-created Complete event.
- **`lc_next.next` is the single resolved suggested next code.** It may come from a terminal gate hook code, or from the transition rule's fallback `complete` code. If neither exists, store `0` and still emit the Complete event so consumer code can decide the next step.
- **`Complete` is a confirmation handoff, not a second routing phase.** By the time the engine emits `Complete`, it has already resolved the single suggested next step. The consumer receives that suggestion as confirmation, may accept it as-is, or may override it in wrapper code.

---

## Part 2 — Ground Rules (must not change)

These mechanics are correct and must be preserved exactly:

| Rule | Where enforced |
|------|---------------|
| Gate hooks block progression (all consumers at same order must ACK before next order dispatches) | `AckOutcomeOrchestrator.AdvanceNextHookOrderAsync` |
| Effect hooks are fire-and-forget within `EffectTimeoutSeconds`; abandon does not block | `MonitorOrchestrator.ResendDispatchKindAsync` + `AbandonEffectHookAsync` |
| Gate + effect at same `order_seq`: effect only advances when gate at same order is done | `AckOutcomeOrchestrator.AckAsync` — effect path checks `CountIncompleteBlockingInOrderAsync` |
| Gate-success skip: if gate has terminal `complete.success`, skip later gate orders, still run same-order effects, and only keep later effects that are explicitly marked `send="always"` | Success-path hook advancement logic |
| ACK gate: blocks new trigger while prior lifecycle ACKs unresolved | `InstanceOrchestrator.TriggerAsync` — `CountPendingForInstanceAsync` |
| Blocking hook gate: blocks new trigger while blocking hooks unresolved | `InstanceOrchestrator.TriggerAsync` — `CountPendingBlockingHookAcksAsync` + `CountUndispatchedBlockingHooksAsync` |
| Policy is locked at instance creation | `EnsureInstanceAsync` — `policy_id` written once |
| Hook rows are emitted at trigger time (policy captured) | `PolicyEnforcer.EmitHooksAsync` called inside `TriggerAsync` |
| Durability before delivery: DB commit before dispatch | `TriggerAsync` — commit then `_dispatchEventsAsync` |
| Idempotency via `ack.guid` | All ACK create paths use upsert-or-get |

---

## Part 3 — New Execution Contract

```
Phase 1 — Transition (ValidationMode)
  Engine applies state, emits hook rows (not dispatched), dispatches ValidationMode event.
  Consumer runs the same transition handler/business logic as NormalRun.
  Consumer runtime records the ACK result but does NOT auto-trigger the next event while in ValidationMode.
  ACK Processed → continue to Phase 2.
  ACK Failed → engine fires OnFailureEvent directly via TriggerAsync. Stop.

Phase 2 — Hooks (ordered, gate/effect contract unchanged)
  Engine dispatches first hook order batch.
  Gate fails + OnFailureEvent code → stop the current hook plan immediately, do NOT run same-order effects or later orders, fire that code directly. No Complete. State blocked.
  Gate fails + no code → stop the current hook plan immediately, do NOT run same-order effects or later orders, fall back to OnFailureEvent from the transition rule. If that is also absent → state blocked. No Complete.
  Gate succeeds → finish that order's effect phase, then advance to next order (gate-skip logic unchanged).
  Effect runs → result ignored for routing, but the engine waits for the current dispatched effect batch to become terminal before advancing.
  All hooks resolve → continue to Phase 3.

Phase 3 — Complete event
  Engine resolves one suggested next code, writes lc_next row, creates ACK, sets dispatched=1, dispatches LifeCycleCompleteEvent — all inline.
  Consumer receives Complete as a confirmation handoff from the engine.
  Consumer calls OnTransitionCompleteAsync → default behavior uses evt.NextEvent, custom wrapper logic may override it → ACKs.
  FireNextEventAsync fires → engine processes next state.
  Monitor's only role: resend overdue ack_consumer rows for Complete events (same ResendDispatchKindAsync path — no special handling needed).

NormalRun (no hooks)
  No hooks emitted → DispatchMode = NormalRun.
  Consumer receives NormalRun → runs business logic → normal post-ACK trigger path applies.
  Unchanged from today.
```

**Complete event carries:** `HooksSucceeded` bool, `NextEvent` (single resolved suggested code, `0` = engine has no suggestion), `LifeCycleId`. Consumer receives this as the engine's resolved suggestion and may either accept it or override it in `OnTransitionCompleteAsync`.

**Hook events carry:** route, params, hook type, order. They do not carry independent consumer-owned routing decisions. Terminal gate outcome codes remain engine-owned policy data.

**Per-order rule:** each order has a gate phase and an effect phase. The effect phase exists only if the order's gates finish successfully. A gate failure cancels the current order immediately: same-order effects do not run, later orders do not run.

---

## Part 4 — DB Schema (already in DB)

Both `lc_next` and `lcn_ack` already exist in `_RESOURCES/lc_state.sql`. Structure (actual):

**`lc_next`** — 1:1 with `lifecycle` (`id` IS the `lc_id`):
```
id          bigint PK + FK → lifecycle.id
created     datetime
next        int    -- suggested next event code (0 = none)
ack_id      bigint nullable -- which ACK triggered creation (hook or transition)
dispatched  bit(1) DEFAULT 0 -- 0=Complete event not yet created, 1=ACK created and being tracked
```

**`lcn_ack`** — maps Complete event's ACK to lifecycle (same pattern as `lc_ack` / `hook_ack`):
```
ack_id   FK → ack.id
lc_id    PK + FK → lifecycle.id
```

**How durability works:**
- Engine creates the Complete event inline when completion is resolved.
- `dispatched=1` → existing `ack_consumer` retry infrastructure handles delivery (same as all ACK resends)
- `dispatched=0` is not normal monitor-owned work. It represents engine-side incomplete materialization and should not be the steady-state path.
- No separate `confirmed` column needed — `ack_consumer.status` tracks delivery state

---

## Part 5 — Implementation Phases

### Phase 1 — Abstractions.Core

**1a. `LifeCycleEventKind` enum**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Enums\LifeCycleEventKind.cs`
- Add `Complete = 3`

**1b. `TransitionDispatchMode` enum**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Enums\TransitionDispatchMode.cs`
- Remove `TransitionMode = 2`
- Keep only `NormalRun = 0`, `ValidationMode = 1`

**1c. Check `ILifeCycleHookEvent`**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Interfaces\Models\ILifeCycleHookEvent.cs`
- If `OnSuccessEvent`/`OnFailureEvent` are on this interface, remove them. Hooks carry no outcome codes.

**1d. New interface `ILifeCycleCompleteEvent`**
New file: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Interfaces\Models\ILifeCycleCompleteEvent.cs`
```csharp
public interface ILifeCycleCompleteEvent : ILifeCycleEvent {
    long LifeCycleId { get; }
    bool HooksSucceeded { get; }
    int NextEvent { get; } // engine-resolved suggested next code; 0 means no suggestion
}
```

---

### Phase 2 — Engine Models + KeyConstants

**2a. New `LifeCycleCompleteEvent` class**
New file: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Models\Events\LifeCycleCompleteEvent.cs`
```csharp
internal sealed class LifeCycleCompleteEvent : LifeCycleEvent, ILifeCycleCompleteEvent {
    public override LifeCycleEventKind Kind => LifeCycleEventKind.Complete;
    public long LifeCycleId { get; set; }
    public bool HooksSucceeded { get; set; }
    public int NextEvent { get; set; }
    public LifeCycleCompleteEvent() { }
    public LifeCycleCompleteEvent(LifeCycleEvent src) : base(src) { }
}
```

**2b. `LifeCycleHookEvent` — keep only engine-owned hook outcome data**
File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Models\Events\LifeCycleHookEvent.cs`
- Keep gate terminal complete-code data available to the engine when needed for gate-success skip / terminal gate failure
- Do not rely on hook events as a consumer-side routing surface for choosing arbitrary next transitions

**2c. `KeyConstants`**
File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\KeyConstants.cs`
- Add `KEY_HOOKS_SUCCEEDED = "hooks_succeeded"`
- Add `KEY_NEXT_EVENT = "next_event"`

---

### Phase 3 — Query File for `lc_next`

New file: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Queries\Instance\QRY_LC_NEXT.cs`

Queries needed:
- `INSERT` — `INSERT IGNORE INTO lc_next (id, next, ack_id, dispatched) VALUES (@LC_ID, @NEXT, @ACK_ID, 0)`
- `MARK_DISPATCHED` — `UPDATE lc_next SET dispatched=1 WHERE id=@LC_ID`
- `LIST_PENDING` — `SELECT id, next FROM lc_next WHERE dispatched=0 LIMIT @LIMIT`

Also need to insert `lcn_ack` row when dispatching:
- `INSERT lcn_ack (ack_id, lc_id) VALUES (@ACK_ID, @LC_ID)` — add to existing `QRY_LC_ACK.cs` pattern or new file

---

### Phase 4 — Engine DAL

New file: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Utils\DAL\MariaLcNextDAL.cs`
- `InsertAsync(lcId, nextCode, triggeringAckId, load)` — write `lc_next` row with `dispatched=0`
- `MarkDispatchedAsync(lcId, ackId, load)` — set `dispatched=1`, insert `lcn_ack` row
- `ListPendingAsync(limit, load)` — return `(id, next)` pairs where `dispatched=0`

New interface `ILcNextDAL` with same three methods. Register in `IWorkFlowDAL`.

Pattern: follow `MariaLcTimeoutDAL` as reference — same insert-first idempotency style.

---

### Phase 5 — InstanceOrchestrator

File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Services\Orchestrators\InstanceOrchestrator.cs`

**Change in `TriggerAsync`:**

Current (lines 249–279): after emitting hook rows, engine immediately creates hook ACKs, marks them dispatched, and adds `LifeCycleHookEvent` objects to `toDispatch` — all dispatched at the same time as the transition event.

**New behavior:** Do NOT create hook ACKs or dispatch hook events at trigger time. Only emit hook rows to DB (already done by `EmitHooksAsync`). The `dispatched=0` bit on `hook_lc` rows already records that they are waiting.

```
TriggerAsync new flow:
  1. Apply transition (unchanged)
  2. EmitHooksAsync → write hook_lc rows with dispatched=0 (unchanged)
  3. hookEmissions.Count > 0 → dispatchMode = ValidationMode, else NormalRun (unchanged)
  4. Create lifecycle ACK (unchanged)
  5. Build LifeCycleTransitionEvent list (unchanged, no hook events)
  6. REMOVE: hook ACK creation loop, hook event building loop
  7. Commit → dispatch ONLY transition events
```

Remove lines 249–279 (the entire hook fan-out block in `TriggerAsync`).

Also remove `hookAckGuids` from result / `toDispatch` accumulation for hooks.

---

### Phase 6 — AckOutcomeOrchestrator

File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Services\Orchestrators\AckOutcomeOrchestrator.cs`

**6a. Handle ValidationMode lifecycle ACK (new path in `AckAsync`)**

Currently `AckAsync` skips all processing if `hookCtx == null` (line 112: `if (hookCtx == null) return`). When a lifecycle (transition) ACK comes back, `hookCtx` is null and nothing happens — the consumer just ACKed a ValidationMode event and nobody dispatches the hooks.

New: after `if (hookCtx == null) return`, add a check for ValidationMode transition ACK:

```csharp
// Check if this is a ValidationMode lifecycle ACK
var lcRow = await _dal.LcAck.GetByAckGuidAsync(ackGuid, load);
if (lcRow != null) {
    var lcId = lcRow.GetLong(KEY_LC_ID);
    // Are there undispatched hooks for this lc_id?
    var hasUndispatched = await _dal.HookLc.CountUndispatchedByLcIdAsync(lcId, load) > 0;
    if (hasUndispatched) {
        // ValidationMode: dispatch first hook batch
        var hookCtxForLc = await _dal.Hook.GetContextByLcIdAsync(lcId, load);
        if (hookCtxForLc != null)
            await AdvanceNextHookOrderAsync(hookCtxForLc, ct);
    }
    return;
}
```

**6b. Replace `DispatchTransitionModeEventAsync` with `DispatchCompleteEventAsync`**

Current `AdvanceAndCheckTransitionModeAsync` (lines 183–222): after all hooks dispatched, looks for skipped gate, resolves its `OnSuccessEvent`, calls `DispatchTransitionModeEventAsync`.

New: replace `DispatchTransitionModeEventAsync` with `DispatchCompleteEventAsync`, carrying the single resolved next code:

```csharp
private async Task DispatchCompleteEventAsync(DbRow hookCtx, int resolvedNextEvent, CancellationToken ct) {
    var lcId = hookCtx.GetLong(KEY_LC_ID);
    var defVersionId = hookCtx.GetLong(KEY_DEF_VERSION_ID);
    var instanceGuid = hookCtx.GetString(KEY_INSTANCE_GUID) ?? string.Empty;
    var entityId = hookCtx.GetString(KEY_ENTITY_ID) ?? string.Empty;
    var definitionId = hookCtx.GetLong(KEY_DEFINITION_ID);
    var metadata = hookCtx.GetString(KEY_METADATA);

    // Engine owns Complete creation inline: write lc_next, create ACK, mark dispatched, dispatch.
    await _dal.LcNext.InsertAsync(lcId, resolvedNextEvent, hookCtx.GetLong(KEY_ACK_ID), new DbExecutionLoad(ct));

    var consumers = await _ackManager.GetTransitionConsumersAsync(defVersionId, ct);
    var normConsumers = InternalUtils.NormalizeConsumers(consumers);
    if (normConsumers.Count == 0) return;

    var txLoad = new DbExecutionLoad(ct);
    var ackRef = await _ackManager.CreateLifecycleAckAsync(lcId, normConsumers, (int)AckStatus.Pending, txLoad);
    await _dal.LcNext.MarkDispatchedAsync(lcId, ackRef.AckId, txLoad);  // sets dispatched=1 + writes lcn_ack

    var toDispatch = new List<ILifeCycleEvent>(normConsumers.Count);
    for (var i = 0; i < normConsumers.Count; i++) {
        toDispatch.Add(new LifeCycleCompleteEvent {
            ConsumerId = normConsumers[i],
            InstanceGuid = instanceGuid,
            DefinitionId = definitionId,
            DefinitionVersionId = defVersionId,
            EntityId = entityId,
            Metadata = metadata,
            OccurredAt = DateTimeOffset.UtcNow,
            AckGuid = ackRef.AckGuid ?? string.Empty,
            LifeCycleId = lcId,
            HooksSucceeded = true,
            NextEvent = resolvedNextEvent
        });
    }
    await _dispatchEventsAsync(toDispatch, ct);
}
```

**6c. Handle the case where no gate was skipped (normal hook completion)**

In `AdvanceAndCheckTransitionModeAsync`, after all hooks resolve, the current code only dispatches TransitionMode if a skipped gate exists. For the new contract, we always dispatch Complete when all hooks are done:

```
After all hooks dispatched (allDone=true):
  Resolve one final next code:
    - first from a skipped terminal gate's complete.success / complete.failure if applicable
    - otherwise from the transition rule's fallback complete code
    - otherwise 0
  Call DispatchCompleteEventAsync(hookCtx, resolvedNextEvent, ct)
```

This covers both cases: gate-success-skip path AND normal all-hooks-done path.

---

### Phase 7 — MonitorOrchestrator

File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Services\Orchestrators\MonitorOrchestrator.cs`

**No new creation scan needed.** Complete events use the same `ack` + `ack_consumer` infrastructure as lifecycle and hook events. The existing `ResendDispatchKindAsync` already handles overdue Pending/Delivered rows for all event kinds — Complete events are covered automatically once the ACK is created inline in Phase 6.

The monitor's scope remains: resend overdue `ack_consumer` rows. It does not create `lc_next` rows or create Complete ACKs.

---

### Phase 8 — Consumer: WorkflowKind enum

File: `E:\HaleyProject\HaleyFlow.Consumer\HaleyFlowConsumer\Enums\WorkflowKind.cs`
- Add `Complete = 3`

---

### Phase 9 — Consumer: LifeCycleWrapper

File: `E:\HaleyProject\HaleyFlow.Consumer\HaleyFlowConsumer\Abstractions\LifeCycleWrapper.cs`

**Remove `TransitionMode` handling from `DispatchTransitionAsync`:**
```csharp
// REMOVE entirely:
if (ctx.DispatchMode == TransitionDispatchMode.TransitionMode) {
    _nextEvent = ctx.OnSuccessEvent;
    return AckOutcome.Processed;
}
```

**`ValidationMode` — same business logic, but suppress post-ACK trigger:**
```csharp
// No early return. Run the same transition handler as NormalRun.
var outcome = ... existing handler dispatch ...

if (ctx.DispatchMode == TransitionDispatchMode.ValidationMode) {
    _nextEvent = null;  // consumer runtime suppresses follow-up trigger
}

return outcome;
```

**Add `DispatchCompleteAsync` + `OnTransitionCompleteAsync`:**
```csharp
internal async Task<AckOutcome> DispatchCompleteAsync(ILifeCycleCompleteEvent evt, ConsumerContext ctx) {
    return await OnTransitionCompleteAsync(evt, ctx);
}

// Override this to customise. Default: accept engine's resolved next suggestion if present.
// Complete is a confirmation handoff from the engine, not a second routing pass.
protected virtual Task<AckOutcome> OnTransitionCompleteAsync(ILifeCycleCompleteEvent evt, ConsumerContext ctx) {
    _nextEvent = evt.NextEvent > 0 ? evt.NextEvent : null;
    return Task.FromResult(AckOutcome.Processed);
}
```

---

### Phase 10 — Consumer: WorkFlowConsumerManager

File: `E:\HaleyProject\HaleyFlow.Consumer\HaleyFlowConsumer\Services\WorkFlowConsumerManager.cs`

**`ProcessItemAsync` dispatch switch — add Complete branch:**
```csharp
outcome = item.Kind switch {
    WorkflowKind.Transition => await wrapper.DispatchTransitionAsync((ILifeCycleTransitionEvent)evt, ctx),
    WorkflowKind.Hook       => await wrapper.DispatchHookAsync((ILifeCycleHookEvent)evt, ctx),
    WorkflowKind.Complete   => await wrapper.DispatchCompleteAsync((ILifeCycleCompleteEvent)evt, ctx),
    _                       => throw new InvalidOperationException($"Unknown kind: {item.Kind}")
};
```

**`BuildInboxRecord` — handle Complete event:**
```csharp
if (evt is ILifeCycleCompleteEvent ce) {
    record.Kind = WorkflowKind.Complete;
    record.HookType = null;  // not a hook
    // on_success / on_failure from base event already mapped
}
```

---

### Phase 11 — Consumer: Timeline UI

File: `E:\HaleyProject\HaleyFlow.Consumer\HaleyFlowConsumer\Services\Renderers\ControlBoardTLR.cs`
- Complete event: letter **C**, accent color purple `#7048e8`, section-title "COMPLETE"
- Update dispatch-mode label: remove "TransitionMode" case, it no longer exists

File: `E:\HaleyProject\HaleyFlow.Consumer\HaleyFlowConsumer\__RESOURCES\consumer.sql`
- Update `inbox.kind` comment: `1=Transition, 2=Hook, 3=Complete`

---

### Phase 12 — Limit hook complete codes to terminal gate behavior; keep Relay aligned

**`SnapshotHookRoute` model**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Models\Snapshot\`
- Keep `CompleteSuccessCode` / `CompleteFailureCode` for gate hooks
- Do not treat effect hooks as owning terminal routing

**`DefinitionJsonReader`**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Models\Snapshot\DefinitionJsonReader.cs`
- Preserve parsing of `complete.success` / `complete.failure` for gate hooks
- Ignore or reject terminal `complete` blocks on `type: "effect"` hooks

**`PolicyEnforcer`** — `ResolveHookContextFromJson`
File: `E:\HaleyProject\HaleyFlow.Engine\HaleyFlowEngine\Services\Core\PolicyEnforcer.cs`
- Keep returning hook complete codes for terminal gate behavior
- Continue to let the engine, not the hook handler, own progression decisions

**`WorkflowRelay`**
File: `E:\HaleyProject\HaleyAbstractions.Core\HaleyAbstractionsCore\WorkFlowEngine\Models\Relay\WorkflowRelay.cs`
- Preserve terminal gate complete-code semantics so relay stays aligned with engine:
  - gate success with terminal code → remember that code, skip later gates, run same-order effects, keep later `send="always"` effects on the success path, then continue
  - gate failure with terminal code → use that code immediately; do not run same-order effects or later orders
  - gate failure without code → blocked / fallback behavior remains explicit
- After all hooks, relay still converges to a single resolved next step

**`LifeCycleHookEvent`**
- Do not expose hook outcome routing as a consumer-owned trigger surface
- Engine may still carry the necessary terminal gate data internally to resolve completion

---

### Phase 13 — Protocol HTML Update

File: `E:\HaleyProject\HaleyFlow.Engine\docs\HaleyFlowProtocol_v1.html`

- Update execution order section: new 7-step flow
- Remove hook `complete` block from `emit[]` field table
- Remove `TransitionMode` from dispatch-mode table
- Add `Complete` event section: kind=3, what it carries, when dispatched, `OnTransitionCompleteAsync`
- Update gate hook section: block or allow only; no outcome codes
- Update ValidationMode description: "run normal business logic; suppress post-ACK auto-trigger; engine dispatches hooks next"

---

## Part 6 — Execution Order

1. Phase 1 — Abstractions enums + `ILifeCycleCompleteEvent` (standalone, must compile first)
2. Phase 2 — Engine event model + KeyConstants (depends on Phase 1)
3. Phase 3 — Query file for `lc_next` (DB already exists)
4. Phase 4 — Engine DAL `MariaLcNextDAL` + `ILcNextDAL` (depends on Phase 3)
5. Phase 5 — `InstanceOrchestrator` hook dispatch removal (depends on Phase 1)
6. Phase 6 — `AckOutcomeOrchestrator` ValidationMode ACK + Complete dispatch (depends on Phase 2, 4)
7. Phase 7 — `MonitorOrchestrator` confirmation only: no lc_next creation scan; existing resend path must cover Complete ACKs
8. Phases 8–10 — Consumer (depends on Phase 1)
9. Phase 11 — Consumer UI (depends on Phase 8–10)
10. Phase 12 — Policy/Relay cleanup (depends on Phase 1, 2)
11. Phase 13 — Protocol HTML (standalone)

---

## Part 7 — Verification

| Scenario | Expected |
|----------|----------|
| NormalRun (no hooks) | Trigger → ValidationMode NOT set → consumer runs logic → `TriggerAsync` → next state. Unchanged. |
| ValidationMode, Processed | Trigger → ValidationMode event → consumer runs the same transition handler → consumer ACKs Processed without auto-triggering → hooks dispatched in order → Complete event → consumer receives engine's resolved `NextEvent` as confirmation and either accepts it or overrides it → next state. |
| ValidationMode, Failed | Consumer ACKs Failed → engine fires `OnFailureEvent` via `TriggerAsync` directly. No hooks. No Complete. |
| Gate hook fails, no code | Hook ACK comes back non-Processed → stop the current hook plan immediately. Same-order effects and later orders do not run. Fall back to transition rule's OnFailureEvent. If absent → state blocked. No Complete. |
| Gate hook fails + OnFailureEvent | Engine fires failure code directly via `TriggerAsync`. Same-order effects and later orders do not run. No Complete. |
| All hooks resolve | `AdvanceNextHookOrderAsync` returns `allDone=true` → engine resolves one final next code → `DispatchCompleteEventAsync` → `lc_next.next` written → Complete dispatched. |
| Consumer crash after receiving Complete | `ack_consumer.status` stays Pending/Delivered → monitor's `ResendDispatchKindAsync` retries delivery. Same as all ACK retries — no special handling. |
| Gate-success skip + effects drain | Skipped gate's terminal `complete.success` becomes the resolved `lc_next.next` suggestion passed through the Complete event after the reachable success-path effects finish (same-order effects, plus later `send="always"` effects only). |
| Complete with next=0 | Engine still dispatches Complete. Default consumer complete handler does nothing, but custom wrapper logic may choose the next trigger. |
| Relay parity | Relay: handler → hooks → resolve one final next step. Engine: ValidationMode handler → hooks → Complete → consumer fires resolved next step. Same contract. |
