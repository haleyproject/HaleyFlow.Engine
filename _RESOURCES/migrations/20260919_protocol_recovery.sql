-- Apply explicitly to the engine database with writers stopped. Never run automatically at startup.
-- Back up first. Resolve duplicate natural keys before retrying; this migration never deletes data.
-- DDL auto-commits in MariaDB. This script is rerunnable after a failed preflight or interrupted upgrade.
DELIMITER //
CREATE OR REPLACE PROCEDURE haleyflow_protocol_preflight()
BEGIN
  IF EXISTS (SELECT 1 FROM activity GROUP BY name HAVING COUNT(*) > 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Duplicate activity names: resolve ownership and references before upgrading.';
  END IF;
  IF EXISTS (SELECT 1 FROM activity_status GROUP BY name HAVING COUNT(*) > 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Duplicate activity_status names: resolve references before upgrading.';
  END IF;
  IF EXISTS (SELECT 1 FROM hook_route GROUP BY name HAVING COUNT(*) > 1) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Duplicate hook_route names: reconcile labels and hook references before upgrading.';
  END IF;
END//
DELIMITER ;
CALL haleyflow_protocol_preflight();
DROP PROCEDURE haleyflow_protocol_preflight;

ALTER TABLE instance ADD COLUMN IF NOT EXISTS revision bigint(20) NOT NULL DEFAULT 0;
ALTER TABLE hook MODIFY COLUMN order_seq int(11) NOT NULL DEFAULT 1;
ALTER TABLE activity ADD UNIQUE INDEX IF NOT EXISTS unq_activity_name (name);
ALTER TABLE activity_status ADD UNIQUE INDEX IF NOT EXISTS unq_activity_status_name (name);
-- Add the replacement before removing the old index, so interruption cannot remove uniqueness.
ALTER TABLE hook_route ADD UNIQUE INDEX IF NOT EXISTS unq_hook_route_name (name);
ALTER TABLE hook_route DROP INDEX IF EXISTS unq_route;

-- Durable execution recovery and caller idempotency. No dispatch occurs without persisted work.
CREATE TABLE IF NOT EXISTS lc_execution (
  lc_id bigint(20) NOT NULL,
  status tinyint NOT NULL DEFAULT 0 COMMENT '0=Pending, 1=Complete, 2=FailureContinuationPending, 3=Blocked',
  next_event int DEFAULT NULL,
  PRIMARY KEY (lc_id),
  KEY idx_lc_execution_pending (status, lc_id),
  CONSTRAINT fk_lc_execution_lifecycle FOREIGN KEY (lc_id) REFERENCES lifecycle(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS trigger_receipt (
  instance_id bigint(20) NOT NULL,
  request_id varchar(160) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  result longtext NOT NULL,
  PRIMARY KEY (instance_id, request_id),
  CONSTRAINT fk_trigger_receipt_instance FOREIGN KEY (instance_id) REFERENCES instance(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS backfill_import (
  instance_id bigint(20) NOT NULL,
  content_hash char(64) NOT NULL,
  PRIMARY KEY (instance_id),
  CONSTRAINT fk_backfill_import_instance FOREIGN KEY (instance_id) REFERENCES instance(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Recover existing, current lifecycle work with a transition ACK. Historical backfills have no ACK.
INSERT IGNORE INTO lc_execution (lc_id, status)
SELECT l.id, IF(EXISTS (SELECT 1 FROM lcn_ack na WHERE na.lc_id = l.id), 1, 0)
FROM lifecycle l JOIN lc_ack la ON la.lc_id = l.id
WHERE l.id = (SELECT MAX(l2.id) FROM lifecycle l2 WHERE l2.instance_id = l.instance_id);

-- Correct persisted ordering using the pinned policy and the same last-specific-else-last-general rule.
-- Existing explicit 999 remains explicit. A missing order becomes the reserved last value.
UPDATE hook h
JOIN instance i ON i.id = h.instance_id
JOIN policy p ON p.id = i.policy_id
JOIN state s ON s.id = h.state_id
JOIN events ev ON ev.id = h.via_event
JOIN hook_route hr ON hr.id = h.route_id
SET h.order_seq = COALESCE((
  SELECT COALESCE(em.hook_order, 2147483647)
  FROM JSON_TABLE(p.content, '$.rules[*]' COLUMNS (
    rule_no FOR ORDINALITY, state_name varchar(140) PATH '$.state',
    via_code int PATH '$.via', body JSON PATH '$'
  )) ruleset
  JOIN JSON_TABLE(ruleset.body, '$.emit[*]' COLUMNS (
    route_name varchar(255) PATH '$.route', hook_order int PATH '$.order'
  )) em
  WHERE lower(trim(ruleset.state_name)) COLLATE utf8mb4_unicode_ci = s.name AND em.route_name COLLATE utf8mb4_unicode_ci = hr.name
    AND (ruleset.via_code IS NULL OR ruleset.via_code = ev.code)
    AND ruleset.rule_no = (
      SELECT r2.rule_no FROM JSON_TABLE(p.content, '$.rules[*]' COLUMNS (
        rule_no FOR ORDINALITY, state_name varchar(140) PATH '$.state', via_code int PATH '$.via'
      )) r2
      WHERE lower(trim(r2.state_name)) COLLATE utf8mb4_unicode_ci = s.name AND (r2.via_code IS NULL OR r2.via_code = ev.code)
      ORDER BY (r2.via_code IS NOT NULL) DESC, r2.rule_no DESC LIMIT 1
    )
  LIMIT 1
), h.order_seq);
-- Already released work cannot be unexecuted. Review active plans that previously combined fallback rules.
