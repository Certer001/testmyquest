CREATE OR REPLACE VIEW autocheck.flow_versions AS
SELECT
  flow_name,
  flow_version,
  status,
  is_active,
  published_at
FROM workflow.flow_versions;

CREATE OR REPLACE VIEW autocheck.processes AS
SELECT
  process_id,
  business_key,
  flow_name,
  flow_version,
  state,
  current_step_key,
  created_at,
  updated_at
FROM workflow.process_instances;

CREATE OR REPLACE VIEW autocheck.steps AS
SELECT
  step_instance_id,
  process_id,
  step_key,
  step_type,
  state,
  outcome,
  entered_at,
  completed_at
FROM workflow.step_instances;

CREATE OR REPLACE VIEW autocheck.jobs AS
SELECT
  job_id,
  process_id,
  step_instance_id,
  execution_id,
  state,
  lease_owner,
  lease_version,
  lease_until,
  attempt_count,
  next_attempt_at
FROM workflow.jobs;

CREATE OR REPLACE VIEW autocheck.attempts AS
SELECT
  attempt_id,
  job_id,
  execution_id,
  lease_version,
  attempt_number,
  status,
  outcome,
  error_code,
  started_at,
  finished_at
FROM workflow.task_attempts;

CREATE OR REPLACE VIEW autocheck.signals AS
SELECT
  message_id,
  process_id,
  signal_type,
  body_hash,
  status,
  received_at
FROM workflow.signals;

CREATE OR REPLACE VIEW autocheck.workflow_events AS
SELECT
  event_id,
  process_id,
  step_instance_id,
  event_type,
  occurred_at
FROM workflow.workflow_events;

GRANT SELECT ON
  autocheck.flow_versions,
  autocheck.processes,
  autocheck.steps,
  autocheck.jobs,
  autocheck.attempts,
  autocheck.signals,
  autocheck.workflow_events
  TO course_runtime, course_publication, workflow_worker;

REVOKE INSERT, UPDATE, DELETE ON
  autocheck.flow_versions,
  autocheck.processes,
  autocheck.steps,
  autocheck.jobs,
  autocheck.attempts,
  autocheck.signals,
  autocheck.workflow_events
  FROM course_runtime, course_publication, workflow_worker;
