-- Week 2 workflow roles, schema, and tables
DO $$ BEGIN
  CREATE ROLE workflow_worker LOGIN PASSWORD 'workflow_worker';
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

CREATE SCHEMA IF NOT EXISTS workflow AUTHORIZATION course_owner;

CREATE TABLE IF NOT EXISTS course.settings (
  key text PRIMARY KEY,
  value text NOT NULL
);

CREATE TABLE IF NOT EXISTS workflow.flow_versions (
  flow_name text NOT NULL,
  flow_version integer NOT NULL,
  status text NOT NULL DEFAULT 'PUBLISHED',
  is_active boolean NOT NULL DEFAULT false,
  published_at timestamptz NOT NULL DEFAULT now(),
  start_step text NOT NULL,
  map_definition jsonb NOT NULL,
  map_checksum text NOT NULL,
  PRIMARY KEY (flow_name, flow_version),
  CONSTRAINT flow_versions_status_check CHECK (status = 'PUBLISHED'),
  CONSTRAINT flow_versions_version_check CHECK (flow_version >= 1)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_flow_versions_one_active
  ON workflow.flow_versions (flow_name)
  WHERE is_active;

CREATE TABLE IF NOT EXISTS workflow.process_instances (
  process_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  business_key text NOT NULL,
  flow_name text NOT NULL,
  flow_version integer NOT NULL,
  state text NOT NULL,
  current_step_key text,
  process_data jsonb NOT NULL DEFAULT '{}'::jsonb,
  data_hash text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT process_instances_state_check CHECK (
    state IN ('CREATED', 'RUNNING', 'WAITING_SIGNAL', 'WAITING_MANUAL', 'COMPLETED', 'FAILED')
  ),
  CONSTRAINT process_instances_flow_fk FOREIGN KEY (flow_name, flow_version)
    REFERENCES workflow.flow_versions (flow_name, flow_version),
  CONSTRAINT process_instances_business_key_unique UNIQUE (flow_name, business_key)
);

CREATE INDEX IF NOT EXISTS idx_process_instances_flow
  ON workflow.process_instances (flow_name, flow_version);

CREATE TABLE IF NOT EXISTS workflow.step_instances (
  step_instance_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  process_id uuid NOT NULL REFERENCES workflow.process_instances (process_id),
  step_key text NOT NULL,
  step_type text NOT NULL,
  state text NOT NULL,
  outcome text,
  entered_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz,
  signal_type text,
  wait_outcome text,
  end_outcome text,
  allowed_outcomes jsonb,
  task_definition jsonb,
  CONSTRAINT step_instances_type_check CHECK (
    step_type IN ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')
  ),
  CONSTRAINT step_instances_state_check CHECK (
    state IN ('PENDING', 'READY', 'RUNNING', 'WAITING', 'COMPLETED', 'FAILED')
  )
);

CREATE INDEX IF NOT EXISTS idx_step_instances_process
  ON workflow.step_instances (process_id, entered_at);

CREATE TABLE IF NOT EXISTS workflow.jobs (
  job_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  process_id uuid NOT NULL REFERENCES workflow.process_instances (process_id),
  step_instance_id uuid NOT NULL UNIQUE REFERENCES workflow.step_instances (step_instance_id),
  execution_id uuid NOT NULL,
  state text NOT NULL,
  lease_owner text,
  lease_version bigint NOT NULL DEFAULT 0,
  lease_until timestamptz,
  attempt_count integer NOT NULL DEFAULT 0,
  next_attempt_at timestamptz,
  max_attempts integer NOT NULL DEFAULT 1,
  retry_delays_ms jsonb NOT NULL DEFAULT '[]'::jsonb,
  CONSTRAINT jobs_state_check CHECK (
    state IN ('READY', 'LEASED', 'RETRY_WAIT', 'SUCCEEDED', 'DEAD')
  ),
  CONSTRAINT jobs_attempt_count_check CHECK (attempt_count >= 0),
  CONSTRAINT jobs_max_attempts_check CHECK (max_attempts >= 1 AND max_attempts <= 10)
);

CREATE INDEX IF NOT EXISTS idx_jobs_claim
  ON workflow.jobs (state, next_attempt_at, lease_until);

CREATE INDEX IF NOT EXISTS idx_jobs_process
  ON workflow.jobs (process_id);

CREATE TABLE IF NOT EXISTS workflow.task_attempts (
  attempt_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  job_id uuid NOT NULL REFERENCES workflow.jobs (job_id),
  execution_id uuid NOT NULL,
  lease_version bigint NOT NULL,
  attempt_number integer NOT NULL,
  status text NOT NULL,
  outcome text,
  error_code text,
  error_message text,
  started_at timestamptz NOT NULL DEFAULT now(),
  finished_at timestamptz,
  CONSTRAINT task_attempts_status_check CHECK (
    status IN ('RUNNING', 'SUCCEEDED', 'FAILED', 'STALE')
  ),
  CONSTRAINT task_attempts_attempt_number_check CHECK (attempt_number >= 1),
  UNIQUE (job_id, attempt_number),
  UNIQUE (job_id, lease_version)
);

CREATE INDEX IF NOT EXISTS idx_task_attempts_job
  ON workflow.task_attempts (job_id, attempt_number);

CREATE TABLE IF NOT EXISTS workflow.signals (
  message_id text PRIMARY KEY,
  process_id uuid NOT NULL REFERENCES workflow.process_instances (process_id),
  signal_type text NOT NULL,
  body jsonb NOT NULL DEFAULT '{}'::jsonb,
  body_hash text NOT NULL,
  status text NOT NULL,
  received_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT signals_status_check CHECK (status IN ('ACCEPTED', 'APPLIED'))
);

CREATE INDEX IF NOT EXISTS idx_signals_process
  ON workflow.signals (process_id, signal_type, status);

CREATE TABLE IF NOT EXISTS workflow.workflow_events (
  event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  process_id uuid NOT NULL REFERENCES workflow.process_instances (process_id),
  step_instance_id uuid REFERENCES workflow.step_instances (step_instance_id),
  event_type text NOT NULL,
  details jsonb NOT NULL DEFAULT '{}'::jsonb,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_workflow_events_process
  ON workflow.workflow_events (process_id, occurred_at);

GRANT USAGE ON SCHEMA workflow TO course_publication, course_runtime, workflow_worker;

GRANT SELECT, INSERT, UPDATE, DELETE ON workflow.flow_versions TO course_publication;
GRANT SELECT, INSERT, UPDATE, DELETE ON
  workflow.process_instances,
  workflow.step_instances,
  workflow.jobs,
  workflow.task_attempts,
  workflow.signals,
  workflow.workflow_events
  TO course_publication;
GRANT SELECT, INSERT, UPDATE, DELETE ON course.settings TO course_publication;

GRANT SELECT ON
  workflow.flow_versions,
  workflow.process_instances,
  workflow.step_instances,
  workflow.jobs,
  workflow.task_attempts,
  workflow.signals,
  workflow.workflow_events,
  course.settings
  TO course_runtime;

REVOKE ALL ON ALL TABLES IN SCHEMA workflow FROM workflow_worker;
REVOKE ALL ON course.settings FROM workflow_worker;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA api FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime, workflow_worker;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;

ALTER ROLE workflow_worker SET search_path = workflow, api, pg_temp, public;

ALTER TABLE course.settings OWNER TO course_owner;
ALTER TABLE workflow.flow_versions OWNER TO course_owner;
ALTER TABLE workflow.process_instances OWNER TO course_owner;
ALTER TABLE workflow.step_instances OWNER TO course_owner;
ALTER TABLE workflow.jobs OWNER TO course_owner;
ALTER TABLE workflow.task_attempts OWNER TO course_owner;
ALTER TABLE workflow.signals OWNER TO course_owner;
ALTER TABLE workflow.workflow_events OWNER TO course_owner;
