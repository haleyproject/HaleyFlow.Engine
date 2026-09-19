using Haley.Abstractions;
using Haley.Models;

namespace WFE.Test.Fixtures;

internal sealed class HistoricalDataProvider : IBackfillDataProvider {
    public Dictionary<int, Queue<BackfillStateData>> History { get; } = new();
    public Task<BackfillStateData?> GetTransitionDataAsync(int eventCode, CancellationToken ct = default) =>
        Task.FromResult(History.TryGetValue(eventCode, out var records) && records.Count > 0 ? records.Dequeue() : null);
    public Task<BackfillHookData?> GetHookDataAsync(int eventCode, string route, CancellationToken ct = default) => Task.FromResult<BackfillHookData?>(null);
}
