using Haley.Enums;
using Haley.Models;
using Haley.Utils;

namespace Haley.Services {
    internal static class HookEventFactory {
        public static LifeCycleHookEvent Create(DbRow context, DbRow hook, HookContext policy,
            long consumerId, string ackGuid, DateTimeOffset occurredAt, int runCount) => new() {
                ConsumerId = consumerId, AckGuid = ackGuid,
                LifeCycleId = context.GetLong("lc_id"),
                InstanceGuid = context.GetString("instance_guid") ?? string.Empty,
                DefinitionId = context.GetLong("definition_id"),
                DefinitionVersionId = context.GetLong("def_version_id"),
                EntityId = context.GetString("entity_id") ?? string.Empty,
                Metadata = context.GetString("metadata"), OccurredAt = occurredAt,
                OnEntry = hook.GetBool("on_entry"), Route = hook.GetString("route") ?? string.Empty,
                HookType = (HookType)hook.GetInt("type"), GroupName = hook.GetString("group_name"),
                OrderSeq = hook.GetInt("order_seq"), AckMode = hook.GetInt("ack_mode"), RunCount = runCount,
                Params = policy.Params, NotBefore = policy.NotBefore, Deadline = policy.Deadline,
                // Hook completion routing is engine-owned on every delivery attempt.
                OnSuccessEvent = null, OnFailureEvent = null
            };
    }
}
