-- Tables created by postgres during migration are owned by postgres.
-- SECURITY DEFINER api.invoke runs as course_owner and must read the catalog.
ALTER TABLE course.migration_history OWNER TO course_owner;
ALTER TABLE course.action_versions OWNER TO course_owner;
ALTER TABLE course.action_state OWNER TO course_owner;
ALTER TABLE course.idempotency_records OWNER TO course_owner;
ALTER TABLE course.operations OWNER TO course_owner;
ALTER TABLE course.operation_events OWNER TO course_owner;
ALTER TABLE course.action_dispatches OWNER TO course_owner;

ALTER FUNCTION course.sha256_hex(text) OWNER TO course_owner;

GRANT SELECT ON course.action_versions, course.action_state TO course_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON course.action_versions, course.action_state TO course_publication;
