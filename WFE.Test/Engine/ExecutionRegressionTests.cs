using Haley.Services;

///;
using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using WFE.Test.Fixtures;
using Xunit;

namespace WFE.Test.Engine;

public sealed class ExecutionRegressionTests {
    private const string OrderedPolicy = """
        {"defName":"regression","rules":[{"state":"active","via":11,
        "complete":{"success":12,"failure":14},"emit":[
        {"route":"gate","order":1},{"route":"effect","type":"effect","order":1},
        {"route":"later","order":2000},{"route":"unordered"}]}]}
        """;

    [DatabaseFact]
    public async Task AllValidationConsumersAndEveryPhaseMustFinishBeforeAdvancing() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, OrderedPolicy);
        var start = await flow.StartAsync();
        var validationAck = Assert.Single(start.LifecycleAckGuids);
        await flow.Engine.AckAsync(flow.Consumers[0], validationAck, AckOutcome.Processed);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await flow.Engine.AckAsync(flow.Consumers[1], validationAck, AckOutcome.Processed);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        // Replaying another lifecycle ACK must not release effects or another order.
        await flow.Engine.AckAsync(flow.Consumers[0], validationAck, AckOutcome.Processed);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        var blocked = await flow.Engine.TriggerAsync(new LifeCycleTriggerRequest { InstanceGuid = start.InstanceGuid, Event = "12" });
        Assert.Equal("BlockedByPendingBlockingHook", blocked.Reason);
        var gate = await flow.HookAckAsync("gate");
        await flow.Engine.AckAsync(flow.Consumers[0], gate, AckOutcome.Processed);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await flow.Engine.AckAsync(flow.Consumers[1], gate, AckOutcome.Processed);
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await flow.AckAllAsync(await flow.HookAckAsync("effect"), AckOutcome.Failed);
        Assert.Equal(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await flow.AckAllAsync(await flow.HookAckAsync("later"));
        Assert.Equal(4, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await flow.AckAllAsync(await flow.HookAckAsync("unordered"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
        Assert.Equal(12, await db.ScalarAsync<int>("SELECT `next` FROM lc_next"));
    }

    [DatabaseFact]
    public async Task SpecificPolicyRuleIsTheOnlyEmittedRule() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[
              {"state":"active","emit":[{"route":"fallback"}]},
              {"state":"active","via":11,"emit":[{"route":"old"}]},
              {"state":"active","via":11,"emit":[{"route":"specific"}]}]}
            """);
        await flow.StartAsync();
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook"));
        Assert.Equal("specific", await db.ScalarAsync<string>("SELECT r.name FROM hook h JOIN hook_route r ON r.id=h.route_id"));
    }

    [DatabaseFact]
    public async Task SelfLoopsProduceFreshOccurrencesAndRequestReplayDoesNotDuplicateThem() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, consumerCount: 1);
        var start = await flow.StartAsync();
        await flow.AckAllAsync(start.LifecycleAckGuids[0]);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => flow.Engine.TriggerAsync(new LifeCycleTriggerRequest {
            InstanceGuid = start.InstanceGuid, Event = "13", RequestId = "same-loop", ExpectedLifeCycleId = start.LifeCycleId
        })));
        Assert.All(results, r => Assert.True(r.Applied, r.Reason));
        Assert.Single(results.Select(r => r.LifeCycleId).Distinct());
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
        await flow.AckAllAsync(results[0].LifecycleAckGuids[0]);
        var next = await flow.Engine.TriggerAsync(new LifeCycleTriggerRequest {
            InstanceGuid = start.InstanceGuid, Event = "13", RequestId = "next-loop", ExpectedLifeCycleId = results[0].LifeCycleId
        });
        Assert.True(next.Applied);
        Assert.NotEqual(results[0].LifeCycleId, next.LifeCycleId);
    }

    [DatabaseFact]
    public async Task RecoveryRepairsFailureAfterTerminalAckAndDoesNotDuplicateDispatch() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[{"state":"active","via":11,"complete":{"success":12},"emit":[{"route":"gate"}]}]}
            """, 1);
        var start = await flow.StartAsync();
        await db.ExecuteAsync("CREATE TRIGGER fail_hook BEFORE INSERT ON hook_ack FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected crash boundary'");
        await Assert.ThrowsAnyAsync<Exception>(() => flow.AckAllAsync(start.LifecycleAckGuids[0]));
        Assert.Equal(3, await db.ScalarAsync<int>("SELECT status FROM ack_consumer"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await db.ExecuteAsync("DROP TRIGGER fail_hook");
        await flow.Engine.RunMonitorOnceAsync(flow.Consumers[0]);
        var hookAck = await flow.HookAckAsync("gate");
        await db.ExecuteAsync("CREATE TRIGGER fail_complete BEFORE INSERT ON lcn_ack FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected completion boundary'");
        await Assert.ThrowsAnyAsync<Exception>(() => flow.AckAllAsync(hookAck));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
        await db.ExecuteAsync("DROP TRIGGER fail_complete");
        await flow.Engine.RunMonitorOnceAsync(flow.Consumers[0]);
        await flow.Engine.RunMonitorOnceAsync(flow.Consumers[0]);
        await flow.AckAllAsync(hookAck);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
    }

    [DatabaseFact]
    public async Task AnyAckSatisfiesOnlyItsOwnHook() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[{"state":"active","via":11,"emit":[
              {"route":"a","order":1,"ack_mode":"any"},{"route":"b","order":1,"ack_mode":"any"}]}]}
            """);
        var start = await flow.StartAsync();
        await flow.AckAllAsync(start.LifecycleAckGuids[0]);
        await flow.Engine.AckAsync(flow.Consumers[0], await flow.HookAckAsync("a"), AckOutcome.Processed);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
        await flow.Engine.AckAsync(flow.Consumers[1], await flow.HookAckAsync("b"), AckOutcome.Processed);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
    }

    [DatabaseFact]
    public async Task HookDeliveryFieldsRemainConsistentOnRetry() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","params":[{"code":"p","data":{"value":7}}],
             "rules":[{"state":"active","via":11,"params":["p"],"complete":{"success":12},
              "emit":[{"route":"gate","order":1500,"ack_mode":"any","not_before":"2020-01-01T00:00:00Z","deadline":"2099-01-01T00:00:00Z"}]}]}
            """, 1);
        var start = await flow.StartAsync();
        await flow.AckAllAsync(start.LifecycleAckGuids[0]);
        await db.ExecuteAsync("UPDATE ack_consumer SET next_due=UTC_TIMESTAMP()-INTERVAL 1 SECOND WHERE status=1");
        var retry = Assert.IsAssignableFrom<ILifeCycleHookEvent>(Assert.Single(await flow.Engine.AckManager.ListDueHookDispatchAsync(flow.Consumers[0], 1, 60, 0, 20)).Event);
        for (var i = 0; i < 100 && !flow.Events.OfType<ILifeCycleHookEvent>().Any(); i++) await Task.Delay(10);
        var first = flow.Events.OfType<ILifeCycleHookEvent>().First();
        Assert.Equal(first.OrderSeq, retry.OrderSeq);
        Assert.Equal(first.AckMode, retry.AckMode);
        Assert.Equal(first.LifeCycleId, retry.LifeCycleId);
        Assert.Equal(first.NotBefore, retry.NotBefore);
        Assert.Equal(first.Deadline, retry.Deadline);
        Assert.Equal(Assert.Single(first.Params!).Code, Assert.Single(retry.Params!).Code);
        Assert.Null(first.OnSuccessEvent);
        Assert.Null(retry.OnSuccessEvent);
    }

    [DatabaseFact]
    public async Task ConsumerContinuationWaitsForAllAcksAndCanBeReplayedAfterAdvancement() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db);
        var start = await flow.StartAsync();
        var ack = start.LifecycleAckGuids[0];
        await flow.Engine.AckAsync(flow.Consumers[0], ack, AckOutcome.Processed);
        LifeCycleTriggerRequest Request() => new() { InstanceGuid=start.InstanceGuid, Event="13", RequestId="continuation", SourceAckGuid=ack };
        Assert.False((await flow.Engine.TriggerAsync(Request())).Applied);
        await flow.Engine.AckAsync(flow.Consumers[1], ack, AckOutcome.Processed);
        var result = await flow.Engine.TriggerAsync(Request());
        Assert.True(result.Applied);
        Assert.Equal(result.LifeCycleId, (await flow.Engine.TriggerAsync(Request())).LifeCycleId);
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
    }

    [DatabaseFact]
    public async Task EngineImportsRejectTheSameInvalidPolicyAsRelay() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db);
        const string invalid = """
            {"defName":"regression","rules":[{"state":"active","emit":[{"route":"gate","send":"always"}]}]}
            """;
        Assert.Throws<InvalidOperationException>(() => WorkflowRelay.FromJson(WorkflowScenario.Definition, invalid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Engine.BlueprintImporter.ImportPolicyJsonAsync(1, "tests", invalid));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM policy"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Engine.BlueprintImporter.ImportDefinitionJsonAsync(1, "tests",
            WorkflowScenario.Definition.Replace("\"is_initial\":true", "\"is_initial\":false")));
    }

    [DatabaseFact]
    public async Task SharedInProcessProxyKeepsConsumerDeliveriesSeparate() {
        await using var db = await TestDatabase.CreateAsync();
        var scenario = await WorkflowScenario.CreateAsync(db,
            """{"defName":"regression","rules":[{"state":"active","emit":[{"route":"review","type":"gate","order":1}]}]}""");
        var proxy = new InProcessEngineProxy(new FixedEngineAccessor(scenario.Engine));
        // Subscribe before triggering so the proxy sees the immediate deliveries.
        await proxy.GetDueTransitionsAsync(scenario.Consumers[0], 0, 60, 0, 100);
        var result = await scenario.StartAsync();
        Assert.True(result.Applied);
        foreach (var consumer in scenario.Consumers) {
            var transitions = await proxy.GetDueTransitionsAsync(consumer, 0, 60, 0, 100);
            Assert.Single(transitions);
            Assert.Equal(consumer, transitions[0].ConsumerId);
            await scenario.Engine.AckAsync(consumer, transitions[0].AckGuid, AckOutcome.Processed);
        }
        foreach (var consumer in scenario.Consumers) {
            var hooks = await proxy.GetDueHooksAsync(consumer, 0, 60, 0, 100);
            Assert.Single(hooks);
            Assert.Equal(consumer, hooks[0].ConsumerId);
        }
    }
}
