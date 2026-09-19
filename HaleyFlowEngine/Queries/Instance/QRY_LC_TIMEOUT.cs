using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    // lc_timeout table — tracks timeout processing per lifecycle entry.
    //
    // Case A (timeout_event IS set): one row per lc entry = idempotency marker.
    //   Written BEFORE TriggerAsync fires so a mid-process crash cannot double-fire.
    //   Resolution is natural: once the instance transitions, LIST_DUE_CASE_A_PAGED stops
    //   returning the old lc_id (latest-lc JOIN filter moves to the new lc_id).
    //
    // Case B (no timeout_event, advisory escalation): one row per lc entry = scheduling state.
    //   First occurrence: INSERT with trigger_count=1, next_due=now+DefaultStateStaleDuration.
    //   Subsequent ticks: UPDATE trigger_count++, last_trigger, next_due.
    //   Policy timeout duration = initial grace period (how long before first notice).
    //   DefaultStateStaleDuration = repeat cadence after first notice.
    //   If trigger_count >= max_retry: FailWithMessageAsync + STATE_TIMEOUT_FAILED notice.
    internal class QRY_LC_TIMEOUT {

        public const string EXISTS_BY_LC_ID = $@"SELECT 1 FROM lc_timeout WHERE lc_id = {LC_ID} LIMIT 1;";

        public const string DELETE_BY_LC_ID = $@"DELETE FROM lc_timeout WHERE lc_id = {LC_ID};";

        // Case A: idempotency marker only — no scheduling fields.
        // ON DUPLICATE KEY noop: safe to call multiple times for the same lc_id.
        public const string INSERT_CASE_A = $@"INSERT INTO lc_timeout (lc_id, trigger_count, max_retry) VALUES ({LC_ID}, 0, {MAX_RETRY}) ON DUPLICATE KEY UPDATE lc_id = lc_id;";

        // Case B — first occurrence: INSERT with count=1, last_trigger=now, next_due=now+@STALE_SECONDS.
        // ON DUPLICATE KEY noop: idempotent if called twice before the UPDATE path fires.
        public const string INSERT_CASE_B_FIRST = $@"INSERT INTO lc_timeout (lc_id, trigger_count, max_retry, last_trigger, next_due) VALUES ({LC_ID}, 1, {MAX_RETRY}, UTC_TIMESTAMP(), DATE_ADD(UTC_TIMESTAMP(), INTERVAL {STALE_SECONDS} SECOND)) ON DUPLICATE KEY UPDATE lc_id = lc_id;";

        // Case B — subsequent ticks: increment count, refresh last_trigger and next_due.
        public const string UPDATE_CASE_B_NEXT = $@"UPDATE lc_timeout SET trigger_count = trigger_count + 1, last_trigger = UTC_TIMESTAMP(), next_due = DATE_ADD(UTC_TIMESTAMP(), INTERVAL {STALE_SECONDS} SECOND) WHERE lc_id = {LC_ID};";

        // Case A due: event_code IS NOT NULL, no lc_timeout marker yet, initial grace elapsed.
        // Returns: instance_id, guid, entity_id, def_version_id, state_id, entry_lc_id,
        //          state_name, timeout_duration, timeout_mode, timeout_event_code, timeout_max_retry, due_at.
        public const string LIST_DUE_CASE_A_PAGED = $@"SELECT i.id AS instance_id, i.guid AS instance_guid, i.entity_id AS entity_id, i.def_version AS def_version_id, i.current_state AS state_id, l.id AS entry_lc_id, s.name AS state_name, tm.duration AS timeout_duration, tm.mode AS timeout_mode, tm.event_code AS timeout_event_code, tm.max_retry AS timeout_max_retry, DATE_ADD(l.created, INTERVAL tm.duration MINUTE) AS due_at FROM instance i JOIN lifecycle l ON l.id = (SELECT MAX(l2.id) FROM lifecycle l2 WHERE l2.instance_id = i.id) JOIN state s ON s.id = i.current_state AND s.def_version = i.def_version JOIN timeouts tm ON tm.policy_id = i.policy_id AND tm.state_name = s.name LEFT JOIN lc_timeout lt ON lt.lc_id = l.id WHERE (i.flags & {FLAGS}) = 0 AND l.to_state = i.current_state AND tm.event_code IS NOT NULL AND DATE_ADD(l.created, INTERVAL tm.duration MINUTE) <= UTC_TIMESTAMP() ORDER BY due_at ASC, i.id ASC LIMIT {TAKE} OFFSET {SKIP};";

        // Case B due: no event_code, either initial grace elapsed (lt.lc_id IS NULL) or next_due passed.
        // Returns: instance_id, guid, entity_id, def_version_id, state_id, entry_lc_id,
        //          state_name, timeout_max_retry, trigger_count (current or 0 for first).
        public const string LIST_DUE_CASE_B_PAGED = $@"SELECT i.id AS instance_id, i.guid AS instance_guid, i.entity_id AS entity_id, i.def_version AS def_version_id, i.current_state AS state_id, l.id AS entry_lc_id, s.name AS state_name, COALESCE(lt.max_retry, tm.max_retry) AS timeout_max_retry, COALESCE(lt.trigger_count, 0) AS trigger_count FROM instance i JOIN lifecycle l ON l.id = (SELECT MAX(l2.id) FROM lifecycle l2 WHERE l2.instance_id = i.id) JOIN state s ON s.id = i.current_state AND s.def_version = i.def_version JOIN timeouts tm ON tm.policy_id = i.policy_id AND tm.state_name = s.name LEFT JOIN lc_timeout lt ON lt.lc_id = l.id WHERE (i.flags & {FLAGS}) = 0 AND l.to_state = i.current_state AND tm.event_code IS NULL AND (lt.lc_id IS NULL AND DATE_ADD(l.created, INTERVAL tm.duration MINUTE) <= UTC_TIMESTAMP() OR lt.next_due IS NOT NULL AND lt.next_due <= UTC_TIMESTAMP()) ORDER BY i.id ASC LIMIT {TAKE} OFFSET {SKIP};";

    }
}
