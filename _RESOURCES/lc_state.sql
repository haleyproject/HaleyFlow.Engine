-- --------------------------------------------------------
-- Host:                         127.0.0.1
-- Server version:               11.8.2-MariaDB - mariadb.org binary distribution
-- Server OS:                    Win64
-- HeidiSQL Version:             12.10.0.7000
-- --------------------------------------------------------

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET NAMES utf8 */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;


-- Dumping database structure for lcstate
-- Dumping structure for table lcstate.ack
CREATE TABLE IF NOT EXISTS `ack` (
  `guid` char(36) NOT NULL DEFAULT uuid() COMMENT 'Stable GUID used for external correlation.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_ack` (`guid`)
) ENGINE=InnoDB AUTO_INCREMENT=2000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Master acknowledgement envelope created by the engine and used for cross-system correlation.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.ack_consumer
CREATE TABLE IF NOT EXISTS `ack_consumer` (
  `consumer` int(11) NOT NULL DEFAULT 0 COMMENT 'Consumer identifier.',
  `ack_id` bigint(20) NOT NULL COMMENT 'Acknowledgement identifier (FK to ack.id).',
  `status` int(11) NOT NULL DEFAULT 1 COMMENT 'Acknowledgement state: 1=Pending, 2=Delivered, 3=Processed, 4=Failed 5=Cancelled',
  `last_trigger` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'Most recent dispatch trigger timestamp.',
  `trigger_count` int(11) NOT NULL DEFAULT 0 COMMENT 'Number of dispatch attempts performed so far.',
  `next_due` datetime DEFAULT NULL COMMENT 'Next scheduled timestamp for retry/dispatch. From C#, this is set as DateTime.UtcNow.. so we need to consider this time is in UTC time.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `modified` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'UTC timestamp when the row was last updated.',
  `max_trigger` int(11) NOT NULL DEFAULT 0,
  `message` text DEFAULT NULL,
  PRIMARY KEY (`ack_id`,`consumer`),
  UNIQUE KEY `unq_ack_consumer` (`ack_id`,`consumer`),
  KEY `idx_ack_consumer` (`next_due`,`status`),
  KEY `idx_ack_consumer_0` (`consumer`,`next_due`,`status`),
  CONSTRAINT `fk_ack_consumer_ack` FOREIGN KEY (`ack_id`) REFERENCES `ack` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Per-consumer delivery state for each acknowledgement, including retry scheduling and processing outcome.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.activity
CREATE TABLE IF NOT EXISTS `activity` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(140) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(140) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized activity key (lowercase trimmed display_name).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_activity_name` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=1998 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Catalog of business activities tracked by consumers outside core state transitions.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.activity_status
CREATE TABLE IF NOT EXISTS `activity_status` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(120) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized status key (lowercase trimmed display_name).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_activity_status_name` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=1998 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Catalog of runtime activity statuses used by consumer-side tracking.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.category
CREATE TABLE IF NOT EXISTS `category` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(120) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized category key (lowercase trimmed display_name).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_category` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=1999 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Catalog that classifies workflow states into functional groups.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.consumer
CREATE TABLE IF NOT EXISTS `consumer` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `env` int(11) NOT NULL COMMENT 'Environment identifier (FK to environment.id).',
  `consumer_guid` varchar(38) NOT NULL COMMENT 'Consumer instance GUID used for identity and heartbeat tracking.',
  `last_beat` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'Latest heartbeat timestamp reported by consumer.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_consumer_beat` (`env`,`consumer_guid`),
  CONSTRAINT `fk_consumer_beat_environment` FOREIGN KEY (`env`) REFERENCES `environment` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1995 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Registered consumer applications with environment binding and heartbeat for liveness detection.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.definition
CREATE TABLE IF NOT EXISTS `definition` (
  `env` int(11) NOT NULL DEFAULT 0 COMMENT 'Environment identifier (FK to environment.id).',
  `guid` char(36) NOT NULL DEFAULT uuid() COMMENT 'Stable GUID used for external correlation.',
  `display_name` varchar(200) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(200) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized definition key (lowercase trimmed display_name).',
  `description` text DEFAULT NULL COMMENT 'Optional descriptive text.',
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_definition_0` (`guid`),
  UNIQUE KEY `unq_definition` (`env`,`name`),
  KEY `idx_definition` (`name`),
  CONSTRAINT `fk_definition_environment` FOREIGN KEY (`env`) REFERENCES `environment` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1998 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Root workflow definition identity scoped to an environment.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.def_policies
CREATE TABLE IF NOT EXISTS `def_policies` (
  `definition` int(11) NOT NULL COMMENT 'Workflow definition identifier (FK to definition.id).',
  `policy` int(11) NOT NULL COMMENT 'Policy identifier (FK to policy.id).',
  `modified` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was last updated.',
  PRIMARY KEY (`definition`,`policy`),
  KEY `fk_def_policies_policy` (`policy`),
  CONSTRAINT `fk_def_policies_definition` FOREIGN KEY (`definition`) REFERENCES `definition` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_def_policies_policy` FOREIGN KEY (`policy`) REFERENCES `policy` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Associative mapping between workflow definitions and policy records.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.def_version
CREATE TABLE IF NOT EXISTS `def_version` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `guid` char(36) NOT NULL DEFAULT uuid() COMMENT 'Stable GUID used for external correlation.',
  `version` int(11) NOT NULL DEFAULT 1 COMMENT 'Monotonic version number starting from 1.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `modified` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'UTC timestamp when the row was last updated.',
  `parent` int(11) NOT NULL COMMENT 'Parent identifier (FK to the parent definition).',
  `data` longtext NOT NULL COMMENT 'Serialized workflow definition JSON for this version.',
  `hash` varchar(48) NOT NULL COMMENT 'Hash of serialized definition JSON for de-duplication.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_def_version` (`parent`,`version`),
  UNIQUE KEY `unq_def_version_0` (`guid`),
  UNIQUE KEY `unq_def_version_1` (`parent`,`hash`),
  CONSTRAINT `fk_def_version_definition` FOREIGN KEY (`parent`) REFERENCES `definition` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `cns_def_version` CHECK (`version` > 0),
  CONSTRAINT `cns_def_version_0` CHECK (json_valid(`data`))
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Immutable version snapshots of workflow definition payloads.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.environment
CREATE TABLE IF NOT EXISTS `environment` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(120) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized environment key (lowercase trimmed display_name).',
  `code` int(11) NOT NULL COMMENT 'Numeric code used by application contracts.',
  `guid` varchar(42) NOT NULL DEFAULT uuid() COMMENT 'Stable GUID used for external correlation.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_environment` (`code`),
  UNIQUE KEY `unq_environment_1` (`guid`),
  UNIQUE KEY `unq_environment_0` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=1998 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Logical environment/tenant boundary used to isolate definitions and consumers.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.events
CREATE TABLE IF NOT EXISTS `events` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable display name.',
  `code` int(11) NOT NULL COMMENT 'Numeric code used by application contracts.',
  `name` varchar(120) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized event key (lowercase trimmed display_name).',
  `def_version` int(11) NOT NULL COMMENT 'Definition version identifier (FK to def_version.id).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_events_0` (`def_version`,`code`),
  UNIQUE KEY `unq_events` (`def_version`,`code`,`name`),
  CONSTRAINT `fk_events_def_version` FOREIGN KEY (`def_version`) REFERENCES `def_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1989 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Event catalog for each definition version; events are used to trigger transitions.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.hook
CREATE TABLE IF NOT EXISTS `hook` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `state_id` int(11) NOT NULL COMMENT 'State identifier in current workflow context.',
  `via_event` int(11) NOT NULL COMMENT 'Event identifier that caused this hook emission.',
  `on_entry` bit(1) NOT NULL DEFAULT b'1' COMMENT 'Hook phase flag: 1=OnEntry, 0=OnExit.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `instance_id` bigint(20) NOT NULL COMMENT 'Workflow instance identifier (FK to instance.id).',
  `type` bit(1) NOT NULL DEFAULT b'1' COMMENT 'Decides if a hook is bloking or not\n1 - Gate : requires successful hook completion before progression.\n0 - Effect : if fails, nothing happens.',
  `order_seq` int(11) NOT NULL DEFAULT 1 COMMENT 'Dispatch order sequence; lower numbers are dispatched first.',
  `ack_mode` tinyint(4) NOT NULL DEFAULT 0 COMMENT 'ACK aggregation mode: 0=AllConsumersMustProcess, 1=AnyConsumerMaySatisfy.',
  `send_mode` tinyint(4) NOT NULL DEFAULT 0 COMMENT 'Effect carry-forward mode: 0=No, 1=Always. Relevant only for effect hooks.',
  `route_id` bigint(20) NOT NULL COMMENT 'Route identifier to invoke for this hook (FK to hook_route.id).',
  `group_id` bigint(20) DEFAULT NULL COMMENT 'Optional grouping identifier for related hooks (FK to hook_group.id).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_hooks` (`instance_id`,`state_id`,`via_event`,`on_entry`,`route_id`),
  KEY `fk_hook_hook_route` (`route_id`),
  KEY `fk_hook_hook_group` (`group_id`),
  CONSTRAINT `fk_hook_hook_group` FOREIGN KEY (`group_id`) REFERENCES `hook_group` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_hook_hook_route` FOREIGN KEY (`route_id`) REFERENCES `hook_route` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_hooks_instance` FOREIGN KEY (`instance_id`) REFERENCES `instance` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=2000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Hook work items emitted by policy on lifecycle edges and routed to consumers.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.hook_ack
CREATE TABLE IF NOT EXISTS `hook_ack` (
  `ack_id` bigint(20) NOT NULL COMMENT 'Acknowledgement identifier (FK to ack.id).',
  `hook_id` bigint(20) NOT NULL COMMENT 'Hook lifecycle identifier (FK to hook_lc.id).',
  PRIMARY KEY (`hook_id`),
  UNIQUE KEY `unq_hook_ack` (`ack_id`),
  CONSTRAINT `fk_hook_ack_ack` FOREIGN KEY (`ack_id`) REFERENCES `ack` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_hook_ack_hook_lc` FOREIGN KEY (`hook_id`) REFERENCES `hook_lc` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='One-to-one mapping between hook lifecycle entries and acknowledgement envelopes.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.hook_group
CREATE TABLE IF NOT EXISTS `hook_group` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `name` varchar(140) NOT NULL COMMENT 'Unique hook group name used to classify related routes.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_hook_group` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Named grouping of hook routes for orchestration and operational organization.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.hook_lc
CREATE TABLE IF NOT EXISTS `hook_lc` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `dispatched` bit(1) NOT NULL DEFAULT b'0' COMMENT 'Dispatch marker: 0=NotDispatched, 1=Dispatched.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `hook_id` bigint(20) NOT NULL COMMENT 'Hook identifier.',
  `lc_id` bigint(20) NOT NULL COMMENT 'Lifecycle identifier (FK to lifecycle.id).',
  `status` tinyint(4) NOT NULL DEFAULT 0 COMMENT '0=Pending, 1=Dispatched, 2=Skipped',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_hook_lc` (`hook_id`,`lc_id`),
  KEY `fk_hook_lc_lifecycle` (`lc_id`),
  CONSTRAINT `fk_hook_lc_hook` FOREIGN KEY (`hook_id`) REFERENCES `hook` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_hook_lc_lifecycle` FOREIGN KEY (`lc_id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Link between hook entries and lifecycle records, including dispatch marker.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.hook_route
CREATE TABLE IF NOT EXISTS `hook_route` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `name` varchar(240) NOT NULL COMMENT 'Unique hook route name invoked by consumers.',
  `label` varchar(120) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_hook_route_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Canonical route names targeted by hook dispatching.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.instance
CREATE TABLE IF NOT EXISTS `instance` (
  `revision` bigint(20) NOT NULL DEFAULT 0 COMMENT 'Incremented on every transition, including self-loops.',
  `def_version` int(11) NOT NULL COMMENT 'Definition version identifier (FK to def_version.id).',
  `current_state` int(11) NOT NULL DEFAULT 0 COMMENT 'Current state identifier for this instance.',
  `last_event` int(11) DEFAULT NULL COMMENT 'Most recently applied event identifier.',
  `guid` char(36) NOT NULL DEFAULT uuid() COMMENT 'Stable GUID used for external correlation.',
  `entity_id` varchar(36) NOT NULL COMMENT 'External business entity identifier (for example submission or application id).',
  `def_id` int(11) NOT NULL COMMENT 'Workflow definition identifier.',
  `policy_id` int(11) DEFAULT 0 COMMENT 'Policy identifier associated with this record.',
  `flags` int(10) unsigned NOT NULL DEFAULT 0 COMMENT 'Instance flags bitmask: 1=Active, 2=Suspended, 4=Completed, 8=Failed, 16=Archived.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `modified` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'UTC timestamp when the row was last updated.',
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `message` text DEFAULT NULL COMMENT 'Operational note describing current state or failure context.',
  `metadata` longtext DEFAULT NULL COMMENT 'Immutable initiation metadata (example: {"source":"backfill"}).',
  `context` longtext DEFAULT NULL COMMENT 'Mutable runtime context bag updated by workflow processing.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_instance` (`guid`),
  UNIQUE KEY `unq_instance_0` (`def_id`,`entity_id`),
  KEY `fk_instance_state` (`current_state`),
  KEY `fk_instance_events` (`last_event`),
  KEY `fk_instance_def_version` (`def_version`),
  KEY `idx_instance` (`entity_id`),
  CONSTRAINT `fk_instance_def_version` FOREIGN KEY (`def_version`) REFERENCES `def_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_instance_definition` FOREIGN KEY (`def_id`) REFERENCES `definition` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1857 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Workflow instance aggregate for one business entity, including immutable metadata and mutable runtime context.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lcn_ack
CREATE TABLE IF NOT EXISTS `lcn_ack` (
  `ack_id` bigint(20) NOT NULL,
  `lc_id` bigint(20) NOT NULL COMMENT 'same lc id is reused..',
  PRIMARY KEY (`lc_id`),
  KEY `fk_lcn_ack_ack` (`ack_id`),
  CONSTRAINT `fk_lcn_ack_ack` FOREIGN KEY (`ack_id`) REFERENCES `ack` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_lcn_ack_lifecycle` FOREIGN KEY (`lc_id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='acknowledgmeent for lc next.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lc_ack
CREATE TABLE IF NOT EXISTS `lc_ack` (
  `ack_id` bigint(20) NOT NULL COMMENT 'Acknowledgement identifier (FK to ack.id).',
  `lc_id` bigint(20) NOT NULL COMMENT 'Lifecycle identifier (FK to lifecycle.id).',
  PRIMARY KEY (`lc_id`),
  UNIQUE KEY `idx_lc_ack` (`ack_id`),
  CONSTRAINT `fk_lc_ack_ack` FOREIGN KEY (`ack_id`) REFERENCES `ack` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_lc_ack_lifecycle` FOREIGN KEY (`lc_id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Mapping between lifecycle transitions and acknowledgement envelopes for transition delivery.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lc_data
CREATE TABLE IF NOT EXISTS `lc_data` (
  `lc_id` bigint(20) NOT NULL COMMENT 'Lifecycle identifier (FK to lifecycle.id).',
  `actor` varchar(60) DEFAULT NULL COMMENT 'Actor identity (user/system/service) for this operation.',
  `payload` longtext DEFAULT NULL COMMENT 'Input payload captured for traceability and replay diagnostics.',
  PRIMARY KEY (`lc_id`),
  CONSTRAINT `fk_transition_data_transition_log` FOREIGN KEY (`lc_id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Supplemental actor and payload details captured for lifecycle transitions.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lc_next
CREATE TABLE IF NOT EXISTS `lc_next` (
  `id` bigint(20) NOT NULL,
  `created` datetime NOT NULL DEFAULT current_timestamp(),
  `next` int(11) NOT NULL DEFAULT 0 COMMENT 'suggested Code . If 0, then it means, we have completed the hooks, but noone of them have the complete codes.. even the transition itself doens'' thave the code.. so, we just need to inform the consumer to complete this.. and consumer should know what to transition next.. (its a case of hard coded transitions)',
  `ack_id` bigint(20) DEFAULT NULL COMMENT 'acknolwedgement id associated which triggered this .. may be a hook triggered this.. or the transition itself triggered this.. sometimes. transsition triggered this, (created this) but the complete codes comes from hooks.. so, we just captured after which ack we created this? or it can even tbe null',
  `dispatched` bit(1) NOT NULL DEFAULT b'0' COMMENT 'did we actually create a acknowledgemetn? our job is only to create the acknowledgmeent.. then the existing infrastructure takes care of this.. like monitor and events..',
  PRIMARY KEY (`id`),
  CONSTRAINT `fk_lc_next_lifecycle` FOREIGN KEY (`id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lc_timeout
CREATE TABLE IF NOT EXISTS `lc_timeout` (
  `lc_id` bigint(20) NOT NULL COMMENT 'Lifecycle identifier (FK to lifecycle.id).',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `trigger_count` int(11) NOT NULL DEFAULT 0,
  `last_trigger` datetime DEFAULT NULL,
  `next_due` datetime DEFAULT NULL,
  `max_retry` int(11) DEFAULT NULL COMMENT 'if null, then take the default max_retry fo engine.. else, use max retry from the policy',
  PRIMARY KEY (`lc_id`),
  CONSTRAINT `fk_timeout_lifecycle` FOREIGN KEY (`lc_id`) REFERENCES `lifecycle` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Marker table for lifecycle entries generated by timeout processing.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.lifecycle
CREATE TABLE IF NOT EXISTS `lifecycle` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `from_state` int(11) NOT NULL COMMENT 'Source state identifier.',
  `to_state` int(11) NOT NULL COMMENT 'Target state identifier.',
  `event` int(11) NOT NULL COMMENT 'Event identifier that triggered this transition.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `instance_id` bigint(20) NOT NULL COMMENT 'Workflow instance identifier (FK to instance.id).',
  `occurred` datetime DEFAULT NULL COMMENT 'Domain occurrence timestamp from source event.',
  PRIMARY KEY (`id`),
  KEY `fk_transition_log_instance` (`instance_id`),
  CONSTRAINT `fk_transition_log_instance` FOREIGN KEY (`instance_id`) REFERENCES `instance` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=2000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Immutable audit log of state-machine transitions for workflow instances.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.policy
CREATE TABLE IF NOT EXISTS `policy` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `hash` varchar(48) NOT NULL COMMENT 'Hash of policy JSON content used for de-duplication.',
  `content` text NOT NULL COMMENT 'Policy definition JSON (hooks, attach modes, timeout configuration).',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_policy` (`hash`)
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Policy payload store defining hook behavior, attachment modes, and timeout rules.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.runtime
CREATE TABLE IF NOT EXISTS `runtime` (
  `instance_id` bigint(20) NOT NULL COMMENT 'Workflow instance identifier (FK to instance.id).',
  `activity` int(11) NOT NULL COMMENT 'Activity identifier (FK to activity.id).',
  `state_id` int(11) NOT NULL COMMENT 'State identifier in current workflow context.',
  `actor_id` varchar(60) NOT NULL DEFAULT '0' COMMENT 'Actor identifier for runtime activity tracking.',
  `status` int(11) NOT NULL COMMENT 'Runtime activity status identifier (FK to activity_status.id).',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `modified` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was last updated.',
  `frozen` bit(1) NOT NULL DEFAULT b'0' COMMENT 'Freeze marker: 1 prevents status changes until explicitly unlocked.',
  `lc_id` bigint(20) NOT NULL DEFAULT 0 COMMENT 'Lifecycle identifier (FK to lifecycle.id).',
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_execution` (`instance_id`,`state_id`,`activity`,`actor_id`),
  KEY `fk_execution_runtime_state` (`status`),
  KEY `fk_runtime_activity` (`activity`),
  CONSTRAINT `fk_execution_runtime_state` FOREIGN KEY (`status`) REFERENCES `activity_status` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_runtime_activity` FOREIGN KEY (`activity`) REFERENCES `activity` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_runtime_instance` FOREIGN KEY (`instance_id`) REFERENCES `instance` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=2000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Consumer-reported runtime activity status for instance/state/activity/actor context.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.runtime_data
CREATE TABLE IF NOT EXISTS `runtime_data` (
  `runtime` bigint(20) NOT NULL COMMENT 'Runtime row identifier (FK to runtime.id).',
  `data` longtext DEFAULT NULL COMMENT 'Serialized data payload (typically JSON).',
  `payload` longtext DEFAULT NULL COMMENT 'Input payload captured for traceability and replay diagnostics.',
  PRIMARY KEY (`runtime`),
  CONSTRAINT `fk_runtime_data_runtime` FOREIGN KEY (`runtime`) REFERENCES `runtime` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Supplemental display and payload data for runtime records.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.state
CREATE TABLE IF NOT EXISTS `state` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `display_name` varchar(200) NOT NULL COMMENT 'Human-readable display name.',
  `name` varchar(200) GENERATED ALWAYS AS (lcase(trim(`display_name`))) VIRTUAL COMMENT 'System-generated normalized state key (lowercase trimmed display_name).',
  `category` int(11) NOT NULL DEFAULT 0 COMMENT 'State category identifier (FK to category.id).',
  `flags` int(10) unsigned NOT NULL DEFAULT 0 COMMENT 'State flags bitmask: 1=Initial, 2=Final, 4=System, 8=Error.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `def_version` int(11) NOT NULL COMMENT 'Definition version identifier (FK to def_version.id).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_state` (`def_version`,`name`),
  KEY `fk_state_category` (`category`),
  CONSTRAINT `fk_state_category` FOREIGN KEY (`category`) REFERENCES `category` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_state_def_version` FOREIGN KEY (`def_version`) REFERENCES `def_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=2014 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='State catalog for a workflow definition version.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.timeouts
CREATE TABLE IF NOT EXISTS `timeouts` (
  `policy_id` int(11) NOT NULL COMMENT 'Policy identifier associated with this record.',
  `state_name` varchar(200) NOT NULL COMMENT 'State name this rule targets.',
  `duration` int(11) NOT NULL COMMENT 'Timeout duration value interpreted by engine timeout semantics.',
  `mode` int(11) NOT NULL DEFAULT 0 COMMENT 'Timeout mode: 0=Once, 1=Repeat.',
  `event_code` int(11) DEFAULT NULL COMMENT 'Event code emitted when timeout is reached.',
  `max_retry` int(11) DEFAULT NULL,
  PRIMARY KEY (`policy_id`,`state_name`),
  CONSTRAINT `fk_timeouts_policy` FOREIGN KEY (`policy_id`) REFERENCES `policy` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Timeout rules per policy and state, including mode and event to emit on expiry.';

-- Data exporting was unselected.

-- Dumping structure for table lcstate.transition
CREATE TABLE IF NOT EXISTS `transition` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Internal surrogate identifier.',
  `from_state` int(11) NOT NULL COMMENT 'Source state identifier.',
  `to_state` int(11) NOT NULL COMMENT 'Target state identifier.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'UTC timestamp when the row was created.',
  `def_version` int(11) NOT NULL COMMENT 'Definition version identifier (FK to def_version.id).',
  `event` int(11) NOT NULL COMMENT 'Event identifier that triggered this transition.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_transition` (`def_version`,`from_state`,`to_state`,`event`),
  KEY `fk_transition_state` (`from_state`),
  KEY `fk_transition_state_0` (`to_state`),
  KEY `fk_transition_events` (`event`),
  CONSTRAINT `fk_transition_def_version` FOREIGN KEY (`def_version`) REFERENCES `def_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_transition_events` FOREIGN KEY (`event`) REFERENCES `events` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_transition_state` FOREIGN KEY (`from_state`) REFERENCES `state` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_transition_state_0` FOREIGN KEY (`to_state`) REFERENCES `state` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Allowed transition graph edges per definition version.';

-- Data exporting was unselected.

/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;

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
