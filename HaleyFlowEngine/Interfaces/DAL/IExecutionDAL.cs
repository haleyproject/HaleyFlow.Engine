using Haley.Models;

namespace Haley.Abstractions {
    internal interface IExecutionDAL {
        Task<DbRow?> LockInstanceAsync(long instanceId, DbExecutionLoad load);
        Task<long?> GetLifecycleByAckAsync(string ackGuid, DbExecutionLoad load);
        Task<int> GetContinuationReadinessAsync(string ackGuid, DbExecutionLoad load);
        Task<int> EnsureAsync(long lcId, DbExecutionLoad load);
        Task<DbRow?> GetAsync(long lcId, DbExecutionLoad load);
        Task<int> SetAsync(long lcId, int status, int? nextEvent, DbExecutionLoad load);
        Task<DbRows> ListPendingAsync(long afterId, int take, DbExecutionLoad load);
        Task<DbRows> ListValidationAcksAsync(long lcId, DbExecutionLoad load);
        Task<DbRows> ListHooksAsync(long lcId, DbExecutionLoad load);
        Task<string?> GetReceiptAsync(long instanceId, string requestId, DbExecutionLoad load);
        Task<int> SaveReceiptAsync(long instanceId, string requestId, string result, DbExecutionLoad load);
        Task<string?> GetBackfillHashAsync(long instanceId, DbExecutionLoad load);
        Task<int> SaveBackfillHashAsync(long instanceId, string hash, DbExecutionLoad load);
    }
}
