using Haley.Abstractions;

namespace WFE.Test.Fixtures;

internal sealed class FixedEngineAccessor(IWorkFlowEngine engine) : IWorkFlowEngineAccessor {
    public Task<IWorkFlowEngine> GetEngineAsync(CancellationToken ct = default) => Task.FromResult(engine);
}
