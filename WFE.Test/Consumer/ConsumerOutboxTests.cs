using Haley.Abstractions;
using Haley.Enums;
using Haley.Internal;
using Haley.Services;
using System.Reflection;
using WFE.Test.Fixtures;
using Xunit;

namespace WFE.Test.Consumer;

public sealed class ConsumerOutboxTests {
    [DatabaseFact]
    public async Task CrashesAroundContinuationRetainTheOutboxAndDoNotRepeatBusinessWork() {
        foreach (var afterApplied in new[] { false, true }) {
            await using var engineDb = await TestDatabase.CreateAsync();
            await using var consumerDb = await TestDatabase.CreateAsync("consumer.sql");
            var flow = await WorkflowScenario.CreateAsync(engineDb, consumerCount:1);
            var proxy = DispatchProxy.Create<ILifeCycleEngineProxy, FaultingEngineProxy>();
            var faults = (FaultingEngineProxy)(object)proxy;
            faults.Target = new InProcessEngineProxy(new FixedEngineAccessor(flow.Engine));
            faults.FailAfterTrigger = afterApplied;
            faults.FailBeforeTrigger = !afterApplied;
            var calls = 0;
            var manager = new WorkFlowConsumerManager(proxy, new MariaServiceDAL(consumerDb.Gateway,"test"),
                new WrapperServiceProvider(() => new ContinuationWrapper(AckOutcome.Processed,13,()=>calls++)));
            Configure(manager, flow.Consumers[0], await engineDb.ScalarAsync<long>("SELECT id FROM definition"));
            await flow.StartAsync();
            await engineDb.ExecuteAsync("UPDATE ack_consumer SET next_due=UTC_TIMESTAMP()-INTERVAL 1 SECOND");
            var item = Assert.Single(await flow.Engine.AckManager.ListDueLifecycleDispatchAsync(flow.Consumers[0],1,60,0,10));
            await ProcessAsync(manager,item);
            Assert.Equal(1, await consumerDb.ScalarAsync<int>("SELECT status FROM outbox"));
            Assert.Equal(1,calls);
            // This represents a delivery after a process restart: durable decision already exists.
            await ProcessAsync(manager,item);
            Assert.Equal(3, await consumerDb.ScalarAsync<int>("SELECT status FROM outbox"));
            Assert.Equal(2, await engineDb.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
            Assert.Equal(1,calls);
        }
    }

    [DatabaseFact]
    public async Task RetryAndFailedOutcomesDoNotFireRecordedNextEvents() {
        foreach (var outcome in new[] { AckOutcome.Retry, AckOutcome.Failed, AckOutcome.Delivered }) {
            await using var engineDb = await TestDatabase.CreateAsync();
            await using var consumerDb = await TestDatabase.CreateAsync("consumer.sql");
            var flow = await WorkflowScenario.CreateAsync(engineDb, consumerCount:1);
            var proxy = new InProcessEngineProxy(new FixedEngineAccessor(flow.Engine));
            var manager = new WorkFlowConsumerManager(proxy,new MariaServiceDAL(consumerDb.Gateway,"test"),
                new WrapperServiceProvider(()=>new ContinuationWrapper(outcome,13,()=>{})));
            Configure(manager,flow.Consumers[0],await engineDb.ScalarAsync<long>("SELECT id FROM definition"));
            await flow.StartAsync();
            await engineDb.ExecuteAsync("UPDATE ack_consumer SET next_due=UTC_TIMESTAMP()-INTERVAL 1 SECOND");
            var item = Assert.Single(await flow.Engine.AckManager.ListDueLifecycleDispatchAsync(flow.Consumers[0],1,60,0,10));
            await ProcessAsync(manager,item);
            Assert.Equal(1,await engineDb.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
            Assert.Equal(1,await consumerDb.ScalarAsync<int>("SELECT next_event IS NULL FROM outbox"));
        }
    }

    [DatabaseFact]
    public async Task HandledBusinessRejectionCanChooseAFailureTransition() {
        await using var engineDb = await TestDatabase.CreateAsync();
        await using var consumerDb = await TestDatabase.CreateAsync("consumer.sql");
        var flow = await WorkflowScenario.CreateAsync(engineDb, consumerCount:1);
        var manager = new WorkFlowConsumerManager(new InProcessEngineProxy(new FixedEngineAccessor(flow.Engine)),
            new MariaServiceDAL(consumerDb.Gateway,"test"),
            new WrapperServiceProvider(()=>new ContinuationWrapper(AckOutcome.Processed,14,()=>{})));
        Configure(manager,flow.Consumers[0],await engineDb.ScalarAsync<long>("SELECT id FROM definition"));
        await flow.StartAsync();
        await engineDb.ExecuteAsync("UPDATE ack_consumer SET next_due=UTC_TIMESTAMP()-INTERVAL 1 SECOND");
        await ProcessAsync(manager,Assert.Single(await flow.Engine.AckManager.ListDueLifecycleDispatchAsync(flow.Consumers[0],1,60,0,10)));
        Assert.Equal("failed",await engineDb.ScalarAsync<string>("SELECT s.name FROM instance i JOIN state s ON s.id=i.current_state"));
    }

    private static void Configure(WorkFlowConsumerManager manager,long consumer,long definition) {
        typeof(WorkFlowConsumerManager).GetField("_consumerId",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(manager,consumer);
        var registry=(WrapperRegistry)typeof(WorkFlowConsumerManager).GetField("_registry",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(manager)!;
        registry.Register(definition,typeof(ContinuationWrapper),"regression");
    }
    private static Task ProcessAsync(WorkFlowConsumerManager manager,ILifeCycleDispatchItem item) =>
        (Task)typeof(WorkFlowConsumerManager).GetMethod("ProcessItemAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(manager,new object[]{item,CancellationToken.None})!;
}
