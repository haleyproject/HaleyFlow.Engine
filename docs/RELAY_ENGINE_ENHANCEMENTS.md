# Relay Engine Enhancements

Discussion record and design review, 19 September 2026.

**Status: proposal for review. No runtime, protocol, API, or database change is approved or implemented by this document.** The policy-versioning decision in section 12 is agreed. Other enhancements and rollout stages remain proposals.

This document records the discussion about when an application needs a workflow engine, why a lightweight relay can be useful even for a small application, and what the current relay would need to support configurable approval orchestration. It uses the leave-approval example throughout and distinguishes current behavior from proposed capabilities.

The same discussion is included in the [Relay engine enhancements section of the HTML relay guide](workflow_relay_guide.html#relay-engine-enhancements). The existing execution contract remains the description of current behavior; the proposals here do not silently replace it.

## 1. The requirement we are trying to satisfy

The application should expose reusable business operations. The workflow definition and policy should determine when those operations run, which participants or parameters they receive, how outcomes combine, and what happens next.

For leave approval, the developer should be able to provide an operation that requests an approval from a specified person. Changing from one manager to two managers, from parallel to sequential participation, or from final to provisional manager approval should be expressible through supported configuration without rewriting the leave process in application code.

The motivation is therefore broader than reducing code or adding reliable delivery. It is making orchestration changeable at runtime while keeping business operations stable.

This does not mean that JSON can introduce an entirely new business capability. A new payroll integration, a new leave-balance rule, or an unsupported policy operator still needs implementation. Configuration can change behavior within the vocabulary that the runtime and application already support.

## 2. The three relevant components

| Component | Current responsibility | Intended place in this discussion |
| --- | --- | --- |
| HaleyFlow Engine | Database-backed workflow execution with persisted lifecycle state, dispatch, acknowledgements, monitoring, reminders, and recovery mechanisms. | Use when the workflow platform should own durable execution and its operational lifecycle. |
| HaleyFlow Consumer | Application integration with the full engine, including handling delivered work and returning acknowledgements. It also contains the optional relay host and default relay state store. | Provides integration and hosting conveniences; its full-engine infrastructure is not inherently required by the core relay interpreter. |
| WorkflowRelay | An embeddable interpreter in the shared abstractions project, with registered transition and hook handlers. It runs inline against a parsed workflow snapshot. | Improve as a lightweight orchestration runtime whose application owns persistence and execution reliability. |

The relay interpreter can already be used without starting the full engine. The optional consumer-side host is a separate integration choice. A lightweight relay does not need to become a second database-backed engine to be useful.

Both execution paths should share the meaning of the supported definition and policy vocabulary. That does not imply identical delivery mechanisms or identical operational guarantees. Sharing JSON parsing alone also does not automatically provide shared interpretation of every business parameter.

## 3. Why a fixed leave process may not need an engine

The original example is straightforward: an employee submits leave, the manager records an approval or rejection, HR makes the final decision, and an approved request is assigned to the employee.

If this process is fixed and belongs to one application, an ordinary application implementation can be appropriate:

1. Save the leave request and its initial status.
2. Create the manager's approval task, directly in the same application transaction where appropriate, or through an outbox handoff.
3. When the manager responds, save the decision and schedule the HR stage.
4. When HR responds, save its final decision.
5. If HR approves, apply the leave entitlement or booking and record completion.

The user's example sends both manager approval and manager rejection to HR. That is a valid policy. Another organization might stop immediately on manager rejection. Neither behavior should be assumed to be universal.

An application-owned outbox can make each handoff reliable. The application can commit its domain change and the next work item together, then process that work later. A completed outbox item can create another outbox item. The original HTTP request does not need to wait for the whole business process.

This is already a form of workflow management. A dedicated engine is not mandatory merely because work has several steps or uses an outbox. For a small, stable process, introducing a full engine may cost more in integration and operation than it saves.

There must still be a clear distinction between accepting a request, delivering a work item, and completing the entire leave process. Clearing an outbox item does not by itself mean that leave has been finally approved or assigned.

## 4. What an outbox does and what orchestration adds

An outbox addresses reliable handoff of work associated with a committed application change. It does not, by itself, interpret the business process.

| Question | Responsibility |
| --- | --- |
| How do we avoid losing the next work item after saving a decision? | Application transaction and outbox. |
| Which stage should follow this decision? | Orchestration rules. |
| Should one manager or two managers participate? | Participant selection and orchestration policy. |
| Should managers receive tasks together or one after another? | Activation policy. |
| Does one approval suffice, or must everyone approve? | Decision aggregation policy. |
| Is a manager's rejection final, or must HR review it? | Outcome routing policy. |
| How do we avoid applying leave twice after a retry? | Business idempotency and coordinated state updates. |

An application can implement all these responsibilities itself. The relevant question is whether their interpretation should be implemented repeatedly inside each business process or provided once by a reusable runtime.

Once an application reads definitions, selects steps, tracks outstanding participants, combines outcomes, and resumes execution, it has implemented an orchestration layer. That may be a sensible local solution, but an outbox alone did not supply those capabilities.

A relay and an outbox can work together. The relay determines the next eligible work; the application commits the resulting state and work records safely. Moving orchestration into a relay does not remove the need for correct application transactions.

## 5. The leave example as requirements evolve

The following examples describe intended behavior. They are conceptual configuration examples, not a claim that the current JSON schema already supports first-class approval groups.

### 5.1 One manager, followed by HR

The manager stage has one participant. The participant's decision settles that stage. The configured outcome route sends the request to HR, which makes the final decision. Only an HR approval permits leave assignment.

If both manager outcomes go to HR, the manager's decision is retained as information for HR. An approved manager task and an approved leave request are different facts.

### 5.2 Two managers in parallel, any approval is sufficient

The manager stage resolves two participants and makes both approval tasks available without waiting for either person's response. The group succeeds when one valid approval satisfies its policy.

| Manager A | Manager B | Typical group result under an any-approval policy |
| --- | --- | --- |
| Pending | Pending | Waiting. |
| Approved | Pending | Group approved; the next stage becomes eligible. |
| Rejected | Pending | Waiting because the remaining participant can still satisfy the policy. |
| Rejected | Approved | Group approved, unless the policy explicitly gives rejection veto power. |
| Rejected | Rejected | Group rejected because no remaining participant can satisfy the policy. |

Rejection-as-veto is a separate choice. It must not be inferred merely from the presence of a rejected task.

Once the group is satisfied, the policy must define what happens to remaining tasks: cancel them, close them as superseded, or accept later responses for audit without changing the completed result. The exact default remains open.

### 5.3 Two managers in parallel, all approvals required

Both tasks are activated together, but one approval leaves the group waiting. The group succeeds only after both required approvals exist. A rejection normally makes the required all-approval condition impossible, but whether to settle immediately or collect remaining responses is a policy decision.

This illustrates why activating participants and combining their decisions are independent concerns. Parallel participation does not imply any approval, and sequential participation does not imply all approvals.

### 5.4 Managers participate sequentially

The first participant receives a task. The second task is created only when the preceding decision permits further participation.

For sequential all-approval, the usual behavior is to ask the second manager after the first approves. For sequential any-approval, the first approval may finish the group, while a rejection may cause the next manager to be asked. These examples show why sequence and aggregation need separate, explicit rules.

Changing the activation mode should not require replacing the underlying operation that requests one person's approval.

### 5.5 Manager approval becomes provisional and HR remains final

The manager stage produces a recommendation or intermediate decision. The HR stage produces the final business decision. Manager approval must not directly invoke leave assignment.

Both manager success and manager rejection can route to HR if that is the selected process. Alternatively, only success can proceed. The workflow should express these edges explicitly.

A provisional manager decision is often represented by the position of the manager stage in the graph and its outgoing routes. It does not automatically require a special provisional-approval operator in the relay.

### 5.6 The stable application operations

The application may provide operations such as resolving eligible participants, creating one approval task, validating a submitted decision, notifying a person, and applying approved leave.

Those operations should not each contain their own interpretation of one-manager versus two-manager rules, any versus all aggregation, or HR sequencing. That interpretation belongs in a reusable orchestration or approval-group component.

Participant lookup still needs application knowledge. JSON can refer to a manager relationship, a role, or a configured participant selector; an application resolver turns that reference into authorized people. The relay should not acquire direct knowledge of the organization's employee database.

## 6. Where a lightweight relay is useful

Application size is not the deciding factor. The frequency and range of process changes, the number of reused business operations, and the desired ownership of execution reliability matter more.

| Application | Potential reason to use a relay |
| --- | --- |
| Leave and expense approvals | Change approvers, escalation paths, intermediate review, and final authority. |
| Purchase and budget requests | Choose review stages from amount, department, or request type using supported policy mechanisms. |
| Document review | Configure ordered or grouped reviewers and publication eligibility. |
| Employee or supplier onboarding | Reorder checks and approvals while keeping the check implementations reusable. |
| Vendor qualification and case review | Vary evaluator groups and final validation stages across supported configurations. |
| Short automated processes | Reuse JSON-defined gates, effects, and routing without a separate workflow service. |

These are candidate uses for the improved model. An application needing durable dispatch, reminders, recovery, and centralized workflow operations may still be better served by the full engine. A fixed, isolated process can remain application code plus an outbox.

## 7. What the relay can already do

The reviewed relay uses the shared definition reader and policy validation. It builds a workflow snapshot, resolves transitions from the current state and event, and invokes application handlers registered by event code or hook route.

It supports ordered hook processing, gate and effect behavior, policy parameters, automatic follow-up events through completion codes, and handler-level routing overrides or completion callbacks. Handler routing can override configured routing, so excessive process-specific overrides in application code would reduce the value of a JSON-driven process.

The core interpreter has no requirement to run the full engine's dispatch and acknowledgement infrastructure. The optional consumer host wraps relay registration and invocation and can load and save state through a replaceable state-store interface.

An application can already represent a human wait as a workflow state and invoke a later event after a person responds. The missing capability is richer, first-class coordination and resumption of partially completed groups, rather than an absolute inability to represent a waiting process.

An application can also construct a fresh relay from new JSON itself. The current limitation is the absence of an integrated revision-aware refresh and selection mechanism in the standard host, not an inability to parse new JSON at all.

## 8. Current execution semantics that must remain clear

| Concern | Observed behavior |
| --- | --- |
| Execution location | Work runs inline and is awaited by the caller. Asynchronous I/O does not turn it into a background workflow service. |
| Hook ordering | Hooks are grouped by order; gate processing precedes effect processing within an order. |
| Same-order concurrency | The relay awaits handlers through sequential loops. Same order does not currently mean concurrent execution. |
| Gate decisions | Handlers return Boolean outcomes. A gate failure makes its phase fail; this is not a general business any-approval aggregator. |
| Effects | Returned Boolean values are ignored for routing. Exceptions can still interrupt execution. This is an existing contract, not automatically a defect. |
| Terminal gate success | Later gate phases are skipped; later effects are restricted by the existing send-always behavior. Same-order effect behavior follows the current contract. |
| Parameters | The relay passes resolved snapshot parameters to the handler. The application must interpret opaque business data unless an explicit shared evaluator exists. |
| Automatic routing | Completion codes can cause additional transitions within the same invocation. |
| State storage | The standard state-store contract reads and writes the current state string. The default host registration uses an in-memory implementation unless replaced. |

The loan sample includes business data such as `approval.rule: any` and a role selector. Their presence demonstrates that the parameter mechanism can carry approval configuration. It does not establish that the relay interprets votes, resolves employees, or manages pending approval tasks.

Business approval aggregation must also remain separate from `ack_mode`. A delivery acknowledgement says that delivered work was handled under its delivery contract. A manager's vote is a domain decision. Several consumers acknowledging delivery are not equivalent to several managers approving leave.

## 9. Confirmed gaps and implementation concerns

The following findings concern the reviewed relay and its standard host. Some are missing features, some are correctness risks, and some are deliberate existing semantics that a new contract must accommodate.

| ID | Finding | Consequence and required consideration |
| --- | --- | --- |
| G1 | The standard host initializes its relay at startup. | There is no built-in publication, refresh, and revision-selection path for new requests while the application remains running. |
| G2 | The relay request/state-store model does not pin a persisted definition and policy revision. | Reloading one shared relay would not by itself preserve the original policy for existing requests. |
| G3 | The handler result is Boolean and the relay result principally reports advanced or blocked. | There is no first-class distinction between a pending group, a business rejection, and other execution outcomes. Explicit wait states are still possible in application-managed flows. |
| G4 | There is no general participant-group decision evaluator. | Any, all, optional quorum, veto, and unresolved-vote behavior cannot be obtained merely by placing several Boolean gates at the same order. Quorum is a possible extension, not an agreed requirement. |
| G5 | Same-order handlers run sequentially. | True concurrent automatic work is not implemented. Human parallel participation also requires task activation and decision tracking, which concurrent delegates alone would not provide. |
| G6 | The definition reader rejects the same hook route more than once within a rule. | Repeating one handler with different participants cannot be expressed directly as duplicate route entries. Invocation identity and handler identity need deliberate separation if this feature is added. |
| G7 | Hooks share a mutable relay context, including its current parameters. | Replacing loops with concurrent task execution would risk cross-invocation data races. Each concurrent invocation needs isolated inputs and explicitly coordinated outputs. |
| G8 | Unregistered hook routes are skipped, including gate routes. | A configured prerequisite can be bypassed if its handler is absent. Required-handler validation or an explicit failure policy is needed. |
| G9 | A blocked gate can return after the context has advanced, before the intended rollback statement. | The caller's context can show the target state despite a blocked result. The host's save-on-success behavior does not make that context mutation consistent. |
| G10 | Automatic follow-up events recurse without a hop budget. | A cyclic or non-progressing completion route can run without a useful execution bound. Validation and a runtime limit should make this diagnosable. |
| G11 | The host loads state, executes, and saves without an atomic per-instance coordination contract. | Two responses can evaluate the same prior state and both activate later work. A concurrent dictionary does not make the whole sequence atomic. |
| G12 | The standard host saves final state only when the overall relay result advances successfully. | Earlier handler side effects or intermediate automatic transitions can have happened before a later block or exception. State persistence and business effects need a defined transaction or checkpoint boundary. |
| G13 | The default state store is in memory, and the interface stores a state string only. | Process restarts lose that default state. Durable approval facts, revision selection, active invocations, and concurrency control require application-owned persistence or a richer adapter. |
| G14 | Policy completion routing for a transition is calculated inside the registered transition-handler path. | A transition without a registered handler does not automatically receive identical completion-routing behavior. Whether a handlerless transition should auto-route needs an explicit, tested contract. |
| G15 | Handler delegates do not directly receive the execution cancellation token in their current signature. | Cancellation checks around orchestration do not guarantee cancellation of work inside an application handler. A compatible extension may be useful. |
| G16 | Effect Boolean results are intentionally ignored, while exceptions propagate. | Essential business actions such as applying leave must have a clear success and failure contract. A proposed richer outcome model must not silently reinterpret existing effect handlers. |

G8, G9, and G10 are sensible correctness work to consider before adding group features. G11 and G12 become especially important as soon as real human decisions or external side effects are involved.

These observations are based on source inspection. They are not a claim that every operational failure mode has been reproduced in an integration environment.

## 10. Proposed lightweight execution model

The relay should run one execution turn: evaluate the selected immutable workflow revision, perform or schedule eligible work through application operations, and return when it completes, blocks, fails, or needs external input.

For a human approval process:

1. The application accepts a new leave request and selects its workflow revision.
2. The relay evaluates the manager stage using that revision.
3. The participant resolver supplies eligible people according to the configured selector.
4. The application creates the required approval tasks, or commits outbox work to create them.
5. The relay returns a waiting outcome and the application persists the resulting progress.
6. A manager later responds through the application's normal API or UI.
7. The application authorizes and records that response, then invokes the relay to evaluate the updated facts.
8. The relay either remains waiting, activates another participant, routes to HR, or follows a configured rejection path.
9. After HR approves, the application performs the leave-assignment operation through the chosen consistency boundary.

No call stays open for the hours or days between steps 5 and 6. No relay-owned polling, acknowledgement monitor, or reminder service is required for this model.

For short automated work, an inline turn can still await several independent operations concurrently when an explicit execution policy permits it. That is a separate implementation concern from making several human tasks outstanding at once.

Creating an approval task successfully must not be confused with receiving an approval. The task-creation operation may complete while the approval stage remains waiting.

## 11. Proposed responsibility boundaries

| Owner | Responsibility |
| --- | --- |
| Shared workflow interpretation | Validate supported definitions and policies; determine eligible transitions, activation order, aggregation, and outcome routing. |
| Relay runtime | Execute a bounded turn using an immutable snapshot and isolated invocation inputs; return explicit progress and outcomes. |
| Application business handlers | Perform domain work, resolve organization-specific references, and enforce business rules. |
| Application security | Authenticate responders, authorize decisions, and prevent unauthorized task completion. JSON participant selection does not replace authorization. |
| Application persistence | Store leave data, approval tasks and votes, workflow progress, the pinned revision, and concurrency information. |
| Application transaction/outbox integration | Commit state and required work consistently and make retries safe. |
| Full engine, when selected | Own its durable dispatch, acknowledgements, monitoring, reminders, and recovery machinery. |

The shared parser is an existing foundation to build on. A reusable policy evaluator should be shared where the semantics are shared; the relay should not acquire a conflicting private interpretation of the same JSON.

The application does not necessarily need a separate generic workflow database. Approval tables and the business request can carry the required state. The runtime should avoid duplicating facts that already have an authoritative application owner.

## 12. Agreed decision: pin each request to its original revision

**User decision: keep the original policy for an existing request; use the new policy for new requests.**

Each request should select and retain a compatible workflow definition and policy revision when it starts. Keeping the definition and policy together prevents a pinned policy from being interpreted against a changed state graph or parameter catalog.

The contract is:

1. A newly created request selects the latest published, validated revision available for its workflow.
2. Existing requests resume against the revision they originally selected.
3. This applies to all later stages, including stages that have not started yet.
4. Published revisions are immutable. Editing the content behind an existing revision identifier is not a valid update mechanism.
5. Referenced revisions remain retrievable while requests can resume against them. An application may store an immutable snapshot instead of relying on a separately retained revision repository.
6. The application persists the revision identifier or snapshot alongside request progress so the decision survives restarts.
7. Refreshing available policies affects revision selection for new requests; it does not swap the snapshot of a running or waiting request.

Example: revision 1 requires one manager and HR. Request A starts under revision 1 and waits for its manager. Revision 2 is published with two parallel managers and any approval. Request B starts under revision 2. Request A still uses one manager and HR, even if its HR stage begins after revision 2 is published.

Automatic adoption of new rules by existing requests is excluded from the agreed behavior. Explicit migration, if ever required, would be a separately designed and authorized feature rather than a hidden consequence of refresh.

Policy pinning does not automatically answer whether resolved people are pinned. If a manager changes jobs while a request is waiting, the application still needs a documented reassignment rule. That is a separate open decision.

## 13. Runtime policy loading and validation

A proposed revision-aware provider would make immutable definition/policy bundles available to the host. The host would select the current published revision for a new request and the stored revision for an existing request.

Compiled or parsed snapshots can be cached by revision. A request's execution turn should use one snapshot consistently. Updating the provider's current-revision pointer must not mutate snapshots already being used.

Publication should validate the graph, referenced parameters, completion routes, supported group operators, and required handlers before the revision becomes selectable. The source of definitions can be an application-owned file store, database, or configuration service; the choice is not fixed by this proposal.

If a candidate update is invalid, it should not replace the last valid published revision. If an existing request's pinned revision cannot be retrieved, silently substituting the latest revision would violate the agreed behavior. The application needs an explicit failure or recovery path for that situation.

This publication mechanism does not require rebuilding the application for every supported orchestration change. Adding new handler capabilities or changing public contracts can still require deployment.

## 14. Approval groups, invocation identity, and outcomes

A proposed group contract should distinguish at least participant selection, activation mode, decision aggregation, and outcome routing. These concepts should not be compressed into one overloaded property.

| Concept | Example | Design implication |
| --- | --- | --- |
| Handler identity | Request one person's approval. | Selects reusable application behavior. |
| Invocation identity | The manager-stage task for participant A. | Identifies one occurrence for progress, correlation, and idempotency. |
| Participant selection | The employee's manager, or two resolved managers. | Requires a supported selector and application resolver. |
| Activation | Parallel or sequential. | Controls when participant work becomes eligible. |
| Aggregation | Any approval or all approvals. | Evaluates persisted decisions and unresolved participants. |
| Rejection behavior | No veto, veto, or collect all responses. | Must be explicit when these alternatives are supported. |
| Outcome routing | Continue to HR, stop, or request another review. | Controls the next workflow stage. |
| Stage authority | Manager recommendation followed by HR final decision. | Usually represented by graph structure and routes. |

A richer outcome model should distinguish completed business decisions, waiting, configuration or execution blocks, and technical failures. The exact types and API names have not been selected.

For groups, the result must be derived from the relevant participant facts. It should not depend accidentally on delegate completion order or on whichever handler first supplies a different completion code.

Concurrent automatic invocations need separate input contexts. Shared payload mutation, current-state mutation, and parameter replacement must not become an implicit communication channel between parallel handlers.

Adding separate invocation identity requires reviewing the shared snapshot model, validation, and any full-engine persistence assumptions. Removing the duplicate-route check by itself would not complete that design.

## 15. Persistence, consistency, and retry behavior

The application-owned record or adapter will generally need to identify the workflow instance, its pinned revision, its current state, and its current progress. Resumable groups additionally need enough information to identify active invocations and relevant decisions. Existing business tables may already contain much of that information.

A concurrency token, transactional lock, or equivalent application mechanism must coordinate responses to the same request. The choice should follow the application's persistence conventions.

For example, two managers may approve almost simultaneously. Both API calls must not independently observe an unsatisfied group and create duplicate HR tasks. The group completion and the next-work decision need a coordinated persistence boundary, with idempotency as additional protection.

Handlers that send messages or call external systems are not rolled back just because the relay later returns blocked or throws. The design must specify whether a turn commits atomically within one application database, records checkpoints, or emits outbox work for external effects.

The current save-after-success host contract deserves particular care when automatic follow-up transitions run several stages in one call. A later failure must not leave untracked earlier effects or cause unsafe repetition on retry.

At minimum, the review should define behavior for duplicate submissions, retried decision events, late responses after group completion, process restarts, and a crash between business effects and workflow-state persistence.

The full engine does not make application-side external effects exactly once. Consumer acknowledgements can be retried and a handler may already have performed its business action. Domain idempotency and application transactions remain necessary with either execution model.

## 16. Compatibility constraints

These proposals must not silently change the meaning of existing definitions or handler registrations.

1. Preserve current gate, effect, completion-routing, order, and send behavior unless a deliberate protocol change is approved.
2. Do not redefine same-order hooks as concurrent work merely by replacing a loop. Existing handlers may depend on sequential behavior and share mutable context.
3. Do not reinterpret delivery `ack_mode` as business voting.
4. Do not assume opaque `approval` parameters are already a universally understood protocol operator.
5. Introduce richer outcomes or cancellation support through a compatible API strategy rather than silently changing existing Boolean handlers.
6. Validate handler reuse through explicit invocation identity instead of only weakening duplicate-route validation.
7. Keep the full engine and relay aligned on any shared group vocabulary. A mode that does not support a construct should reject it clearly rather than run it with different semantics.
8. Pinning configuration does not freeze application binaries. Deployments must retain compatible handler behavior for revisions that remain active, or make incompatibility explicit.

The exact schema, interfaces, persistence format, and compatibility/versioning strategy need design review before implementation.

## 17. Options and decision framework

| Approach | Good fit | Cost or limitation |
| --- | --- | --- |
| Application code with an outbox where needed | A fixed, local process with few variants. | Process changes remain application changes unless a reusable interpreter is added. |
| Current relay with application-managed waiting states and a reusable approval coordinator | Basic JSON routing plus domain-owned task and vote management. | Rich approval semantics remain in the coordinator; passing parameters alone does not make the relay understand them. |
| Enhanced relay with explicit group semantics and application-owned persistence | Configurable orchestration in an application that can own transactions, tasks, retries, and hosting. | Requires the group, resume, revision, and consistency improvements described here. |
| Full engine and consumer integration | A platform should own durable workflow dispatch, acknowledgements, reminders, recovery, and operational management. | Additional infrastructure and integration cost; application business consistency still matters. |

A smaller first step could provide a reusable approval coordinator that interprets participant and aggregation configuration while the current relay handles stage routing. That can meet real needs without immediately expanding the shared protocol. It must be reusable orchestration code, rather than a different hardcoded manager method for every workflow variant.

A broader step would make group interpretation an explicit capability shared by the relay and full engine. That better supports general orchestration reuse, but its protocol and compatibility implications are larger.

The recommendation discussed is to improve the existing relay around these boundaries rather than introduce a separate competing engine. Choosing the implementation scope remains open.

## 18. Suggested implementation stages for review

These stages are a proposal, not an instruction to begin implementation.

### Stage 1: make the current execution contract dependable

Address required-handler validation, blocked-result state consistency, bounded automatic advancement, and the intended semantics of handlerless completion routing. Specify how the host coordinates state changes and side effects. Add focused tests that demonstrate the failures and the intended behavior.

### Stage 2: add revision selection and request pinning

Introduce immutable revision lookup and runtime refresh for new requests. Persist the selected revision through the application adapter. Verify restarts and simultaneous old/new revision execution before adding richer group behavior.

### Stage 3: introduce resumable approval coordination

Choose the reusable-coordinator or shared-group approach. Define invocation identities, waiting and decision outcomes, participant activation, and any/all aggregation. Keep security, domain tasks, and persistence with their existing application owners.

### Stage 4: complete concurrency and operational integration

Add explicit concurrent automatic execution only where needed, with isolated contexts. Complete idempotency, per-request coordination, late-response handling, and useful diagnostics. Verify behavior against the leave scenarios and cross-mode contract expectations.

No new project, database schema, scheduler, reminder subsystem, or consumer dispatch dependency is inherently mandated by these stages. Those choices should follow demonstrated ownership and persistence needs.

## 19. Acceptance scenarios for a future implementation

These scenarios describe proposed validation. They have not been executed as part of this documentation task.

| Scenario | Expected behavior |
| --- | --- |
| One manager, then HR | Manager handling leads to the configured HR stage; only HR approval permits leave assignment. |
| Manager rejection is provisional | The rejection is recorded and HR still receives the request when both manager outcomes route there. |
| Two parallel managers, any approval | Both tasks are activated; one approval satisfies the group. |
| Any approval with one rejection and one pending | The group remains waiting when rejection has no veto. |
| Any approval with all participants rejected | The group follows its configured rejection route. |
| Parallel all-approval | One approval is insufficient; all required approvals satisfy the group. |
| Sequential all-approval | The second task does not become active before the first required approval. |
| Sequential any-approval | An early approval can finish the group; a rejection can activate the next participant under the configured rule. |
| A satisfied group has outstanding tasks | Remaining tasks follow the selected cancellation, closure, or audit-only response policy. |
| Reuse one handler for two people | Each invocation receives the correct isolated parameters and has a distinct occurrence identity. |
| A task is created successfully | The approval stage remains waiting until the required human decision exists. |
| Publish a new revision while a request waits | The existing request keeps its original definition and policy for all future stages. |
| Start a request after publication | The new request selects the newly published validated revision. |
| Restart the application | Persisted progress resumes with the original revision and participant facts. |
| Publish an invalid revision | The invalid candidate is not made active; existing valid revisions remain usable. |
| A pinned revision is unavailable | Execution reports an explicit problem rather than substituting current policy. |
| Two responses arrive concurrently | Group completion and next-stage activation occur once under the application's consistency contract. |
| The same response is retried | Recorded decisions and business actions are not duplicated. |
| A late response arrives | It follows the documented completed-group policy and does not accidentally reopen completed work. |
| A required gate handler is absent | Validation or execution fails clearly instead of bypassing the prerequisite. |
| A gate blocks | The reported result and exposed state agree with the defined commit boundary. |
| Automatic completion routes cycle | Validation or a bounded execution limit stops the turn with useful diagnostics. |
| A later automatic step fails after earlier work | Retry and persisted progress follow an explicit transaction or checkpoint rule. |
| An essential effect fails | Failure is surfaced through the selected contract; leave is not falsely reported as assigned. |
| A supported definition runs in either mode | Shared constructs retain their meaning; unsupported constructs are rejected explicitly. |

## 20. Decisions still open

The original-policy behavior for existing requests is settled. The following choices are not settled:

1. Whether the first enhancement uses a reusable application approval coordinator or introduces first-class shared group operators immediately.
2. The exact group schema, outcome types, invocation identity, and compatible handler API extensions.
3. Whether any-approval rejection can veto, whether all responses must be collected, and which optional aggregation policies are actually needed.
4. What happens to outstanding tasks after the group is satisfied and how late responses are recorded.
5. When role selectors resolve to people, whether resolved membership is frozen, and how reassignment or unavailable approvers work.
6. How empty participant sets, duplicate participants, abstentions, delegation, and time limits behave if those features are required.
7. Which application-owned store supplies published revisions and how publication and handler validation integrate with deployment.
8. Which transaction, concurrency, checkpoint, and outbox conventions the relay host should support.
9. Whether concurrent automatic handlers are needed initially or whether parallel human task activation is the immediate requirement.
10. How handler implementation compatibility is maintained for long-lived pinned revisions.
11. The final boundary between relay diagnostics and the operational capabilities intentionally left to the full engine.

Reminders, escalation timers, compensation, an authoring UI, and explicit migration of active requests are possible future topics. They are not implicit requirements of the lightweight relay proposal or agreed implementation scope.

## 21. Source references and review boundary

The findings were checked against the relay interpreter, shared snapshot reader, context and result contracts, state-store interface, consumer host, and loan-approval example available in the workspace at the time of review.

| Source | Relevance |
| --- | --- |
| [WorkflowRelay.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Models/Relay/WorkflowRelay.cs) | Handler execution, gate/effect sequencing, routing, context mutation, and automatic follow-up. |
| [DefinitionJsonReader.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Models/Snapshot/DefinitionJsonReader.cs) | Shared snapshot construction, rule validation, hook-route uniqueness, and policy parsing. |
| [RelayContext.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Models/Relay/RelayContext.cs) | Invocation context and mutable parameter storage. |
| [RelayResult.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Models/Relay/RelayResult.cs) | Current result model. |
| [SnapshotHookRoute.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Models/Snapshot/SnapshotHookRoute.cs) | Current hook metadata and absence of a first-class participant-group model. |
| [IWorkflowRelayStateStore.cs](../../HaleyAbstractions.Core/HaleyAbstractionsCore/WorkFlowEngine/Interfaces/Services/IWorkflowRelayStateStore.cs) | State persistence boundary. |
| [WorkflowRelayHost.cs](../../HaleyFlow.Consumer/HaleyFlowConsumer/Services/WorkflowRelayHost.cs) | Startup initialization and load/execute/save behavior. |
| [WorkflowRelayBase.cs](../../HaleyFlow.Consumer/HaleyFlowConsumer/Abstractions/WorkflowRelayBase.cs) | Relay definition and handler-registration integration. |
| [InMemoryWorkflowRelayStateStore.cs](../../HaleyFlow.Consumer/HaleyFlowConsumer/Services/InMemoryWorkflowRelayStateStore.cs) | Default in-memory state behavior. |
| [LoanApprovalRelay.cs](../WFE.Lib/UseCases/LoanApproval/LoanApprovalRelay.cs) | Example handler registration and inline execution. |
| [Loan approval policy](../WFE.Lib/UseCases/LoanApproval/policy.loan_approval.json) | Example approval parameters carried by the policy. |
| [Current relay execution contract](RELAY_EXECUTION_CONTRACT.md) | Existing execution semantics to preserve or deliberately revise. |
| [HaleyFlow protocol](HaleyFlowProtocol_v1.html) | Shared protocol vocabulary and acknowledgement semantics. |
| [Engine guide](engine_guide.html) | Full engine responsibilities and operational model. |

References to sibling repositories require the shared workspace layout to resolve. These documents record a source-based assessment and proposed design; they do not claim that the enhanced model already exists or that production behavior has been load-tested.

Only documentation is changed by this task. There is no runtime implementation change, no SQL or database schema change, and no database execution. The purpose is to make the tradeoffs and missing work reviewable before an implementation decision.
