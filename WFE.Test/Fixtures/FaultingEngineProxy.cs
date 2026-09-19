using Haley.Abstractions;
using Haley.Models;
using System.Reflection;

namespace WFE.Test.Fixtures;

public class FaultingEngineProxy : DispatchProxy {
    public ILifeCycleEngineProxy Target { get; set; } = null!;
    public bool FailBeforeTrigger { get; set; }
    public bool FailAfterTrigger { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        if (method!.Name == nameof(ILifeCycleEngineProxy.TriggerAsync)) return TriggerAsync(method, args);
        return method.Invoke(Target, args);
    }
    private async Task<LifeCycleTriggerResult> TriggerAsync(MethodInfo method, object?[]? args) {
        if (FailBeforeTrigger) { FailBeforeTrigger=false; throw new IOException("Simulated crash after ACK."); }
        var result = await (Task<LifeCycleTriggerResult>)method.Invoke(Target, args)!;
        if (FailAfterTrigger) { FailAfterTrigger=false; throw new IOException("Simulated lost trigger response."); }
        return result;
    }
}
