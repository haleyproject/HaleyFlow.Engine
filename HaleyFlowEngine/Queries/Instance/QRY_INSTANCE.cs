using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    internal class QRY_INSTANCE {
        public const string EXISTS_BY_ID = $@"SELECT 1 FROM instance WHERE id = {ID} LIMIT 1;";
        public const string EXISTS_BY_GUID = $@"SELECT 1 FROM instance WHERE guid = lower(trim({GUID})) LIMIT 1;";
        public const string EXISTS_BY_DEF_VERSION_AND_ENTITY_ID = $@"SELECT 1 FROM instance WHERE def_version = {PARENT_ID} AND entity_id = trim({ENTITY_ID}) LIMIT 1;";

        // GUID-first lookups
        public const string GET_BY_GUID = $@"SELECT * FROM instance WHERE guid = lower(trim({GUID})) LIMIT 1;";
        public const string GET_BY_DEF_VERSION_AND_ENTITY_ID = $@"SELECT * FROM instance WHERE def_version = {PARENT_ID} AND entity_id = lower(trim({ENTITY_ID})) LIMIT 1;";
        public const string GET_BY_DEF_ID_AND_ENTITY_ID = $@"SELECT * FROM instance WHERE def_id = {DEF_ID} AND entity_id = lower(trim({ENTITY_ID})) LIMIT 1;";

        public const string GET_ID_BY_GUID = $@"SELECT id FROM instance WHERE guid = lower(trim({GUID})) LIMIT 1;";

        public const string GET_BY_ID = $@"SELECT * FROM instance WHERE id = {ID} LIMIT 1;";
        public const string GET_GUID_BY_ID = $@"SELECT guid FROM instance WHERE id = {ID} LIMIT 1;";
        public const string GET_ID_BY_DEF_VERSION_AND_ENTITY_ID = $@"SELECT id FROM instance WHERE def_version = {PARENT_ID} AND entity_id = lower(trim({ENTITY_ID})) LIMIT 1;";
        public const string GET_GUID_BY_DEF_VERSION_AND_ENTITY_ID = $@"SELECT guid FROM instance WHERE def_version = {PARENT_ID} AND entity_id = lower(trim({ENTITY_ID})) LIMIT 1 FOR UPDATE;";

        // lists should be chronological newest-first
        public const string LIST_BY_DEF_VERSION = $@"SELECT * FROM instance WHERE def_version = {PARENT_ID} ORDER BY created DESC, id DESC;";
        public const string LIST_BY_ENTITY_ID = $@"SELECT * FROM instance WHERE entity_id = lower(trim({ENTITY_ID})) ORDER BY created DESC, id DESC;";
        public const string LIST_BY_CURRENT_STATE = $@"SELECT * FROM instance WHERE current_state = {STATE_ID} ORDER BY created DESC, id DESC;";

        public const string LIST_WHERE_FLAGS_ANY = $@"SELECT * FROM instance WHERE (flags & {FLAGS}) <> 0 ORDER BY created DESC, id DESC;";
        public const string LIST_WHERE_FLAGS_NONE = $@"SELECT * FROM instance WHERE (flags & {FLAGS}) = 0 ORDER BY created DESC, id DESC;";

        // INSERT returns guid — def_id derived from def_version to avoid caller mismatch
        public const string INSERT = $@"INSERT INTO instance (def_version, def_id, entity_id, current_state, last_event, policy_id, flags, metadata) VALUES ({PARENT_ID}, (SELECT parent FROM def_version WHERE id = {PARENT_ID} LIMIT 1), lower(trim({ENTITY_ID})), {STATE_ID}, {EVENT_ID}, {POLICY_ID}, {FLAGS}, {METADATA}); SELECT guid FROM instance WHERE id = LAST_INSERT_ID() LIMIT 1;";

        // UPSERT conflict key is UNIQUE(def_id, entity_id) — def_id derived from def_version
        public const string UPSERT_BY_DEF_ID_AND_ENTITY_ID_RETURN_GUID = $@"INSERT INTO instance (def_version, def_id, entity_id, current_state, last_event, policy_id, flags, metadata) VALUES ({PARENT_ID}, (SELECT parent FROM def_version WHERE id = {PARENT_ID} LIMIT 1), lower(trim({ENTITY_ID})), {STATE_ID}, {EVENT_ID}, {POLICY_ID}, {FLAGS}, {METADATA}) ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id); SELECT guid FROM instance WHERE id = LAST_INSERT_ID() LIMIT 1;";

        public const string UPDATE_CURRENT_STATE = $@"UPDATE instance SET current_state = {STATE_ID}, last_event = {EVENT_ID} WHERE id = {ID};";
        public const string UPDATE_CURRENT_STATE_CAS = $@"UPDATE instance SET current_state = {TO_ID}, last_event = {EVENT_ID}, revision = revision + 1 WHERE id = {ID} AND current_state = {FROM_ID};";

        public const string SET_FLAGS = $@"UPDATE instance SET flags = {FLAGS} WHERE id = {ID};";
        public const string ADD_FLAGS = $@"UPDATE instance SET flags = (flags | {FLAGS}) WHERE id = {ID};";
        public const string REMOVE_FLAGS = $@"UPDATE instance SET flags = (flags & ~{FLAGS}) WHERE id = {ID};";
        // Admin reopen: atomically reset current_state, clear terminal+suspended flags, set Active (1),
        // and clear last_event + message. No CAS — engine verifies terminal state in C# before calling.
        public const string FORCE_RESET_TO_STATE = $@"UPDATE instance SET current_state = {STATE_ID}, flags = ((flags & ~{FLAGS}) | 1), last_event = NULL, message = NULL WHERE id = {ID};";

        public const string SET_POLICY = $@"UPDATE instance SET policy_id = {POLICY_ID} WHERE id = {ID};";
        public const string SET_ENTITY_ID = $@"UPDATE instance SET entity_id = lower(trim({ENTITY_ID})) WHERE id = {ID};";
        public const string SET_CONTEXT = $@"UPDATE instance SET context = {CONTEXT} WHERE id = {ID};";

        public const string DELETE = $@"DELETE FROM instance WHERE id = {ID};";

        public const string SET_MESSAGE_BY_ID = @$"UPDATE instance SET message={MESSAGE} WHERE id={INSTANCE_ID};";
        public const string SUSPEND_WITH_MESSAGE_BY_ID = @$"UPDATE instance SET flags=(flags | {FLAGS}), message={MESSAGE} WHERE id={INSTANCE_ID};";
        // Fail path is atomic: set Failed bit and clear Active bit in one update to avoid Failed+Active mixes.
        public const string FAIL_WITH_MESSAGE_BY_ID = @$"UPDATE instance SET flags=((flags & ~1) | {FLAGS}), message={MESSAGE} WHERE id={INSTANCE_ID};";
        public const string CLEAR_MESSAGE_BY_ID = @$"UPDATE instance SET message=NULL WHERE id={INSTANCE_ID};";
        public const string UNSUSPEND_BY_ID = @$"UPDATE instance SET flags=(flags & ~{FLAGS}) WHERE id={INSTANCE_ID};";
        public const string CLEAR_FAILED_BY_ID = @$"UPDATE instance SET flags=(flags & ~{FLAGS}) WHERE id={INSTANCE_ID};";
        public const string COMPLETE_WITH_MESSAGE_BY_ID = @$"UPDATE instance SET flags=(flags | {FLAGS}), message={MESSAGE} WHERE id={INSTANCE_ID};";
        public const string CLEAR_COMPLETED_BY_ID = @$"UPDATE instance SET flags=(flags & ~{FLAGS}) WHERE id={INSTANCE_ID};";
        public const string ARCHIVE_WITH_MESSAGE_BY_ID = @$"UPDATE instance SET flags=(flags | {FLAGS}), message={MESSAGE} WHERE id={INSTANCE_ID};";
        public const string CLEAR_ARCHIVED_BY_ID = @$"UPDATE instance SET flags=(flags & ~{FLAGS}) WHERE id={INSTANCE_ID};";

        // Flat instance row for TimelineBuilder — joins definition, def_version, current state and last event.
        public const string GET_FOR_TIMELINE =
            $@"SELECT i.id, i.guid, i.entity_id, i.def_id, i.def_version, i.flags, i.message,
                      d.name AS def_name, dv.version AS def_version_num,
                      cs.display_name AS current_state, le.display_name AS last_event,
                      i.created, i.modified
               FROM instance i
               JOIN definition d ON d.id = i.def_id
               JOIN def_version dv ON dv.id = i.def_version
               JOIN state cs ON cs.id = i.current_state
               LEFT JOIN events le ON le.id = i.last_event
               WHERE i.id = {INSTANCE_ID}
               LIMIT 1;";

        public const string GET_TIMELINE_JSON_BY_INSTANCE_ID = $@"WITH inst AS ( SELECT * FROM instance WHERE id = {INSTANCE_ID} LIMIT 1 ), timeline AS ( SELECT JSON_ARRAYAGG( JSON_OBJECT( 'lifecycle_id', l.id, 'created', COALESCE(l.occurred, l.created), 'occurred', l.occurred, 'from_state', sf.display_name, 'to_state', st.display_name, 'event', ev.display_name, 'event_code', ev.code, 'is_initial', IF((sf.flags & 1) <> 0, JSON_EXTRACT('true','$'), JSON_EXTRACT('false','$')), 'is_terminal', IF((st.flags & 2) <> 0, JSON_EXTRACT('true','$'), JSON_EXTRACT('false','$')), 'actor', ld.actor, 'activities', COALESCE(( SELECT JSON_ARRAYAGG( JSON_OBJECT( 'runtime_id', r.id, 'lc_id', r.lc_id, 'state', rs.display_name, 'actor_id', r.actor_id, 'activity', act.display_name, 'label', COALESCE(hr.label, ''), 'status', ast.display_name, 'created', r.created, 'modified', r.modified, 'frozen', IF(r.frozen = 1, JSON_EXTRACT('true','$'), JSON_EXTRACT('false','$')) ) ) FROM runtime r JOIN state rs ON rs.id = r.state_id JOIN activity act ON act.id = r.activity JOIN activity_status ast ON ast.id = r.status LEFT JOIN hook_route hr ON hr.name = act.display_name WHERE r.instance_id = i.id AND r.lc_id = l.id ), JSON_ARRAY()) ) ) AS j FROM inst i JOIN lifecycle l ON l.instance_id = i.id JOIN state sf ON sf.id = l.from_state JOIN state st ON st.id = l.to_state JOIN events ev ON ev.id = l.event LEFT JOIN lc_data ld ON ld.lc_id = l.id ), other_activities AS ( SELECT JSON_ARRAYAGG( JSON_OBJECT( 'runtime_id', r.id, 'lc_id', r.lc_id, 'state', rs.display_name, 'actor_id', r.actor_id, 'activity', act.display_name, 'label', COALESCE(hr.label, ''), 'status', ast.display_name, 'created', r.created, 'modified', r.modified, 'frozen', IF(r.frozen = 1, JSON_EXTRACT('true','$'), JSON_EXTRACT('false','$')) ) ) AS j FROM inst i JOIN runtime r ON r.instance_id = i.id JOIN state rs ON rs.id = r.state_id JOIN activity act ON act.id = r.activity JOIN activity_status ast ON ast.id = r.status LEFT JOIN hook_route hr ON hr.name = act.display_name WHERE (r.lc_id = 0 OR NOT EXISTS ( SELECT 1 FROM lifecycle l2 WHERE l2.id = r.lc_id AND l2.instance_id = r.instance_id )) ) SELECT JSON_OBJECT( 'instance', JSON_OBJECT( 'id', i.id, 'guid', i.guid, 'entity_id', i.entity_id, 'def_id', i.def_id, 'def_name', d.name, 'def_version_id', i.def_version, 'def_version', dv.version, 'current_state_id', i.current_state, 'current_state', cs.display_name, 'last_event_id', i.last_event, 'last_event', le.display_name, 'created', i.created, 'modified', i.modified, 'instance_status', CASE WHEN (i.flags & 16) <> 0 THEN 'Archived' WHEN (i.flags & 8) <> 0 THEN 'Failed' WHEN (i.flags & 4) <> 0 THEN 'Completed' WHEN (i.flags & 2) <> 0 THEN 'Suspended' WHEN (i.flags & 1) <> 0 THEN 'Active' ELSE 'None' END, 'instance_message', i.message ), 'timeline', COALESCE(t.j, JSON_ARRAY()), 'Other Activities', COALESCE(o.j, JSON_ARRAY()) ) AS json FROM inst i JOIN definition d ON d.id = i.def_id JOIN def_version dv ON dv.id = i.def_version JOIN state cs ON cs.id = i.current_state LEFT JOIN events le ON le.id = i.last_event LEFT JOIN timeline t ON 1=1 LEFT JOIN other_activities o ON 1=1;";

        public const string LIST_BY_FLAGS_AND_DEF_VERSION_PAGED = $@"SELECT i.entity_id, i.guid AS instance_guid, i.created FROM instance i WHERE i.def_version = {PARENT_ID} AND (i.flags & {FLAGS}) <> 0 ORDER BY i.created ASC, i.id ASC LIMIT {TAKE} OFFSET {SKIP};";

        public const string LIST_STALE_BY_DEFAULT_STATE_DURATION_PAGED = $@"SELECT i.id AS instance_id, i.guid AS instance_guid, i.entity_id AS entity_id, i.def_version AS def_version_id, i.current_state AS current_state_id, s.display_name AS state_name, l.id AS lc_id, l.created AS entered_at, TIMESTAMPDIFF(SECOND, l.created, UTC_TIMESTAMP()) AS stale_seconds FROM instance i JOIN state s ON s.id = i.current_state AND s.def_version = i.def_version JOIN lifecycle l ON l.id = (SELECT l2.id FROM lifecycle l2 WHERE l2.instance_id = i.id AND l2.to_state = i.current_state ORDER BY l2.id DESC LIMIT 1) WHERE (i.flags & {FLAGS}) = 0 AND NOT EXISTS (SELECT 1 FROM timeouts tm WHERE tm.policy_id = i.policy_id AND tm.state_name = s.name AND tm.event_code IS NOT NULL) AND l.created <= DATE_SUB(UTC_TIMESTAMP(), INTERVAL {STALE_SECONDS} SECOND) AND NOT EXISTS (SELECT 1 FROM lc_ack la JOIN ack_consumer ac ON ac.ack_id = la.ack_id WHERE la.lc_id = l.id AND ac.status <> {ACK_STATUS}) AND NOT EXISTS (SELECT 1 FROM hook h JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = l.id JOIN hook_ack ha ON ha.hook_id = hl.id JOIN ack_consumer ac2 ON ac2.ack_id = ha.ack_id WHERE h.instance_id = i.id AND h.state_id = i.current_state AND ac2.status <> {ACK_STATUS}) ORDER BY l.created ASC, i.id ASC LIMIT {TAKE} OFFSET {SKIP};";

        public const string LIST_BY_ENV_AND_DEF_PAGED =
            $@"SELECT i.id, i.guid, i.entity_id, d.name AS def_name, i.def_version,
              i.current_state, s.display_name AS current_state_name, s.flags AS state_flags,
              i.flags AS instance_flags, i.created, i.modified
       FROM instance i
       JOIN definition d ON d.id = i.def_id
       JOIN environment e ON e.id = d.env
       JOIN state s ON s.id = i.current_state
       WHERE e.code = {CODE}
         AND ({DEF_NAME} = '' OR d.name = {DEF_NAME})
         AND ({RUNNING_ONLY} = 0 OR (s.flags & 2) = 0)
       ORDER BY i.id DESC
       LIMIT {TAKE} OFFSET {SKIP};";

        public const string LIST_BY_ENV_AND_DEF_AND_STATUS_PAGED =
            $@"SELECT i.id, i.guid, i.entity_id, d.name AS def_name, i.def_version,
              i.current_state, s.display_name AS current_state_name, s.flags AS state_flags,
              i.flags AS instance_flags, i.message, i.created, i.modified
       FROM instance i
       JOIN definition d ON d.id = i.def_id
       JOIN environment e ON e.id = d.env
       JOIN state s ON s.id = i.current_state
       WHERE e.code = {CODE}
         AND ({DEF_NAME} = '' OR d.name = {DEF_NAME})
         AND ({STATUS_FLAGS} = 0 OR (i.flags & {STATUS_FLAGS}) <> 0)
       ORDER BY i.id DESC
       LIMIT {TAKE} OFFSET {SKIP};";
    }
}


