using Haley.Enums;
using Haley.Models;
using Haley.Services;
using WFE.Test.Fixtures;
using Xunit;

namespace WFE.Test.Engine;

public sealed class RecoveryAndImportTests {
    [DatabaseFact]
    public async Task TimeoutFailureRemainsRetryableAndConcurrentMonitorsApplyItOnce() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","timeouts":[{"state":"active","timeout_minutes":1,"timeout_event":13}]}
            """, 1);
        var start = await flow.StartAsync();
        await db.ExecuteAsync("UPDATE lifecycle SET created=UTC_TIMESTAMP()-INTERVAL 2 MINUTE");
        await db.ExecuteAsync("CREATE TRIGGER fail_timeout BEFORE INSERT ON lifecycle FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected timeout failure'");
        await flow.Engine.RunMonitorOnceInternalAsync((kind, ct) => Task.FromResult<IReadOnlyList<long>>(flow.Consumers));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lc_timeout"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
        // A marker from the old crash window must not suppress recovery of the still-current entry.
        await db.ExecuteAsync("INSERT INTO lc_timeout (lc_id) SELECT id FROM lifecycle");
        await db.ExecuteAsync("DROP TRIGGER fail_timeout");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => flow.Engine.RunMonitorOnceInternalAsync((kind, ct) => Task.FromResult<IReadOnlyList<long>>(flow.Consumers))));
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lc_timeout"));
    }

    [DatabaseFact]
    public async Task ResendingAnEffectDoesNotExtendItsExecutionDeadline() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[{"state":"active","via":11,"emit":[{"route":"effect","type":"effect"}]}]}
            """, 1);
        var start = await flow.StartAsync();
        await flow.AckAllAsync(start.LifecycleAckGuids[0]);
        await db.ExecuteAsync("UPDATE ack a JOIN hook_ack ha ON ha.ack_id=a.id SET a.created=UTC_TIMESTAMP()-INTERVAL 2 MINUTE; UPDATE ack_consumer SET last_trigger=UTC_TIMESTAMP(), next_due=UTC_TIMESTAMP()-INTERVAL 1 SECOND WHERE status=1");
        await flow.Engine.RunMonitorOnceAsync(flow.Consumers[0]);
        Assert.Equal(4, await db.ScalarAsync<int>("SELECT ac.status FROM ack_consumer ac JOIN hook_ack ha ON ha.ack_id=ac.ack_id"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
    }

    [DatabaseFact]
    public async Task FailedValidationUsesDurableFailureContinuationAndNeverReleasesHooks() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[{"state":"active","via":11,"complete":{"failure":14},"emit":[{"route":"gate"}]}]}
            """);
        var start = await flow.StartAsync();
        await db.ExecuteAsync("CREATE TRIGGER fail_branch BEFORE INSERT ON lifecycle FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected branch failure'");
        await Assert.ThrowsAnyAsync<Exception>(() => flow.Engine.AckAsync(flow.Consumers[0], start.LifecycleAckGuids[0], AckOutcome.Failed));
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT status FROM lc_execution"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM hook_ack"));
        await db.ExecuteAsync("DROP TRIGGER fail_branch");
        await flow.Engine.RunMonitorOnceAsync(flow.Consumers[0]);
        Assert.Equal("failed", await db.ScalarAsync<string>("SELECT s.name FROM instance i JOIN state s ON s.id=i.current_state"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lcn_ack"));
    }

    [DatabaseFact]
    public async Task BackfillIsAtomicIdempotentAndUsesEventIdsCorrectly() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db);
        var history = new WorkflowBackfillObject { WorkflowName="regression", EnvCode=1, EntityRef=Guid.NewGuid().ToString() };
        history.Transitions.Add(new BackfillTransition { FromState="idle", ToState="active", EventCode=11, Timestamp=DateTime.UtcNow.AddDays(-2) });
        history.Transitions.Add(new BackfillTransition { FromState="active", ToState="done", EventCode=12, Timestamp=DateTime.UtcNow.AddDays(-1) });
        history.MarkValidated();
        await db.ExecuteAsync("CREATE TRIGGER fail_history BEFORE INSERT ON lifecycle FOR EACH ROW BEGIN IF NEW.from_state <> (SELECT id FROM state WHERE name='idle' LIMIT 1) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected partial history'; END IF; END");
        await Assert.ThrowsAnyAsync<Exception>(() => flow.Engine.ImportBackfillAsync(history));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM instance"));
        await db.ExecuteAsync("DROP TRIGGER fail_history");
        Assert.True((await flow.Engine.ImportBackfillAsync(history)).Success);
        Assert.True((await flow.Engine.ImportBackfillAsync(history)).Success);
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM lifecycle"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM ack"));
        history.Transitions[1].Actor = "different-content";
        Assert.Equal("BackfillContentConflict", (await flow.Engine.ImportBackfillAsync(history)).Reason);
        history.Transitions[0].FromState = "active";
        Assert.Equal("DisconnectedHistory", (await flow.Engine.ImportBackfillAsync(history)).Reason);
    }

    [DatabaseFact]
    public async Task WalkerFollowsGraphAndHistoricalOrderIncludingRepeatedLoops() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db);
        var snapshot = await flow.Engine.GetDefinitionSnapshotAsync(1, "regression");
        var data = new HistoricalDataProvider();
        var time = DateTime.UtcNow.AddDays(-1);
        data.History[11] = new(new[] { new BackfillStateData { Timestamp=time } });
        data.History[13] = new(new[] { new BackfillStateData { Timestamp=time.AddMinutes(1) }, new BackfillStateData { Timestamp=time.AddMinutes(2) } });
        data.History[12] = new(new[] { new BackfillStateData { Timestamp=time.AddMinutes(3) } });
        var result = await new DefinitionWalker(new FixedEngineAccessor(flow.Engine)).WalkAsync(snapshot!, Guid.NewGuid().ToString(), data);
        Assert.Equal(new[] { 11,13,13,12 }, result.Transitions.Select(t => t.EventCode));
        Assert.True(result.Validated);
    }

    [DatabaseFact]
    public async Task UpgradeIsRerunnableAndPreservesExplicit999WhileMovingUnorderedHooksLast() {
        await using var db = await TestDatabase.CreateAsync();
        var flow = await WorkflowScenario.CreateAsync(db, """
            {"defName":"regression","rules":[{"state":"active","via":11,"emit":[{"route":"explicit","order":999},{"route":"last"}]}]}
            """);
        await flow.StartAsync();
        await db.ExecuteAsync("UPDATE hook SET order_seq=999; ALTER TABLE hook MODIFY order_seq smallint NOT NULL DEFAULT 1;");
        var migration = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Sql","upgrade.sql")))
            .Replace("DELIMITER //", "").Replace("DELIMITER ;", "").Replace("END//", "END;");
        await db.ExecuteAsync(migration);
        await db.ExecuteAsync(migration);
        Assert.Equal(999, await db.ScalarAsync<int>("SELECT h.order_seq FROM hook h JOIN hook_route r ON r.id=h.route_id WHERE r.name='explicit'"));
        Assert.Equal(int.MaxValue, await db.ScalarAsync<int>("SELECT h.order_seq FROM hook h JOIN hook_route r ON r.id=h.route_id WHERE r.name='last'"));
    }
}
