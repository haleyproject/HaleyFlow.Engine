using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    internal class QRY_ACK_DISPATCH {

        public const string COUNT_DUE_LC = $@"SELECT COUNT(1) AS pending_count FROM lc_ack la JOIN ack_consumer ac ON ac.ack_id = la.ack_id JOIN lifecycle l ON l.id = la.lc_id JOIN instance i ON i.id = l.instance_id WHERE ac.status = {ACK_STATUS} AND ac.next_due IS NOT NULL AND ac.next_due <= UTC_TIMESTAMP() AND (i.flags & 5) <> 0 AND (i.flags & 26) = 0;";

        public const string COUNT_DUE_COMPLETE = $@"SELECT COUNT(1) AS pending_count FROM lcn_ack lna JOIN ack_consumer ac ON ac.ack_id = lna.ack_id JOIN lifecycle l ON l.id = lna.lc_id JOIN lc_next ln ON ln.id = lna.lc_id JOIN instance i ON i.id = l.instance_id WHERE ac.status = {ACK_STATUS} AND ac.next_due IS NOT NULL AND ac.next_due <= UTC_TIMESTAMP() AND (i.flags & 5) <> 0 AND (i.flags & 26) = 0;";

        public const string COUNT_DUE_HOOK = $@"SELECT COUNT(1) AS pending_count FROM hook_ack ha JOIN ack_consumer ac ON ac.ack_id = ha.ack_id JOIN hook_lc hl ON hl.id = ha.hook_id JOIN hook h ON h.id = hl.hook_id JOIN instance i ON i.id = h.instance_id WHERE ac.status = {ACK_STATUS} AND ac.next_due IS NOT NULL AND ac.next_due <= UTC_TIMESTAMP() AND (i.flags & 5) <> 0 AND (i.flags & 26) = 0;";

        public const string LIST_DUE_LC_PAGED = $@"SELECT a.id AS ack_id, a.guid AS ack_guid, a.created AS ack_created, ac.consumer AS consumer, ac.status AS status, ac.last_trigger AS last_trigger, ac.trigger_count AS trigger_count, ac.max_trigger AS max_trigger, ac.next_due AS next_due, ac.created AS consumer_created, ac.modified AS consumer_modified, l.id AS lc_id, l.instance_id, i.def_id AS def_id, i.def_version AS def_version_id, i.entity_id, i.metadata AS metadata, l.from_state, l.to_state, l.event AS event_id, ev.code AS event_code, ev.display_name AS event_name, CASE WHEN EXISTS (SELECT 1 FROM hook_lc hl WHERE hl.lc_id = l.id) THEN 1 ELSE 0 END AS dispatch_mode, NULLIF(i.policy_id,0) AS policy_id, p.hash AS policy_hash, p.content AS policy_json, l.created AS lc_created, i.guid AS instance_guid FROM lc_ack la JOIN ack a ON a.id = la.ack_id JOIN ack_consumer ac ON ac.ack_id = a.id JOIN consumer c ON c.id = ac.consumer JOIN lifecycle l ON l.id = la.lc_id JOIN instance i ON i.id = l.instance_id JOIN events ev ON ev.id = l.event LEFT JOIN policy p ON p.id = NULLIF(i.policy_id,0) WHERE ac.consumer = {CONSUMER_ID} AND ac.status = {ACK_STATUS} AND ac.next_due IS NOT NULL AND ac.next_due <= UTC_TIMESTAMP() AND TIMESTAMPDIFF(SECOND, c.last_beat, UTC_TIMESTAMP()) <= {TTL_SECONDS} AND (i.flags & 5) <> 0 AND (i.flags & 26) = 0 ORDER BY ac.next_due ASC, l.id ASC, ac.ack_id ASC, ac.consumer ASC LIMIT {TAKE} OFFSET {SKIP};";

        public const string LIST_DUE_COMPLETE_PAGED =
            $@"SELECT a.id AS ack_id, a.guid AS ack_guid, a.created AS ack_created,
                      ac.consumer AS consumer, ac.status AS status,
                      ac.last_trigger AS last_trigger, ac.trigger_count AS trigger_count,
                      ac.max_trigger AS max_trigger,
                      ac.next_due AS next_due, ac.created AS consumer_created, ac.modified AS consumer_modified,
                      l.id AS lc_id, l.instance_id,
                      i.def_id AS def_id, i.def_version AS def_version_id,
                      i.entity_id, i.metadata AS metadata,
                      ln.created AS lc_created,
                      i.guid AS instance_guid,
                      1 AS hooks_succeeded,
                      ln.`next` AS next_event
               FROM lcn_ack lna
               JOIN ack a ON a.id = lna.ack_id
               JOIN ack_consumer ac ON ac.ack_id = a.id
               JOIN consumer c ON c.id = ac.consumer
               JOIN lifecycle l ON l.id = lna.lc_id
               JOIN lc_next ln ON ln.id = lna.lc_id
               JOIN instance i ON i.id = l.instance_id
               WHERE ac.consumer = {CONSUMER_ID}
                 AND ac.status = {ACK_STATUS}
                 AND ac.next_due IS NOT NULL
                 AND ac.next_due <= UTC_TIMESTAMP()
                 AND TIMESTAMPDIFF(SECOND, c.last_beat, UTC_TIMESTAMP()) <= {TTL_SECONDS}
                 AND (i.flags & 5) <> 0
                 AND (i.flags & 26) = 0
               ORDER BY ac.next_due ASC, l.id ASC, ac.ack_id ASC, ac.consumer ASC
               LIMIT {TAKE} OFFSET {SKIP};";

        // hook_ack.hook_id now references hook_lc.id — join via hook_lc to reach hook definition.
        // run_count = how many times this hook definition has been dispatched across all lifecycle entries.
        public const string LIST_DUE_HOOK_PAGED =
            $@"SELECT a.id AS ack_id, a.guid AS ack_guid, a.created AS ack_created,
                      ac.consumer AS consumer, ac.status AS status,
                      ac.last_trigger AS last_trigger, ac.trigger_count AS trigger_count,
                      ac.max_trigger AS max_trigger,
                      ac.next_due AS next_due, ac.created AS consumer_created, ac.modified AS consumer_modified,
                      ha.hook_id AS hook_lc_id,
                      hl.lc_id AS lc_id,
                      h.id AS hook_id, h.instance_id AS instance_id,
                      i.def_id AS def_id, i.def_version AS def_version_id,
                      i.entity_id AS entity_id, i.metadata AS metadata,
                      h.state_id AS state_id, h.via_event AS via_event, h.on_entry AS on_entry,
                      hr.name AS route, h.type, h.order_seq, h.ack_mode, hg.name AS group_name,
                      i.def_id AS definition_id,
                      hl.created AS hook_created,
                      i.guid AS instance_guid,
                      NULLIF(i.policy_id,0) AS policy_id, p.hash AS policy_hash, p.content AS policy_json,
                      (SELECT COUNT(*) FROM hook_lc hl2 WHERE hl2.hook_id = h.id AND hl2.dispatched = 1 AND hl2.lc_id <= hl.lc_id) AS run_count
               FROM hook_ack ha
               JOIN ack a ON a.id = ha.ack_id
               JOIN ack_consumer ac ON ac.ack_id = a.id
               JOIN consumer c ON c.id = ac.consumer
               JOIN hook_lc hl ON hl.id = ha.hook_id
               JOIN hook h ON h.id = hl.hook_id
               JOIN hook_route hr ON hr.id = h.route_id
               LEFT JOIN hook_group hg ON hg.id = h.group_id
               JOIN instance i ON i.id = h.instance_id
               LEFT JOIN policy p ON p.id = NULLIF(i.policy_id,0)
               WHERE ac.consumer = {CONSUMER_ID}
                 AND ac.status = {ACK_STATUS}
                 AND ac.next_due IS NOT NULL
                 AND ac.next_due <= UTC_TIMESTAMP()
                 AND TIMESTAMPDIFF(SECOND, c.last_beat, UTC_TIMESTAMP()) <= {TTL_SECONDS} AND (i.flags & 5) <> 0 AND (i.flags & 26) = 0
               ORDER BY ac.next_due ASC, h.id ASC, ac.ack_id ASC, ac.consumer ASC
               LIMIT {TAKE} OFFSET {SKIP};";
    }
}

