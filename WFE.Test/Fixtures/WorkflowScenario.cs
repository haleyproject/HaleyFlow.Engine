using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Services;
using Haley.Utils;
using System.Collections.Concurrent;

namespace WFE.Test.Fixtures;

internal sealed class WorkflowScenario {
    public const string Definition = """
        {"name":"regression","states":[
          {"name":"idle","is_initial":true},{"name":"active"},
          {"name":"done","is_final":true},{"name":"failed","is_final":true}],
         "transitions":[{"from":"idle","to":"active","event":11},
          {"from":"active","to":"done","event":12},
          {"from":"active","to":"active","event":13},
          {"from":"active","to":"failed","event":14}]}
        """;
    public TestDatabase Database { get; }
    public WorkFlowEngine Engine { get; }
    public List<long> Consumers { get; } = new();
    public ConcurrentQueue<ILifeCycleEvent> Events { get; } = new();
    private WorkflowScenario(TestDatabase database, int effectTimeoutSeconds) {
        Database = database;
        Engine = new WorkFlowEngine(new MariaWorkFlowDAL(database.Gateway, "test"), new WorkFlowEngineOptions {
            AckGateEnabled = true,
            AckPendingResendAfter = TimeSpan.FromMilliseconds(1), AckDeliveredResendAfter = TimeSpan.FromMilliseconds(1),
            EffectTimeoutSeconds = effectTimeoutSeconds,
            ResolveConsumers = (kind, version, ct) => Task.FromResult<IReadOnlyList<long>>(Consumers)
        });
        Engine.EventRaised += evt => { Events.Enqueue(evt); return Task.CompletedTask; };
    }
    public static async Task<WorkflowScenario> CreateAsync(TestDatabase database, string? policy = null, int consumerCount = 2, int effectTimeoutSeconds = 60) {
        var scenario = new WorkflowScenario(database, effectTimeoutSeconds);
        await scenario.Engine.BlueprintImporter.ImportDefinitionJsonAsync(1, "tests", Definition);
        if (policy != null) await scenario.Engine.BlueprintImporter.ImportPolicyJsonAsync(1, "tests", policy);
        for (var i = 0; i < consumerCount; i++)
            scenario.Consumers.Add(await scenario.Engine.RegisterConsumerAsync(1, Guid.NewGuid().ToString()));
        return scenario;
    }
    public Task<LifeCycleTriggerResult> StartAsync(string? entity = null) => Engine.TriggerAsync(new LifeCycleTriggerRequest {
        EnvCode = 1, DefName = "regression", EntityId = entity ?? Guid.NewGuid().ToString(), Event = "11"
    });
    public async Task AckAllAsync(string ack, AckOutcome outcome = AckOutcome.Processed) {
        foreach (var consumer in Consumers) await Engine.AckAsync(consumer, ack, outcome);
    }
    public Task<string> HookAckAsync(string route) => Database.ScalarAsync<string>(
        "SELECT a.guid FROM ack a JOIN hook_ack ha ON ha.ack_id=a.id JOIN hook_lc hl ON hl.id=ha.hook_id JOIN hook h ON h.id=hl.hook_id JOIN hook_route r ON r.id=h.route_id WHERE r.name=@route ORDER BY hl.id DESC LIMIT 1", ("@route", route));
}
