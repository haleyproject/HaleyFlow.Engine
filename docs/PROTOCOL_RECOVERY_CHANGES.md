# Protocol conformance and recovery changes

Updated 19 September 2026. This document describes the implemented durable Engine and Consumer behavior and supersedes conflicting recovery claims in older planning documents.

## Execution contract

- Self-loops create a new lifecycle occurrence, transition ACKs, and a new hook plan. An instance revision changes even when the state does not.
- Engine definition and policy imports use the same snapshot reader and policy validator as Relay. Definitions are checked against an attached policy before becoming active. Policy imports require a definition.
- For a destination state and incoming event, the last matching via-specific rule is selected; the last general rule is its fallback. Hooks are emitted only from that selected rule.
- Rule completion is transition/Complete behavior. It is never copied into a hook's completion context. The Engine resolves hook completion and failure routes.
- Explicit hook order is an integer from 1 through 2147483646. Omitted order uses 2147483647 and always sorts last. Hooks of the same order and phase may run concurrently.
- Every transition consumer must ACK Processed before any hooks are released. Retry/Delivered remains pending. A Failed validation follows the selected rule's failure route, or leaves the execution blocked when no route is configured.
- At each order, gates precede effects. All required gates must succeed before effects at that order start. Effects finish or are abandoned before the next order starts.
- A successful gate completion code skips later gates and later ordinary effects. Later effects with `send: always` remain eligible. Complete carries the resolved next event only after eligible phases finish.
- `ack_mode: any` means one successful consumer of one hook satisfies that hook. It does not satisfy sibling routes. Each emitted gate route remains its own obligation.
- Hook delivery uses one event builder for immediate delivery and monitor redelivery, preserving parameters, deadlines, lifecycle identity, order, and occurrence counts.

## Transition gate and consumer ordering

`RequireAckBeforeNextTransition` remains opt-in. When enabled, an ordinary next trigger waits for successful lifecycle ACKs and unresolved gate work, including queued gates. Effects do not block an independently requested next transition through this option. The normal Complete handoff still waits for the eligible effect phases.

An unresolved failed execution cannot be bypassed by an ordinary trigger. Explicit recovery triggers can bypass this gate with `SkipAckGate` and an expected lifecycle guard.

The Consumer serializes phases per instance within one running manager. Same-phase hooks may run concurrently, while different instances remain independent and the configured global concurrency limit is respected. In-process proxy queues are isolated by consumer identity. Run one active manager per consumer identity; the queue is not a distributed lease or a guarantee that external side effects happen exactly once.

## Durable progression and continuation

A transition creates an `lc_execution` record in the same database transaction. ACK updates use an instance row lock. The monitor reconciles pending execution records, so a crash after an ACK cannot permanently lose hook release, Complete creation, or a configured failure continuation. Database changes commit before delivery; monitor redelivery covers interrupted notifications.

The Consumer saves its result and intended next event before delivering the ACK. Its outbox remains Pending until both the ACK and the requested continuation succeed. Retries use a stable `RequestId`; `trigger_receipt` records the applied result transactionally. A lost response therefore replays the result rather than applying another transition. The consumer reuses an already-recorded terminal business result on redelivery.

Only a Processed outcome can publish a recorded next event. A business rejection may be represented as Processed with a chosen failure event. Retry, Failed, Delivered, and hook deliveries do not directly publish wrapper continuations. NormalRun transition ACKs and Complete ACKs may own a continuation; every consumer of the source ACK must have processed it. `SourceAckGuid` and `ExpectedLifeCycleId` prevent obsolete work from advancing a later lifecycle.

Business handlers must still make their own external side effects idempotent. A crash after a business side effect but before its outbox result is committed can cause that handler to run again.

## Timeout recovery

A state timeout is triggered using a stable request identifier and the expected lifecycle before its audit marker is written. Failure leaves the timeout eligible for retry. Legacy markers are not treated as proof that the transition succeeded. Repeated or concurrent attempts cannot apply the same timeout twice.

Effect deadlines use the immutable ACK creation time. Resending an effect does not extend its execution allowance. Existing suspension and consumer-availability rules continue to govern monitor eligibility.

## Historical import

The Consumer walks from the initial state along connected outgoing transitions, choosing chronological occurrences supplied by the provider. It supports branches and self-loops, rejects ambiguous equal-time branches, and has a finite traversal limit.

The Engine validates the complete path before writing and imports it in one transaction. Transition lookup uses internal event IDs after resolving the public event code. A content fingerprint in `backfill_import` makes an identical replay a no-op; different content or existing unrelated history is rejected. This is an initial historical import, not an append or merge API. Existing instance definition pinning is respected.

## Database deployment

Canonical schema: [lc_state.sql](../_RESOURCES/lc_state.sql).

Upgrade script: [20260919_protocol_recovery.sql](../_RESOURCES/migrations/20260919_protocol_recovery.sql).

Changed tables:
- `instance`: revision counter for concurrency checks, including self-loops.
- `hook`: order column widened to INT so omitted order can sort last.
- `activity` and `activity_status`: unique names.
- `hook_route`: unique route name, matching runtime identity.

Added tables: `lc_execution`, `trigger_receipt`, and `backfill_import`.

For an existing installation, stop Engine writers, back up the database, resolve any duplicate natural keys reported by the preflight, and apply the upgrade script before deploying these binaries. The script is rerunnable and seeds recovery records for current acknowledged lifecycle work. It also recalculates existing hook order from the selected stored policy, preserving explicitly ordered 999 hooks.

Review in-flight legacy plans before resuming: the migration cannot undo hooks already executed under an old fallback rule, business side effects, or continuations lost by a previously confirmed outbox. No shared or application database was upgraded during implementation; verification used disposable local test databases.

The new trigger fields are additive public contracts. Engine, Consumer, and HaleyAbstractions.Core should be deployed from matching builds.

## Automated verification

[WFE.Test](../WFE.Test/README.md) is an executable xUnit regression suite. It covers protocol selection and ordering, all-consumer barriers, durable recovery after injected SQL failures, idempotent self-loops, timeout retries, immutable effect deadlines, graph backfill and rollback, migration reruns, consumer queue ordering, and outbox continuation replay.

The existing [test checklist](TEST_CHECKLIST.md) remains a broader manual scenario matrix, not evidence that every listed scenario is automated.
