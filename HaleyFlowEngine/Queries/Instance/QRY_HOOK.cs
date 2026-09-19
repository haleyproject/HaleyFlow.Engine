using static Haley.Internal.QueryFields;

namespace Haley.Internal {
    internal class QRY_HOOK {
        public const string EXISTS_BY_ID = $@"SELECT 1 FROM hook WHERE id = {ID} LIMIT 1;";
        public const string EXISTS_BY_KEY = $@"SELECT 1 FROM hook WHERE instance_id = {INSTANCE_ID} AND state_id = {STATE_ID} AND via_event = {EVENT_ID} AND on_entry = {ON_ENTRY} AND route_id = {ROUTE_ID} LIMIT 1;";

        public const string GET_BY_ID = $@"SELECT * FROM hook WHERE id = {ID} LIMIT 1;";
        public const string GET_BY_KEY =
        $@"SELECT * FROM hook WHERE instance_id = {INSTANCE_ID} AND state_id = {STATE_ID} AND via_event = {EVENT_ID} AND on_entry = {ON_ENTRY} AND route_id = {ROUTE_ID} LIMIT 1;";
        public const string LIST_BY_INSTANCE = $@"SELECT * FROM hook WHERE instance_id = {INSTANCE_ID} ORDER BY created DESC, id DESC;";
        public const string LIST_BY_INSTANCE_AND_STATE = $@"SELECT * FROM hook WHERE instance_id = {INSTANCE_ID} AND state_id = {STATE_ID} ORDER BY created DESC, id DESC;";
        public const string LIST_BY_INSTANCE_STATE_ENTRY = $@"SELECT * FROM hook WHERE instance_id = {INSTANCE_ID} AND state_id = {STATE_ID} AND on_entry = {ON_ENTRY} ORDER BY created DESC, id DESC;";

        // dispatched column removed from hook — it now lives on hook_lc.
        public const string INSERT = $@"INSERT INTO hook (instance_id, state_id, via_event, on_entry, route_id, type, group_id, order_seq, ack_mode, send_mode) VALUES ({INSTANCE_ID}, {STATE_ID}, {EVENT_ID}, {ON_ENTRY}, {ROUTE_ID}, {HOOK_TYPE}, {GROUP_ID}, {ORDER_SEQ}, {ACK_MODE}, {SEND_MODE}); SELECT LAST_INSERT_ID() AS id;";

        // group_id, type, order_seq and ack_mode are updated on re-emit so that policy changes are reflected.
        public const string UPDATE_TYPE_AND_GROUP = $@"UPDATE hook SET type = {HOOK_TYPE}, group_id = {GROUP_ID}, order_seq = {ORDER_SEQ}, ack_mode = {ACK_MODE}, send_mode = {SEND_MODE} WHERE id = {ID};";

        // Context query for post-ACK logic (any hook, grouped or not).
        // hook_ack.hook_id now references hook_lc.id — join chain: ack → hook_ack → hook_lc → hook.
        public const string GET_CONTEXT_BY_ACK_GUID =
            $@"SELECT h.id, h.instance_id, h.state_id, h.via_event, h.on_entry,
                      h.type, h.ack_mode, h.send_mode, h.order_seq, h.group_id, ha.ack_id,
                      hl.id AS hook_lc_id, hl.lc_id,
                      i.guid AS instance_guid, i.def_version AS def_version_id,
                      i.metadata AS metadata,
                      i.entity_id AS entity_id, i.policy_id,
                      dv.parent AS definition_id,
                      hr.name AS route
               FROM ack a
               JOIN hook_ack ha ON ha.ack_id = a.id
               JOIN hook_lc hl ON hl.id = ha.hook_id
               JOIN hook h ON h.id = hl.hook_id
               JOIN hook_route hr ON hr.id = h.route_id
               JOIN instance i ON i.id = h.instance_id
               JOIN def_version dv ON dv.id = i.def_version
               WHERE a.guid = lower(trim({GUID}))
               LIMIT 1;";

        public const string GET_CONTEXT_BY_LC_ID =
            $@"SELECT h.id, h.instance_id, h.state_id, h.via_event, h.on_entry,
                      h.type, h.ack_mode, h.send_mode, h.order_seq, h.group_id,
                      hl.id AS hook_lc_id, hl.lc_id,
                      i.guid AS instance_guid, i.def_version AS def_version_id,
                      i.metadata AS metadata,
                      i.entity_id AS entity_id, i.policy_id,
                      dv.parent AS definition_id,
                      hr.name AS route
               FROM hook_lc hl
               JOIN hook h ON h.id = hl.hook_id
               JOIN hook_route hr ON hr.id = h.route_id
               JOIN instance i ON i.id = h.instance_id
               JOIN def_version dv ON dv.id = i.def_version
               WHERE hl.lc_id = {LC_ID}
               ORDER BY h.order_seq ASC, h.id ASC
               LIMIT 1;";

        // Count gate+dispatched hooks in a given order for the current lifecycle entry
        // where at least one ack_consumer is non-terminal (Processed=3, Failed=4, Cancelled=5 all count as terminal).
        // Used by AckOutcomeOrchestrator to decide whether the current order is done enough to advance to the next.
        public const string COUNT_INCOMPLETE_BLOCKING_IN_ORDER =
            $@"SELECT COUNT(DISTINCT h.id) AS cnt
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               JOIN hook_ack ha ON ha.hook_id = hl.id
               JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id = {STATE_ID}
                 AND h.via_event = {EVENT_ID}
                 AND h.on_entry = {ON_ENTRY}
                 AND h.order_seq = {ORDER_SEQ}
                 AND h.type = 1
                 AND hl.dispatched = 1
                 AND ac.status NOT IN (3, 4, 5);";

        // Count all dispatched hooks in a given order for the current lifecycle entry that still have
        // non-terminal ack_consumer work. This is used for effect-batch advancement so an order moves on
        // only after the whole dispatched batch becomes terminal.
        public const string COUNT_INCOMPLETE_IN_ORDER =
            $@"SELECT COUNT(DISTINCT h.id) AS cnt
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id = {STATE_ID}
                 AND h.via_event = {EVENT_ID}
                 AND h.on_entry = {ON_ENTRY}
                 AND h.order_seq = {ORDER_SEQ}
                 AND hl.dispatched = 1
                 AND (
                   (h.ack_mode = 0 AND EXISTS (
                     SELECT 1 FROM hook_ack ha
                     JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
                     WHERE ha.hook_id = hl.id AND ac.status NOT IN (3, 4, 5)
                   ))
                   OR
                   (h.ack_mode = 1 AND NOT EXISTS (
                     SELECT 1 FROM hook_ack ha
                     JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
                     WHERE ha.hook_id = hl.id AND ac.status = 3
                   ))
                 );";

        // Find the next order_seq that still has queued hook_lc rows for this lifecycle entry.
        // Skipped rows (status=2) are already closed and must not be treated as pending dispatch work.
        public const string GET_MIN_UNDISPATCHED_ORDER =
            $@"SELECT MIN(h.order_seq) AS next_order
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id = {STATE_ID}
                 AND h.via_event = {EVENT_ID}
                 AND h.on_entry = {ON_ENTRY}
                 AND hl.dispatched = 0
                 AND hl.status <> 2;";

        // List all queued hooks for a specific order_seq scope for this lifecycle entry.
        // Returns hook_lc_id so callers can use it for CreateHookAckAsync + MarkDispatchedAsync.
        public const string LIST_UNDISPATCHED_BY_ORDER =
            $@"SELECT h.id, h.type, h.ack_mode, h.group_id, h.on_entry,
                      hl.id AS hook_lc_id,
                      hr.name AS route, hg.name AS group_name
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               JOIN hook_route hr ON hr.id = h.route_id
               LEFT JOIN hook_group hg ON hg.id = h.group_id
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id = {STATE_ID}
                 AND h.via_event = {EVENT_ID}
                 AND h.on_entry = {ON_ENTRY}
                 AND h.order_seq = {ORDER_SEQ}
                 AND hl.dispatched = 0
                 AND hl.status <> 2;";

        public const string LIST_UNDISPATCHED_BY_ORDER_AND_TYPE =
            $@"SELECT h.id, h.type, h.ack_mode, h.send_mode, h.group_id, h.on_entry,
                      hl.id AS hook_lc_id,
                      hr.name AS route, hg.name AS group_name
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               JOIN hook_route hr ON hr.id = h.route_id
               LEFT JOIN hook_group hg ON hg.id = h.group_id
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id = {STATE_ID}
                 AND h.via_event = {EVENT_ID}
                 AND h.on_entry = {ON_ENTRY}
                 AND h.order_seq = {ORDER_SEQ}
                 AND h.type = {HOOK_TYPE}
                 AND hl.dispatched = 0
                 AND hl.status <> 2
               ORDER BY h.id ASC;";

        // Gate hook gate — instance-wide checks scoped only to (instanceId, lcId).
        // Used by InstanceOrchestrator to prevent state transitions when gate hooks are unresolved.

        // Count dispatched gate hooks for the current lifecycle entry that are not yet resolved,
        // taking ack_mode into account:
        //   ack_mode = ALL (0): hook is unresolved if ANY ack_consumer is not terminal (status NOT IN 3,4,5)
        //   ack_mode = ANY (1): hook is unresolved if NO ack_consumer has Processed (status = 3)
        // Terminal statuses: Processed=3, Failed=4, Cancelled=5
        public const string COUNT_PENDING_BLOCKING_HOOK_ACKS =
            $@"SELECT COUNT(DISTINCT h.id) AS cnt
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.type = 1
                 AND hl.dispatched = 1
                 AND (
                   (h.ack_mode = 0 AND EXISTS (
                     SELECT 1 FROM hook_ack ha
                     JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
                     WHERE ha.hook_id = hl.id AND ac.status <> 3
                   ))
                   OR
                   (h.ack_mode = 1 AND NOT EXISTS (
                     SELECT 1 FROM hook_ack ha
                     JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
                     WHERE ha.hook_id = hl.id AND ac.status = 3
                   ))
                 );";

        // Count queued gate hooks for the current lifecycle entry that have not been dispatched yet.
        // Skipped rows remain dispatched=0, status=2 and must not be treated as queued work.
        public const string COUNT_UNDISPATCHED_BLOCKING_HOOKS =
            $@"SELECT COUNT(*) AS cnt
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.type = 1
                 AND hl.dispatched = 0
                 AND hl.status <> 2;";

        // Timeout cancellation: bulk-set all non-terminal ack_consumer rows for gate hooks in this
        // lifecycle entry to Cancelled (status = @ACK_STATUS = 5), clearing next_due.
        // Called by the monitor immediately before firing a Case A timeout transition so that:
        //   (a) open hook ACKs are properly closed before the state machine advances, and
        //   (b) any late ACK arriving afterwards is rejected with a STALE_ACK_RECEIVED notice.
        public const string CANCEL_PENDING_BLOCKING_HOOK_ACK_CONSUMERS =
            $@"UPDATE ack_consumer ac
               JOIN ack a ON a.id = ac.ack_id
               JOIN hook_ack ha ON ha.ack_id = a.id
               JOIN hook_lc hl ON hl.id = ha.hook_id AND hl.lc_id = {LC_ID}
               JOIN hook h ON h.id = hl.hook_id AND h.instance_id = {INSTANCE_ID}
               SET ac.status = {ACK_STATUS}, ac.next_due = NULL
               WHERE h.type = 1
                 AND ac.status NOT IN (3, 4, 5);";

        // Gate failure closes the entire remaining hook plan for this lifecycle entry.
        // Cancel all still-open hook ack_consumer rows so late effect ACKs cannot keep advancing
        // the old lifecycle while the engine is triggering the failure branch.
        public const string CANCEL_PENDING_HOOK_ACK_CONSUMERS =
            $@"UPDATE ack_consumer ac
               JOIN ack a ON a.id = ac.ack_id
               JOIN hook_ack ha ON ha.ack_id = a.id
               JOIN hook_lc hl ON hl.id = ha.hook_id AND hl.lc_id = {LC_ID}
               JOIN hook h ON h.id = hl.hook_id AND h.instance_id = {INSTANCE_ID}
               SET ac.status = {ACK_STATUS}, ac.next_due = NULL
               WHERE ac.status NOT IN (3, 4, 5);";

        // Gate-success drain: mark all queued gate hook_lc rows as Skipped (status=2) so they never
        // get dispatched. A row is marked dispatched only when ACK rows are created and the event is sent.
        // Called by AckOutcomeOrchestrator when a gate ACKs success with
        // an OnSuccessEvent, before draining the remaining effect hooks.
        public const string SKIP_UNDISPATCHED_GATE_HOOKS =
            $@"UPDATE hook_lc hl
               JOIN hook h ON h.id = hl.hook_id
               SET hl.status = 2
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id    = {STATE_ID}
                 AND h.via_event   = {EVENT_ID}
                 AND h.on_entry    = {ON_ENTRY}
                 AND hl.lc_id      = {LC_ID}
                 AND h.type        = 1
                 AND hl.dispatched = 0
                 AND hl.status <> 2;";

        public const string SKIP_UNDISPATCHED_NON_ALWAYS_EFFECTS_AFTER_ORDER =
            $@"UPDATE hook_lc hl
               JOIN hook h ON h.id = hl.hook_id
               SET hl.status = 2
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id    = {STATE_ID}
                 AND h.via_event   = {EVENT_ID}
                 AND h.on_entry    = {ON_ENTRY}
                 AND hl.lc_id      = {LC_ID}
                 AND h.type        = 0
                 AND h.send_mode   = 0
                 AND h.order_seq   > {ORDER_SEQ}
                 AND hl.dispatched = 0
                 AND hl.status <> 2;";

        // After effect drain completes, check if there is a skipped gate in scope (status=2).
        // Returns (route, state_id, via_event, on_entry) so caller can re-resolve OnSuccessEvent from policy.
        // The first skipped gate by order_seq is the one whose success code should be fired.
        public const string GET_FIRST_SKIPPED_GATE_ROUTE =
            $@"SELECT hr.name AS route, h.state_id, h.via_event, h.on_entry
               FROM hook_lc hl
               JOIN hook h ON h.id = hl.hook_id
               JOIN hook_route hr ON hr.id = h.route_id
               WHERE h.instance_id = {INSTANCE_ID}
                 AND hl.lc_id      = {LC_ID}
                 AND h.type        = 1
                 AND hl.status     = 2
               ORDER BY h.order_seq ASC
               LIMIT 1;";

        public const string LIST_PROCESSED_GATE_ROUTES_IN_ORDER =
            $@"SELECT hr.name AS route
               FROM hook h
               JOIN hook_lc hl ON hl.hook_id = h.id AND hl.lc_id = {LC_ID}
               JOIN hook_route hr ON hr.id = h.route_id
               WHERE h.instance_id = {INSTANCE_ID}
                 AND h.state_id    = {STATE_ID}
                 AND h.via_event   = {EVENT_ID}
                 AND h.on_entry    = {ON_ENTRY}
                 AND h.order_seq   = {ORDER_SEQ}
                 AND h.type        = 1
                 AND EXISTS (
                     SELECT 1 FROM hook_ack ha
                     JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
                     WHERE ha.hook_id = hl.id AND ac.status = 3
                 )
               ORDER BY h.id ASC;";

        public const string DELETE = $@"DELETE FROM hook WHERE id = {ID};";
        public const string DELETE_BY_INSTANCE = $@"DELETE FROM hook WHERE instance_id = {INSTANCE_ID};";
    }
}
