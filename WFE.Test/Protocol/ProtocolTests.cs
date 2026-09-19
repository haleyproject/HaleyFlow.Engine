using Haley.Enums;
using Haley.Models;
using WFE.Test.Fixtures;
using Xunit;

namespace WFE.Test.Protocol;

public sealed class ProtocolTests {
    [Fact]
    public void SpecificRuleOverridesFallbackAndDoesNotInheritCompletion() {
        var snapshot = DefinitionJsonReader.ReadSnapshot(WorkflowScenario.Definition, """
            {"defName":"regression","rules":[
              {"state":"active","emit":[{"route":"fallback"}]},
              {"state":"active","via":11,"complete":{"success":12,"failure":14},"emit":[{"route":"specific"}]}]}
            """);
        var entry = snapshot.Transitions.Single(t => t.EventCode == 11);
        Assert.Equal("specific", Assert.Single(entry.Hooks).Route);
        Assert.Null(entry.Hooks[0].CompleteSuccessCode);
        Assert.Null(entry.Hooks[0].CompleteFailureCode);
    }

    [Fact]
    public void UnorderedHooksFollowLargeExplicitOrdersAndInheritType() {
        var snapshot = DefinitionJsonReader.ReadSnapshot(WorkflowScenario.Definition, """
            {"rules":[{"state":"active","type":"effect","emit":[{"route":"last"},{"route":"first","order":200000}]}]}
            """);
        var hooks = snapshot.Transitions.First(t => t.EventCode == 11).Hooks.OrderBy(h => h.OrderSeq).ToList();
        Assert.Equal("last", hooks[^1].Route);
        Assert.All(hooks, h => Assert.Equal(HookType.Effect, h.Type));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    [InlineData("1.5")]
    [InlineData("\"5\"")]
    public void InvalidOrdersAreRejected(string order) {
        var policy = "{\"rules\":[{\"state\":\"active\",\"emit\":[{\"route\":\"hook\",\"order\":" + order + "}]}]}";
        Assert.Throws<InvalidOperationException>(() => DefinitionJsonReader.ReadSnapshot(WorkflowScenario.Definition, policy));
    }

    [Fact]
    public void SharedValidatorRejectsAmbiguousGateCompletionOrder() {
        var snapshot = DefinitionJsonReader.ReadSnapshot(WorkflowScenario.Definition, """
            {"rules":[{"state":"active","emit":[
             {"route":"a","order":1,"complete":{"success":12}},
             {"route":"b","order":1,"complete":{"failure":14}}]}]}
            """);
        Assert.True(PolicyValidator.Validate(snapshot).HasCriticalErrors);
    }

    [Fact]
    public void UnusedPolicyStatesRemainWarningsRatherThanImportErrors() {
        var snapshot = DefinitionJsonReader.ReadSnapshot(WorkflowScenario.Definition,
            """{"defName":"regression","rules":[{"state":"future_state","emit":[{"route":"future_gate","order":1}]}]}""");
        Assert.False(PolicyValidator.Validate(snapshot).HasCriticalErrors);
    }
}
