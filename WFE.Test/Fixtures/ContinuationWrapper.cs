using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Services;

namespace WFE.Test.Fixtures;

internal sealed class ContinuationWrapper(AckOutcome outcome, int next, Action executed) : LifeCycleWrapper {
    protected override Task<AckOutcome> OnUnhandledTransitionAsync(ILifeCycleTransitionEvent evt, ConsumerContext ctx) {
        executed();
        UseConsumerOverrideNextEvent(next);
        return Task.FromResult(outcome);
    }
    protected override Task<AckOutcome> OnUnhandledHookAsync(ILifeCycleHookEvent evt, ConsumerContext ctx) => Task.FromResult(AckOutcome.Processed);
    protected override int? ResolveTransitionCompleteFallbackEvent(ILifeCycleCompleteEvent evt, ConsumerContext ctx) => null;
}
