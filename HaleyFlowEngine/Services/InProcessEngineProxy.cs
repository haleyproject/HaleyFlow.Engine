using System.Collections.Concurrent;
using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using System.Threading.Channels;

namespace Haley.Services;

/// <summary>
/// In-process implementation of <see cref="ILifeCycleEngineProxy"/>.
///
/// BELONGS IN THE ENGINE PROJECT — NOT THE CONSUMER PROJECT.
/// This class resolves <see cref="IWorkFlowEngine"/> via <see cref="IWorkFlowEngineAccessor"/>
/// and holds a direct reference to it after first use. That direct coupling is exactly why it
/// lives here: the engine project is the only one allowed to know about engine internals.
/// The consumer project only ever sees <see cref="ILifeCycleEngineProxy"/> (the abstraction)
/// and is completely unaware of which implementation is active.
///
/// ── WHY A PROXY? ────────────────────────────────────────────────────────────
/// The consumer is designed to be deployment-agnostic:
///   • In-process (same .NET host): wire up this class. Zero network overhead.
///   • Remote (separate process/machine): wire up an HttpEngineProxy (lives in consumer package)
///     that issues HTTP calls to wherever the engine is running.
/// The consumer service itself never changes — only the proxy registration changes.
///
/// ── LAZY INITIALISATION ─────────────────────────────────────────────────────
/// The engine instance is NOT resolved at construction time. <see cref="IWorkFlowEngine"/> is
/// built lazily inside <see cref="IWorkFlowEngineAccessor.GetEngineAsync"/> and cannot be
/// registered in DI directly. The proxy defers resolution to the first method call, at which
/// point the host has finished starting and the engine is guaranteed to be ready.
/// A SemaphoreSlim ensures exactly one initialisation even under concurrent first calls.
///
/// ── HOW EVENT DELIVERY WORKS ────────────────────────────────────────────────
/// <see cref="GetDueTransitionsAsync"/> and <see cref="GetDueHooksAsync"/> are NOT ordinary
/// queries — they are the consumer-side end of the engine's push-delivery mechanism.
///
/// When the engine fires <see cref="IWorkFlowEngine.EventRaised"/> (on trigger, monitor resend,
/// or hook advancement), <see cref="OnEventRaised"/> intercepts it and writes the event into
/// one of two unbounded in-memory channels (one for transitions, one for hooks). TryWrite is
/// non-blocking, so the engine's own thread is never delayed.
///
/// The consumer's poll loop then calls GetDueTransitionsAsync / GetDueHooksAsync each tick,
/// which drain whatever items are ready in those channels.
///
/// Compare this to <see cref="ILifeCycleRuntimeBus.ListPendingAcksAsync"/>: that is a plain
/// on-demand query the consumer initiates against the engine DB — no event push involved.
///
/// ── TRADE-OFFS ──────────────────────────────────────────────────────────────
/// • No durability: channel items are lost on crash. The engine monitor re-sends after the
///   AckPendingResendAfter window, so at-least-once delivery is preserved.
/// • No fan-out: one proxy instance = one consumer subscription. Register multiple instances
///   for multiple independent consumers in the same process.
/// • Zero latency: ideal for integration tests and lightweight monolith deployments.
/// </summary>
public sealed class InProcessEngineProxy : ILifeCycleEngineProxy {

    private readonly IWorkFlowEngineAccessor _engineAccessor;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Two separate channels: one for lifecycle state transitions, one for hook events.
    // Created at field-init time — always ready to receive events once the engine is wired up.
    private readonly ConcurrentDictionary<long, Channel<ILifeCycleDispatchItem>> _transitions = new();
    private readonly ConcurrentDictionary<long, Channel<ILifeCycleDispatchItem>> _hooks = new();

    private IWorkFlowEngine? _engine;

    /// <inheritdoc/>
    public event Func<LifeCycleNotice, Task>? NoticeRaised;

    public InProcessEngineProxy(IWorkFlowEngineAccessor engineAccessor) {
        _engineAccessor = engineAccessor ?? throw new ArgumentNullException(nameof(engineAccessor));
    }

    // ── Lazy engine resolution ────────────────────────────────────────────────

    private async ValueTask<IWorkFlowEngine> EnsureEngineAsync(CancellationToken ct) {
        if (_engine != null) return _engine;
        await _initLock.WaitAsync(ct);
        try {
            if (_engine != null) return _engine;
            var engine = await _engineAccessor.GetEngineAsync(ct);
            engine.EventRaised += OnEventRaised;
            engine.NoticeRaised += OnEngineNoticeRaised;
            _engine = engine;
            return _engine;
        } finally {
            _initLock.Release();
        }
    }

    // ── Event ingress ─────────────────────────────────────────────────────────
    // Called on the engine's own thread. Must be fast and non-blocking.
    // TryWrite on an unbounded channel never blocks and never fails.
    // Complete events travel with transition events because the consumer polls them from the same lane.

    private Task OnEventRaised(ILifeCycleEvent evt) {
        var item = new InProcessDispatchItem(evt);
        var writer = evt.Kind == LifeCycleEventKind.Hook
            ? _hooks.GetOrAdd(evt.ConsumerId, _ => Channel.CreateUnbounded<ILifeCycleDispatchItem>()).Writer
            : _transitions.GetOrAdd(evt.ConsumerId, _ => Channel.CreateUnbounded<ILifeCycleDispatchItem>()).Writer;
        writer.TryWrite(item);
        return Task.CompletedTask;
    }

    private Task OnEngineNoticeRaised(LifeCycleNotice n) {
        var h = NoticeRaised;
        if (h == null) return Task.CompletedTask;
        foreach (Func<LifeCycleNotice, Task> sub in h.GetInvocationList()) {
            var captured = sub;
            _ = Task.Run(async () => { try { await captured(n); } catch { } });
        }
        return Task.CompletedTask;
    }

    // ── Event delivery (poll) ─────────────────────────────────────────────────
    // These drain the in-memory channels. EnsureEngineAsync is called first to guarantee
    // event subscription is in place before any items could be missed.
    // The ackStatus/ttlSeconds/skip parameters are irrelevant in-process
    // (no DB paging, no TTL concept for channels) but are part of the contract so the
    // consumer's poll loop is deployment-agnostic.

    public async Task<IReadOnlyList<ILifeCycleDispatchItem>> GetDueTransitionsAsync(long consumerId, int ackStatus, int ttlSeconds, int skip, int take, CancellationToken ct = default) {
        await EnsureEngineAsync(ct);
        var result = new List<ILifeCycleDispatchItem>();
        while (result.Count < take && _transitions.GetOrAdd(consumerId, _ => Channel.CreateUnbounded<ILifeCycleDispatchItem>()).Reader.TryRead(out var item))
            result.Add(item);
        return result;
    }

    public async Task<IReadOnlyList<ILifeCycleDispatchItem>> GetDueHooksAsync(long consumerId, int ackStatus, int ttlSeconds, int skip, int take, CancellationToken ct = default) {
        await EnsureEngineAsync(ct);
        var result = new List<ILifeCycleDispatchItem>();
        while (result.Count < take && _hooks.GetOrAdd(consumerId, _ => Channel.CreateUnbounded<ILifeCycleDispatchItem>()).Reader.TryRead(out var item))
            result.Add(item);
        return result;
    }

    // ── ILifeCycleConsumerBus ─────────────────────────────────────────────────

    public async Task AckAsync(long consumerId, string ackGuid, AckOutcome outcome, string? message = null, DateTimeOffset? retryAt = null, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).AckAsync(consumerId, ackGuid, outcome, message, retryAt, ct);

    public async Task AckAsync(int envCode, string consumerGuid, string ackGuid, AckOutcome outcome, string? message = null, DateTimeOffset? retryAt = null, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).AckAsync(envCode, consumerGuid, ackGuid, outcome, message, retryAt, ct);

    public async Task<long> RegisterConsumerAsync(int envCode, string consumerGuid, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).RegisterConsumerAsync(envCode, consumerGuid, ct);

    public async Task BeatConsumerAsync(int envCode, string consumerGuid, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).BeatConsumerAsync(envCode, consumerGuid, ct);

    public async Task<int> RegisterEnvironmentAsync(int envCode, string? envDisplayName, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).RegisterEnvironmentAsync(envCode, envDisplayName, ct);

    public async Task<long?> GetDefinitionIdAsync(int envCode, string definitionName, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetDefinitionIdAsync(envCode, definitionName, ct);

    public async Task<long> ImportDefinitionJsonAsync(int envCode, string envDisplayName, string definitionJson, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ImportDefinitionJsonAsync(envCode, envDisplayName, definitionJson, ct);

    public async Task<long> ImportPolicyJsonAsync(int envCode, string envDisplayName, string policyJson, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ImportPolicyJsonAsync(envCode, envDisplayName, policyJson, ct);

    // ── ILifeCycleRuntimeBus ──────────────────────────────────────────────────
    // Every method is a straight pass-through to the engine.
    // An HttpEngineProxy would POST/GET the engine's REST endpoints instead.

    public async Task<LifeCycleTriggerResult> TriggerAsync(LifeCycleTriggerRequest req, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).TriggerAsync(req, ct);

    public async Task<LifeCycleInstanceData?> GetInstanceDataAsync(LifeCycleInstanceKey key, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetInstanceDataAsync(key, ct);

    public async Task<string?> GetInstanceContextAsync(LifeCycleInstanceKey key, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetInstanceContextAsync(key, ct);

    public async Task<int> SetInstanceContextAsync(LifeCycleInstanceKey key, string? context, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).SetInstanceContextAsync(key, context, ct);

    public async Task ClearCacheAsync(CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ClearCacheAsync(ct);

    public async Task InvalidateAsync(int envCode, string defName, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).InvalidateAsync(envCode, defName, ct);

    public async Task InvalidateAsync(long defVersionId, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).InvalidateAsync(defVersionId, ct);

    public async Task<string?> GetTimelineJsonAsync(LifeCycleInstanceKey key, TimelineDetail detail = TimelineDetail.Detailed, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetTimelineJsonAsync(key, detail, ct);

    public async Task<IReadOnlyList<InstanceRefItem>> GetInstanceRefsAsync(int envCode, string defName, LifeCycleInstanceFlag flags, int skip, int take, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetInstanceRefsAsync(envCode, defName, flags, skip, take, ct);

    public async Task<long> UpsertRuntimeAsync(RuntimeLogByNameRequest req, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).UpsertRuntimeAsync(req, ct);

    public async Task<DbRows> ListInstancesAsync(int envCode, string? defName, bool runningOnly, int skip, int take, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ListInstancesAsync(envCode, defName, runningOnly, skip, take, ct);

    public async Task<DbRows> ListInstancesByStatusAsync(int envCode, string? defName, LifeCycleInstanceFlag statusFlags, int skip, int take, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ListInstancesByStatusAsync(envCode, defName, statusFlags, skip, take, ct);

    public async Task<DbRows> ListPendingAcksAsync(int envCode, int skip, int take, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ListPendingAcksAsync(envCode, skip, take, ct);

    public async Task<bool> SuspendInstanceAsync(string instanceGuid, string? message, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).SuspendInstanceAsync(instanceGuid, message, ct);

    public async Task<bool> ResumeInstanceAsync(string instanceGuid, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ResumeInstanceAsync(instanceGuid, ct);

    public async Task<bool> FailInstanceAsync(string instanceGuid, string? message, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).FailInstanceAsync(instanceGuid, message, ct);

    public async Task<LifeCycleTriggerResult> ReopenAsync(string instanceGuid, string actor, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ReopenAsync(instanceGuid, actor, ct);

    public async Task<bool> UnsuspendAsync(string instanceGuid, string actor, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).UnsuspendAsync(instanceGuid, actor, ct);

    public async Task<WorkflowDefinitionSnapshot?> GetDefinitionSnapshotAsync(int envCode, string definitionName, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).GetDefinitionSnapshotAsync(envCode, definitionName, ct);

    public async Task<BackfillImportResult> ImportBackfillAsync(WorkflowBackfillObject obj, CancellationToken ct = default)
        => await (await EnsureEngineAsync(ct)).ImportBackfillAsync(obj, ct);

    // ── Private adapter ───────────────────────────────────────────────────────
    // Wraps ILifeCycleEvent into ILifeCycleDispatchItem with sensible in-process defaults:
    //   AckId = 0        — not used by the dispatch pipeline
    //   AckStatus = 0    — "pending", triggers normal dispatch
    //   TriggerCount = 1 — first (and only) in-process delivery
    //   NextDue = null   — no scheduled retry; engine monitor handles resends

    private sealed class InProcessDispatchItem : ILifeCycleDispatchItem {
        private readonly ILifeCycleEvent _event;
        public InProcessDispatchItem(ILifeCycleEvent evt) => _event = evt;

        public LifeCycleEventKind Kind => _event.Kind;
        public long AckId => 0;
        public string AckGuid => _event.AckGuid;
        public long ConsumerId => _event.ConsumerId;
        public int AckStatus => 0;
        public int TriggerCount => 1;
        // In-process delivery has no DB row; MaxTrigger = 0 tells the monitor there is no budget limit.
        public int MaxTrigger => 0;
        public DateTime LastTrigger => DateTime.UtcNow;
        public DateTime? NextDue => null;
        public ILifeCycleEvent Event => _event;
    }
}
