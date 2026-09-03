CREATE TABLE IF NOT EXISTS course.migration_history (
  filename text PRIMARY KEY,
  checksum text NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS course.action_versions (
  module text NOT NULL,
  action text NOT NULL,
  version integer NOT NULL,
  http_method text NOT NULL,
  target_schema text NOT NULL,
  target_function text NOT NULL,
  request_schema jsonb NOT NULL,
  response_schema jsonb NOT NULL,
  outcomes jsonb NOT NULL,
  required_policy jsonb NOT NULL,
  idempotency_mode text NOT NULL,
  idempotency_scope text NOT NULL,
  timeout_ms integer NOT NULL,
  manifest_checksum text NOT NULL,
  published_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (module, action, version)
);

CREATE TABLE IF NOT EXISTS course.action_state (
  module text NOT NULL,
  action text NOT NULL,
  version integer NOT NULL,
  enabled boolean NOT NULL,
  is_default boolean NOT NULL,
  PRIMARY KEY (module, action, version),
  FOREIGN KEY (module, action, version) REFERENCES course.action_versions (module, action, version)
);

CREATE TABLE IF NOT EXISTS course.idempotency_records (
  scope_key text NOT NULL,
  idempotency_key text NOT NULL,
  payload_hash text NOT NULL,
  response_envelope jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (scope_key, idempotency_key)
);

CREATE TABLE IF NOT EXISTS course.operations (
  operation_id uuid PRIMARY KEY,
  request_id text NOT NULL,
  operation_kind text NOT NULL,
  amount numeric(18, 2) NOT NULL,
  currency text NOT NULL,
  status text NOT NULL,
  process_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS course.operation_events (
  event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  operation_id uuid NOT NULL REFERENCES course.operations (operation_id),
  event_type text NOT NULL,
  payload_hash text NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS course.action_dispatches (
  dispatch_id bigserial PRIMARY KEY,
  correlation_id uuid NOT NULL,
  request_id text,
  module text NOT NULL,
  action text NOT NULL,
  version integer NOT NULL,
  principal text NOT NULL,
  payload_hash text NOT NULL,
  status text NOT NULL,
  outcome text,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_action_dispatches_module_action
  ON course.action_dispatches (module, action);

CREATE INDEX IF NOT EXISTS idx_operations_request_id ON course.operations (request_id);

REVOKE ALL ON course.operations FROM course_runtime, course_publication;
REVOKE ALL ON course.operation_events FROM course_runtime, course_publication;
GRANT SELECT ON course.operations, course.operation_events TO course_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON course.action_versions, course.action_state TO course_publication;
GRANT SELECT ON course.action_versions, course.action_state TO course_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON course.idempotency_records TO course_runtime;
GRANT SELECT, INSERT ON course.action_dispatches TO course_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON course.migration_history TO course_publication;
GRANT SELECT ON course.migration_history TO course_runtime;
