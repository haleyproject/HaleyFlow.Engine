using Haley.Models;
using Haley.Utils;

namespace Haley.Services {
    internal static class HookAckBarrier {
        public static bool HasSucceeded(DbRow hook) => hook.GetBool("dispatched") && hook.GetInt("total") > 0 &&
            (hook.GetInt("ack_mode") == 1 ? hook.GetInt("processed") > 0 : hook.GetInt("processed") == hook.GetInt("total"));
        public static bool HasFailed(DbRow hook) => hook.GetBool("dispatched") && hook.GetInt("total") > 0 &&
            (hook.GetInt("ack_mode") == 1 ? hook.GetInt("processed") == 0 && hook.GetInt("pending") == 0 : hook.GetInt("failed") > 0);
        public static bool IsTerminal(DbRow hook) => HasSucceeded(hook) ||
            (hook.GetBool("dispatched") && hook.GetInt("total") > 0 && hook.GetInt("pending") == 0);
    }
}
