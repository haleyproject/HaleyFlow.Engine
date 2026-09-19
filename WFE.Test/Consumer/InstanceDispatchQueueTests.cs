using Haley.Internal;
using Xunit;

namespace WFE.Test.Consumer;

public sealed class InstanceDispatchQueueTests {
    [Fact]
    public async Task PhasesAreOrderedAndSiblingHooksCanOverlap() {
        var queue = new InstanceDispatchQueue();
        var releaseTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHooks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothHooks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooksStarted = 0;
        var completed = false;
        var first = queue.Enqueue("instance", "transition", false, () => releaseTransition.Task);
        Func<Task> hook = async () => { if (Interlocked.Increment(ref hooksStarted) == 2) bothHooks.SetResult(); await releaseHooks.Task; };
        var a = queue.Enqueue("instance", "hooks:1", true, hook);
        var b = queue.Enqueue("instance", "hooks:1", true, hook);
        var last = queue.Enqueue("instance", "complete", false, () => { completed = true; return Task.CompletedTask; });
        Assert.Equal(0, hooksStarted);
        releaseTransition.SetResult();
        await bothHooks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completed);
        releaseHooks.SetResult();
        await Task.WhenAll(first, a, b, last).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed);
    }

    [Fact]
    public async Task AnotherInstanceAndLaterWorkSurviveAFailedTask() {
        var queue = new InstanceDispatchQueue();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = queue.Enqueue("a", "phase", false, () => hold.Task);
        await queue.Enqueue("b", "phase", false, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        var failed = queue.Enqueue("a", "failure", false, () => throw new InvalidOperationException("test"));
        var later = queue.Enqueue("a", "later", false, () => Task.CompletedTask);
        hold.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await Task.WhenAll(blocked, later).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
