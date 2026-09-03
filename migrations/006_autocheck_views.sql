CREATE OR REPLACE VIEW autocheck.contract_info AS
SELECT
  'course-1'::text AS contract_version,
  now() AS generated_at;

CREATE OR REPLACE VIEW autocheck.action_definitions AS
SELECT
  av.module,
  av.action,
  av.version,
  av.http_method,
  av.target_schema,
  av.target_function,
  av.outcomes,
  st.enabled,
  st.is_default
FROM course.action_versions av
JOIN course.action_state st
  ON st.module = av.module AND st.action = av.action AND st.version = av.version;

CREATE OR REPLACE VIEW autocheck.action_dispatches AS
SELECT
  correlation_id,
  request_id,
  module,
  action,
  version,
  principal,
  payload_hash,
  status,
  outcome,
  occurred_at
FROM course.action_dispatches;

CREATE OR REPLACE VIEW autocheck.operations AS
SELECT
  operation_id,
  request_id,
  operation_kind,
  amount,
  currency,
  status,
  process_id,
  created_at,
  updated_at
FROM course.operations;

CREATE OR REPLACE VIEW autocheck.operation_events AS
SELECT
  event_id,
  operation_id,
  event_type,
  payload_hash,
  occurred_at
FROM course.operation_events;

GRANT USAGE ON SCHEMA autocheck TO course_runtime, course_publication;
GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions,
  autocheck.action_dispatches, autocheck.operations, autocheck.operation_events
  TO course_runtime, course_publication;

REVOKE INSERT, UPDATE, DELETE ON autocheck.contract_info FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON autocheck.action_definitions FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON autocheck.action_dispatches FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON autocheck.operations FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON autocheck.operation_events FROM course_runtime;

-- Prevent runtime from mutating underlying tables through direct access
REVOKE INSERT, UPDATE, DELETE ON course.operations FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON course.operation_events FROM course_runtime;

CREATE OR REPLACE FUNCTION course.block_operation_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  RAISE EXCEPTION 'operation_events is insert-only for application roles';
END;
$$;

DROP TRIGGER IF EXISTS trg_block_operation_event_mutation ON course.operation_events;
CREATE TRIGGER trg_block_operation_event_mutation
  BEFORE UPDATE OR DELETE ON course.operation_events
  FOR EACH ROW
  EXECUTE FUNCTION course.block_operation_event_mutation();
