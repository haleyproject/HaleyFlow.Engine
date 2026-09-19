namespace Haley.Internal {
    internal static class QRY_EXECUTION {
        public const string LOCK_INSTANCE = "SELECT * FROM instance WHERE id = @instance_id FOR UPDATE;";
        public const string LIFECYCLE_BY_ACK = @"SELECT lc_id FROM (
            SELECT la.lc_id FROM lc_ack la JOIN ack a ON a.id = la.ack_id WHERE a.guid = lower(trim(@guid))
            UNION ALL SELECT hl.lc_id FROM hook_ack ha JOIN hook_lc hl ON hl.id = ha.hook_id JOIN ack a ON a.id = ha.ack_id WHERE a.guid = lower(trim(@guid))
            UNION ALL SELECT na.lc_id FROM lcn_ack na JOIN ack a ON a.id = na.ack_id WHERE a.guid = lower(trim(@guid))
        ) source LIMIT 1;";
        public const string ENSURE = "INSERT INTO lc_execution (lc_id) VALUES (@lc_id) ON DUPLICATE KEY UPDATE lc_id = lc_id;";
        public const string CONTINUATION_READY = @"SELECT CASE
            WHEN EXISTS (SELECT 1 FROM hook_ack ha WHERE ha.ack_id = a.id) THEN -1
            WHEN EXISTS (SELECT 1 FROM lc_ack la JOIN hook_lc hl ON hl.lc_id = la.lc_id WHERE la.ack_id = a.id) THEN -1
            WHEN EXISTS (SELECT 1 FROM ack_consumer ac WHERE ac.ack_id = a.id)
             AND NOT EXISTS (SELECT 1 FROM ack_consumer ac WHERE ac.ack_id = a.id AND ac.status <> 3) THEN 1
            ELSE 0 END FROM ack a WHERE a.guid = lower(trim(@guid)) FOR UPDATE;";
        public const string GET = "SELECT * FROM lc_execution WHERE lc_id = @lc_id FOR UPDATE;";
        public const string SET = "UPDATE lc_execution SET status = @status, next_event = @next_event WHERE lc_id = @lc_id;";
        public const string PENDING = "SELECT lc_id FROM lc_execution WHERE status IN (0, 2) AND lc_id > @after_id ORDER BY lc_id LIMIT @take;";
        public const string VALIDATION_ACKS = @"SELECT ac.* FROM lc_ack la JOIN ack_consumer ac ON ac.ack_id = la.ack_id WHERE la.lc_id = @lc_id;";
        public const string HOOKS = @"SELECT h.*, hl.id AS hook_lc_id, hl.created AS hook_created, hl.dispatched, hl.status AS hook_status,
            hr.name AS route, hg.name AS group_name, ha.ack_id,
            COUNT(ac.consumer) AS total, COALESCE(SUM(ac.status = 3), 0) AS processed,
            COALESCE(SUM(ac.status IN (4,5)), 0) AS failed,
            COALESCE(SUM(ac.status IN (1,2)), 0) AS pending
            FROM hook_lc hl JOIN hook h ON h.id = hl.hook_id
            JOIN hook_route hr ON hr.id = h.route_id LEFT JOIN hook_group hg ON hg.id = h.group_id
            LEFT JOIN hook_ack ha ON ha.hook_id = hl.id LEFT JOIN ack_consumer ac ON ac.ack_id = ha.ack_id
            WHERE hl.lc_id = @lc_id
            GROUP BY h.id, hl.id, hl.created, hl.dispatched, hl.status, hr.name, hg.name, ha.ack_id
            ORDER BY h.order_seq, h.type, h.id;";
        public const string GET_RECEIPT = "SELECT result FROM trigger_receipt WHERE instance_id = @instance_id AND request_id = @request_id FOR UPDATE;";
        public const string SAVE_RECEIPT = "INSERT INTO trigger_receipt (instance_id, request_id, result) VALUES (@instance_id, @request_id, @result);";
        public const string GET_BACKFILL = "SELECT content_hash FROM backfill_import WHERE instance_id = @instance_id;";
        public const string SAVE_BACKFILL = "INSERT INTO backfill_import (instance_id, content_hash) VALUES (@instance_id, @hash);";
    }
}
