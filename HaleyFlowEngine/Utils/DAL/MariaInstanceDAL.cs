using Haley.Abstractions;
using Haley.Models;
using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    internal sealed class MariaInstanceDAL : MariaDALBase, IInstanceDAL {
        public MariaInstanceDAL(IDALUtilBase db) : base(db) { }

        public Task<DbRow?> GetByGuidAsync(string guid, DbExecutionLoad load = default)
            => Db.RowAsync(QRY_INSTANCE.GET_BY_GUID, load, (GUID, guid));

        public Task<long?> GetIdByGuidAsync(string guid, DbExecutionLoad load = default)
            => Db.ScalarAsync<long?>(QRY_INSTANCE.GET_ID_BY_GUID, load, (GUID, guid));

        public Task<long?> GetIdByKeyAsync(long defVersionId, string entityId, DbExecutionLoad load = default)
            => Db.ScalarAsync<long?>(QRY_INSTANCE.GET_ID_BY_DEF_VERSION_AND_ENTITY_ID, load, (PARENT_ID, defVersionId), (ENTITY_ID, entityId));

        public Task<DbRow?> GetByDefIdAndEntityIdAsync(long defId, string entityId, DbExecutionLoad load = default)
            => Db.RowAsync(QRY_INSTANCE.GET_BY_DEF_ID_AND_ENTITY_ID, load, (DEF_ID, defId), (ENTITY_ID, entityId));

        public async Task<string?> UpsertByKeyReturnGuidAsync(long defVersionId, string entityId, long currentStateId, long? lastEventId, long policyId, uint flags, string? metadata, DbExecutionLoad load = default) {
            return await Db.ScalarAsync<string?>(QRY_INSTANCE.UPSERT_BY_DEF_ID_AND_ENTITY_ID_RETURN_GUID, load, (PARENT_ID, defVersionId), (ENTITY_ID, entityId), (STATE_ID, currentStateId), (EVENT_ID, lastEventId), (POLICY_ID, policyId), (FLAGS, flags), (METADATA, metadata));
        }

        public Task<int> UpdateCurrentStateCasAsync(long instanceId, long expectedFromStateId, long newToStateId, long? lastEventId, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.UPDATE_CURRENT_STATE_CAS, load, (ID, instanceId), (FROM_ID, expectedFromStateId), (TO_ID, newToStateId), (EVENT_ID, lastEventId));

        public Task<int> SetPolicyAsync(long instanceId, long policyId, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.SET_POLICY, load, (ID, instanceId), (POLICY_ID, policyId));

        public Task<int> AddFlagsAsync(long instanceId, uint flags, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.ADD_FLAGS, load, (ID, instanceId), (FLAGS, flags));

        public Task<int> RemoveFlagsAsync(long instanceId, uint flags, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.REMOVE_FLAGS, load, (ID, instanceId), (FLAGS, flags));

        public Task<int> ForceResetToStateAsync(long instanceId, long stateId, uint clearFlagsMask, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.FORCE_RESET_TO_STATE, load, (ID, instanceId), (STATE_ID, stateId), (FLAGS, clearFlagsMask));

        public Task<int> SetMessageAsync(long instanceId, string? message, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.SET_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId), (MESSAGE, message));

        public Task<int> ClearMessageAsync(long instanceId, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.CLEAR_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId));

        public Task<int> SetContextAsync(long instanceId, string? context, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.SET_CONTEXT, load, (ID, instanceId), (CONTEXT, context));

        public Task<int> SuspendWithMessageAsync(long instanceId, uint flags, string? message, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.SUSPEND_WITH_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId), (FLAGS, flags), (MESSAGE, message));

        public Task<int> FailWithMessageAsync(long instanceId, uint flags, string? message, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.FAIL_WITH_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId), (FLAGS, flags), (MESSAGE, message));

        public Task<int> CompleteWithMessageAsync(long instanceId, uint flags, string? message, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.COMPLETE_WITH_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId), (FLAGS, flags), (MESSAGE, message));

        public Task<int> ArchiveWithMessageAsync(long instanceId, uint flags, string? message, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.ARCHIVE_WITH_MESSAGE_BY_ID, load, (INSTANCE_ID, instanceId), (FLAGS, flags), (MESSAGE, message));

        public Task<int> UnsetFlagsAsync(long instanceId, uint flags, DbExecutionLoad load = default)
            => Db.ExecAsync(QRY_INSTANCE.UNSUSPEND_BY_ID, load, (INSTANCE_ID, instanceId), (FLAGS, flags));

        public Task<DbRows> ListStaleByDefaultStateDurationPagedAsync(int staleSeconds, int processedAckStatus, uint excludedInstanceFlagsMask, int skip, int take, DbExecutionLoad load = default)
            => Db.RowsAsync(QRY_INSTANCE.LIST_STALE_BY_DEFAULT_STATE_DURATION_PAGED, load, (STALE_SECONDS, staleSeconds), (ACK_STATUS, processedAckStatus), (FLAGS, excludedInstanceFlagsMask), (SKIP, skip), (TAKE, take));

        public Task<DbRows> ListByFlagsAndDefVersionPagedAsync(long defVersionId, uint flagsMask, int skip, int take, DbExecutionLoad load = default)
            => Db.RowsAsync(QRY_INSTANCE.LIST_BY_FLAGS_AND_DEF_VERSION_PAGED, load, (PARENT_ID, defVersionId), (FLAGS, flagsMask), (SKIP, skip), (TAKE, take));

        public Task<DbRows> ListByEnvAndDefPagedAsync(int envCode, string? defName, bool runningOnly, int skip, int take, DbExecutionLoad load = default)
            => Db.RowsAsync(QRY_INSTANCE.LIST_BY_ENV_AND_DEF_PAGED, load,
                (CODE, envCode),
                (DEF_NAME, defName ?? string.Empty),
                (RUNNING_ONLY, runningOnly ? 1 : 0),
                (SKIP, skip),
                (TAKE, take));

        public Task<DbRows> ListByEnvDefAndStatusPagedAsync(int envCode, string? defName, uint statusFlags, int skip, int take, DbExecutionLoad load = default)
            => Db.RowsAsync(QRY_INSTANCE.LIST_BY_ENV_AND_DEF_AND_STATUS_PAGED, load,
                (CODE, envCode),
                (DEF_NAME, defName ?? string.Empty),
                (STATUS_FLAGS, statusFlags),
                (SKIP, skip),
                (TAKE, take));
    }
}

