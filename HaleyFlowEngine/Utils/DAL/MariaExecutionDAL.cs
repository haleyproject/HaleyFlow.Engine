using Haley.Abstractions;
using Haley.Models;

namespace Haley.Internal {
    internal sealed class MariaExecutionDAL : MariaDALBase, IExecutionDAL {
        public MariaExecutionDAL(IDALUtilBase db) : base(db) { }
        public Task<DbRow?> LockInstanceAsync(long instanceId, DbExecutionLoad load)
            => Db.RowAsync(QRY_EXECUTION.LOCK_INSTANCE, load, ("@instance_id", instanceId));
        public Task<long?> GetLifecycleByAckAsync(string ackGuid, DbExecutionLoad load)
            => Db.ScalarAsync<long?>(QRY_EXECUTION.LIFECYCLE_BY_ACK, load, ("@guid", ackGuid));
        public Task<int> GetContinuationReadinessAsync(string ackGuid, DbExecutionLoad load)
            => Db.ScalarAsync<int>(QRY_EXECUTION.CONTINUATION_READY, load, ("@guid", ackGuid));
        public Task<int> EnsureAsync(long lcId, DbExecutionLoad load)
            => Db.ExecAsync(QRY_EXECUTION.ENSURE, load, ("@lc_id", lcId));
        public Task<DbRow?> GetAsync(long lcId, DbExecutionLoad load)
            => Db.RowAsync(QRY_EXECUTION.GET, load, ("@lc_id", lcId));
        public Task<int> SetAsync(long lcId, int status, int? nextEvent, DbExecutionLoad load)
            => Db.ExecAsync(QRY_EXECUTION.SET, load, ("@lc_id", lcId), ("@status", status), ("@next_event", (object?)nextEvent ?? DBNull.Value));
        public Task<DbRows> ListPendingAsync(long afterId, int take, DbExecutionLoad load)
            => Db.RowsAsync(QRY_EXECUTION.PENDING, load, ("@after_id", afterId), ("@take", take));
        public Task<DbRows> ListValidationAcksAsync(long lcId, DbExecutionLoad load)
            => Db.RowsAsync(QRY_EXECUTION.VALIDATION_ACKS, load, ("@lc_id", lcId));
        public Task<DbRows> ListHooksAsync(long lcId, DbExecutionLoad load)
            => Db.RowsAsync(QRY_EXECUTION.HOOKS, load, ("@lc_id", lcId));
        public Task<string?> GetReceiptAsync(long instanceId, string requestId, DbExecutionLoad load)
            => Db.ScalarAsync<string?>(QRY_EXECUTION.GET_RECEIPT, load, ("@instance_id", instanceId), ("@request_id", requestId));
        public Task<int> SaveReceiptAsync(long instanceId, string requestId, string result, DbExecutionLoad load)
            => Db.ExecAsync(QRY_EXECUTION.SAVE_RECEIPT, load, ("@instance_id", instanceId), ("@request_id", requestId), ("@result", result));
        public Task<string?> GetBackfillHashAsync(long instanceId, DbExecutionLoad load)
            => Db.ScalarAsync<string?>(QRY_EXECUTION.GET_BACKFILL, load, ("@instance_id", instanceId));
        public Task<int> SaveBackfillHashAsync(long instanceId, string hash, DbExecutionLoad load)
            => Db.ExecAsync(QRY_EXECUTION.SAVE_BACKFILL, load, ("@instance_id", instanceId), ("@hash", hash));
    }
}
