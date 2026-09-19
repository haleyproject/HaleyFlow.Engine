using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    internal class QRY_LIFECYCLE {
        public const string EXISTS_BY_ID = $@"SELECT 1 FROM lifecycle WHERE id = {ID} LIMIT 1;";

        public const string GET_BY_ID = $@"SELECT * FROM lifecycle WHERE id = {ID} LIMIT 1;";
        public const string GET_CONTEXT_BY_LC_ID =
            $@"SELECT l.id AS lc_id, l.instance_id, l.from_state, l.to_state AS state_id, l.event AS via_event,
                      i.guid AS instance_guid, i.def_version AS def_version_id, i.metadata AS metadata,
                      i.entity_id AS entity_id, i.policy_id,
                      dv.parent AS definition_id
               FROM lifecycle l
               JOIN instance i ON i.id = l.instance_id
               JOIN def_version dv ON dv.id = i.def_version
               WHERE l.id = {LC_ID}
               LIMIT 1;";
        public const string GET_LAST_BY_INSTANCE = $@"SELECT * FROM lifecycle WHERE instance_id = {INSTANCE_ID} ORDER BY id DESC LIMIT 1 FOR UPDATE;";

        public const string LIST_BY_INSTANCE = $@"SELECT * FROM lifecycle WHERE instance_id = {INSTANCE_ID} ORDER BY created DESC, id DESC;";
        public const string LIST_BY_INSTANCE_PAGED = $@"SELECT * FROM lifecycle WHERE instance_id = {INSTANCE_ID} ORDER BY created DESC, id DESC LIMIT {TAKE} OFFSET {SKIP};";

        // Timeline fetch — lifecycle rows with joined state/event names. Ordered oldest-first for display.
        public const string LIST_FOR_TIMELINE =
            $@"SELECT l.id AS lifecycle_id, COALESCE(l.occurred, l.created) AS created, l.occurred,
                      sf.display_name AS from_state, IF((sf.flags & 1) <> 0, 1, 0) AS is_initial,
                      st.display_name AS to_state,  IF((st.flags & 2) <> 0, 1, 0) AS is_terminal,
                      ev.display_name AS event, ev.code AS event_code,
                      ld.actor,
                      CASE WHEN EXISTS (SELECT 1 FROM hook_lc hl WHERE hl.lc_id = l.id) THEN 'ValidationMode' ELSE 'NormalRun' END AS dispatch_mode,
                      ln.`next` AS next_event,
                      IF(ln.id IS NULL, 0, 1) AS has_complete,
                      IF(ln.dispatched = 1, 1, 0) AS complete_dispatched,
                      a.guid AS complete_ack_guid,
                      COUNT(ac.ack_id) AS complete_total_acks,
                      COALESCE(SUM(IF(ac.status = 3, 1, 0)), 0) AS complete_processed_acks,
                      COALESCE(SUM(IF(ac.status = 4, 1, 0)), 0) AS complete_failed_acks,
                      MAX(ac.last_trigger) AS complete_last_trigger
               FROM lifecycle l
               JOIN state sf ON sf.id = l.from_state
               JOIN state st ON st.id = l.to_state
               JOIN events ev ON ev.id = l.event
               LEFT JOIN lc_data ld ON ld.lc_id = l.id
               LEFT JOIN lc_next ln ON ln.id = l.id
               LEFT JOIN lcn_ack lna ON lna.lc_id = l.id
               LEFT JOIN ack a ON a.id = lna.ack_id
               LEFT JOIN ack_consumer ac ON ac.ack_id = a.id
               WHERE l.instance_id = {INSTANCE_ID}
               GROUP BY l.id, l.occurred, l.created, sf.display_name, sf.flags, st.display_name, st.flags, ev.display_name, ev.code, ld.actor, ln.id, ln.`next`, ln.dispatched, a.guid
               ORDER BY l.id ASC;";

        // occurred is NULL for normal flow; set only for replay/late-join scenarios
        public const string INSERT = $@"INSERT INTO lifecycle (from_state, to_state, event, instance_id, occurred) VALUES ({FROM_ID}, {TO_ID}, {EVENT_ID}, {INSTANCE_ID}, {OCCURRED}); SELECT LAST_INSERT_ID() AS id;";

        public const string DELETE = $@"DELETE FROM lifecycle WHERE id = {ID};";
        public const string DELETE_BY_INSTANCE = $@"DELETE FROM lifecycle WHERE instance_id = {INSTANCE_ID};";
    }
}
