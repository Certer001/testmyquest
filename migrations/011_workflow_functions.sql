CREATE OR REPLACE FUNCTION workflow.lease_seconds()
RETURNS integer
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_test_profile text := current_setting('course.test_profile', true);
  v_setting text;
BEGIN
  IF v_test_profile IN ('1', 'true', 'TRUE', 'on', 'ON') THEN
    RETURN 2;
  END IF;

  SELECT s.value
  INTO v_setting
  FROM course.settings s
  WHERE s.key = 'workflow.lease_seconds';

  IF v_setting IS NOT NULL AND v_setting ~ '^[0-9]+$' THEN
    RETURN v_setting::integer;
  END IF;

  RETURN 30;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.append_event(
  p_process_id uuid,
  p_step_instance_id uuid,
  p_event_type text,
  p_details jsonb DEFAULT '{}'::jsonb
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_event_id uuid := gen_random_uuid();
BEGIN
  INSERT INTO workflow.workflow_events (
    event_id, process_id, step_instance_id, event_type, details
  ) VALUES (
    v_event_id, p_process_id, p_step_instance_id, p_event_type, coalesce(p_details, '{}'::jsonb)
  );
  RETURN v_event_id;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.find_step_definition(
  p_map jsonb,
  p_step_key text
)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT step.value
  FROM jsonb_array_elements(p_map -> 'steps') AS step(value)
  WHERE step.value ->> 'key' = p_step_key
  LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION workflow.find_transition_target(
  p_map jsonb,
  p_from_step text,
  p_outcome text
)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT transition.value ->> 'to'
  FROM jsonb_array_elements(p_map -> 'transitions') AS transition(value)
  WHERE transition.value ->> 'from' = p_from_step
    AND transition.value ->> 'outcome' = p_outcome
  LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION workflow.normalize_step_type(p_type text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT upper(replace(p_type, '-', '_'));
$$;

CREATE OR REPLACE FUNCTION workflow.merge_process_data(
  p_process_data jsonb,
  p_result jsonb
)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT coalesce(p_process_data, '{}'::jsonb) || coalesce(p_result, '{}'::jsonb);
$$;

CREATE OR REPLACE FUNCTION workflow.failed_attempt_count(p_job_id uuid)
RETURNS integer
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = workflow, pg_temp, public
AS $$
  SELECT count(*)::integer
  FROM workflow.task_attempts ta
  WHERE ta.job_id = p_job_id
    AND ta.status = 'FAILED';
$$;

CREATE OR REPLACE FUNCTION workflow.mark_running_attempt_stale(p_job_id uuid)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_attempt workflow.task_attempts%ROWTYPE;
BEGIN
  SELECT *
  INTO v_attempt
  FROM workflow.task_attempts ta
  WHERE ta.job_id = p_job_id
    AND ta.status = 'RUNNING'
  ORDER BY ta.attempt_number DESC
  LIMIT 1
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN;
  END IF;

  UPDATE workflow.task_attempts
  SET status = 'STALE',
      finished_at = now()
  WHERE attempt_id = v_attempt.attempt_id;

  UPDATE workflow.jobs
  SET attempt_count = attempt_count + 1,
      lease_version = lease_version + 1
  WHERE job_id = p_job_id;

  PERFORM workflow.append_event(
    (SELECT process_id FROM workflow.jobs WHERE job_id = p_job_id),
    (SELECT step_instance_id FROM workflow.jobs WHERE job_id = p_job_id),
    'LeaseExpired',
    jsonb_build_object('attemptId', v_attempt.attempt_id::text)
  );
END;
$$;

CREATE OR REPLACE FUNCTION workflow.enter_step(
  p_process_id uuid,
  p_step_key text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_process workflow.process_instances%ROWTYPE;
  v_map jsonb;
  v_step jsonb;
  v_step_type text;
  v_step_instance_id uuid;
  v_signal record;
  v_target text;
  v_outcome text;
BEGIN
  SELECT *
  INTO v_process
  FROM workflow.process_instances pi
  WHERE pi.process_id = p_process_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RAISE EXCEPTION 'process not found';
  END IF;

  SELECT fv.map_definition
  INTO v_map
  FROM workflow.flow_versions fv
  WHERE fv.flow_name = v_process.flow_name
    AND fv.flow_version = v_process.flow_version;

  v_step := workflow.find_step_definition(v_map, p_step_key);
  IF v_step IS NULL THEN
    RAISE EXCEPTION 'step definition not found';
  END IF;

  v_step_type := workflow.normalize_step_type(v_step ->> 'type');

  IF v_step_type = 'END' THEN
    v_outcome := v_step ->> 'outcome';
    v_step_instance_id := gen_random_uuid();
    INSERT INTO workflow.step_instances (
      step_instance_id, process_id, step_key, step_type, state, outcome,
      completed_at, end_outcome
    ) VALUES (
      v_step_instance_id, p_process_id, p_step_key, 'END', 'COMPLETED', v_outcome,
      now(), v_outcome
    );

    UPDATE workflow.process_instances
    SET state = 'COMPLETED',
        current_step_key = p_step_key,
        updated_at = now()
    WHERE process_id = p_process_id;

    PERFORM workflow.append_event(
      p_process_id, v_step_instance_id, 'ProcessCompleted',
      jsonb_build_object('outcome', v_outcome)
    );
    RETURN;
  END IF;

  IF v_step_type = 'AUTOMATIC' THEN
    v_step_instance_id := gen_random_uuid();
    INSERT INTO workflow.step_instances (
      step_instance_id, process_id, step_key, step_type, state, task_definition
    ) VALUES (
      v_step_instance_id, p_process_id, p_step_key, 'AUTOMATIC', 'RUNNING', v_step -> 'task'
    );

    INSERT INTO workflow.jobs (
      process_id, step_instance_id, execution_id, state,
      max_attempts, retry_delays_ms
    ) VALUES (
      p_process_id,
      v_step_instance_id,
      gen_random_uuid(),
      'READY',
      coalesce((v_step -> 'task' -> 'retry' ->> 'max_attempts')::integer, 1),
      coalesce(v_step -> 'task' -> 'retry' -> 'delays_ms', '[]'::jsonb)
    );

    UPDATE workflow.process_instances
    SET state = 'RUNNING',
        current_step_key = p_step_key,
        updated_at = now()
    WHERE process_id = p_process_id;

    PERFORM workflow.append_event(
      p_process_id, v_step_instance_id, 'StepEntered',
      jsonb_build_object('stepKey', p_step_key, 'stepType', 'AUTOMATIC')
    );
    RETURN;
  END IF;

  IF v_step_type = 'WAIT_SIGNAL' THEN
    v_step_instance_id := gen_random_uuid();
    INSERT INTO workflow.step_instances (
      step_instance_id, process_id, step_key, step_type, state,
      signal_type, wait_outcome
    ) VALUES (
      v_step_instance_id, p_process_id, p_step_key, 'WAIT_SIGNAL', 'WAITING',
      v_step ->> 'signal_type', v_step ->> 'outcome'
    );

    SELECT s.message_id, s.body_hash
    INTO v_signal
    FROM workflow.signals s
    WHERE s.process_id = p_process_id
      AND s.signal_type = v_step ->> 'signal_type'
      AND s.status = 'ACCEPTED'
    ORDER BY s.received_at, s.message_id
    LIMIT 1
    FOR UPDATE;

    IF FOUND THEN
      UPDATE workflow.signals
      SET status = 'APPLIED'
      WHERE message_id = v_signal.message_id;

      UPDATE workflow.step_instances
      SET state = 'COMPLETED',
          outcome = v_step ->> 'outcome',
          completed_at = now()
      WHERE step_instance_id = v_step_instance_id;

      PERFORM workflow.append_event(
        p_process_id, v_step_instance_id, 'SignalApplied',
        jsonb_build_object('messageId', v_signal.message_id)
      );

      v_target := workflow.find_transition_target(v_map, p_step_key, v_step ->> 'outcome');
      IF v_target IS NULL THEN
        RAISE EXCEPTION 'transition not found for signal outcome';
      END IF;

      PERFORM workflow.enter_step(p_process_id, v_target);
      RETURN;
    END IF;

    UPDATE workflow.process_instances
    SET state = 'WAITING_SIGNAL',
        current_step_key = p_step_key,
        updated_at = now()
    WHERE process_id = p_process_id;

    PERFORM workflow.append_event(
      p_process_id, v_step_instance_id, 'StepEntered',
      jsonb_build_object('stepKey', p_step_key, 'stepType', 'WAIT_SIGNAL')
    );
    RETURN;
  END IF;

  IF v_step_type = 'MANUAL' THEN
    v_step_instance_id := gen_random_uuid();
    INSERT INTO workflow.step_instances (
      step_instance_id, process_id, step_key, step_type, state, allowed_outcomes
    ) VALUES (
      v_step_instance_id, p_process_id, p_step_key, 'MANUAL', 'WAITING',
      v_step -> 'allowed_outcomes'
    );

    UPDATE workflow.process_instances
    SET state = 'WAITING_MANUAL',
        current_step_key = p_step_key,
        updated_at = now()
    WHERE process_id = p_process_id;

    PERFORM workflow.append_event(
      p_process_id, v_step_instance_id, 'StepEntered',
      jsonb_build_object('stepKey', p_step_key, 'stepType', 'MANUAL')
    );
    RETURN;
  END IF;

  RAISE EXCEPTION 'unsupported step type %', v_step_type;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.advance_process(
  p_process_id uuid,
  p_from_step_key text,
  p_outcome text,
  p_result jsonb DEFAULT '{}'::jsonb
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_process workflow.process_instances%ROWTYPE;
  v_map jsonb;
  v_target text;
BEGIN
  SELECT *
  INTO v_process
  FROM workflow.process_instances pi
  WHERE pi.process_id = p_process_id
  FOR UPDATE;

  SELECT fv.map_definition
  INTO v_map
  FROM workflow.flow_versions fv
  WHERE fv.flow_name = v_process.flow_name
    AND fv.flow_version = v_process.flow_version;

  UPDATE workflow.process_instances
  SET process_data = workflow.merge_process_data(process_data, p_result),
      updated_at = now()
  WHERE process_id = p_process_id;

  v_target := workflow.find_transition_target(v_map, p_from_step_key, p_outcome);
  IF v_target IS NULL THEN
    RAISE EXCEPTION 'unknown outcome transition';
  END IF;

  PERFORM workflow.enter_step(p_process_id, v_target);
END;
$$;

CREATE OR REPLACE FUNCTION workflow.build_action_contract(
  p_task jsonb,
  p_action jsonb
)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT jsonb_build_object(
    'module', p_task ->> 'module',
    'action', p_task ->> 'action',
    'version', (p_task ->> 'action_version')::integer,
    'requestSchema', p_action -> 'request_schema',
    'responseSchema', p_action -> 'response_schema',
    'outcomes', p_action -> 'outcomes',
    'requiredPolicy', p_action -> 'required_policy',
    'inputMapping', coalesce(p_task -> 'input_mapping', '{}'::jsonb),
    'inputConstants', coalesce(p_task -> 'input_constants', '{}'::jsonb),
    'timeoutMs', (p_task ->> 'timeout_ms')::integer,
    'retry', coalesce(p_task -> 'retry', jsonb_build_object('max_attempts', 1, 'delays_ms', '[]'::jsonb))
  );
$$;

CREATE OR REPLACE FUNCTION workflow.claim_jobs(p_owner text, p_limit integer)
RETURNS SETOF json
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_job workflow.jobs%ROWTYPE;
  v_process workflow.process_instances%ROWTYPE;
  v_step workflow.step_instances%ROWTYPE;
  v_action jsonb;
  v_attempt_id uuid;
  v_attempt_number integer;
  v_lease_seconds integer;
  v_claim jsonb;
  v_task jsonb;
BEGIN
  IF p_limit IS NULL OR p_limit < 1 THEN
    RETURN;
  END IF;

  v_lease_seconds := workflow.lease_seconds();

  FOR v_job IN
    SELECT j.*
    FROM workflow.jobs j
    WHERE (
      j.state = 'READY'
      OR (j.state = 'RETRY_WAIT' AND coalesce(j.next_attempt_at, now()) <= now())
      OR (j.state = 'LEASED' AND coalesce(j.lease_until, now()) <= now())
    )
    ORDER BY j.next_attempt_at NULLS FIRST, j.job_id
    FOR UPDATE OF j SKIP LOCKED
    LIMIT p_limit
  LOOP
    IF v_job.state = 'LEASED' AND coalesce(v_job.lease_until, now()) <= now() THEN
      PERFORM workflow.mark_running_attempt_stale(v_job.job_id);
      SELECT * INTO v_job FROM workflow.jobs WHERE job_id = v_job.job_id;
    END IF;

    IF v_job.state = 'RETRY_WAIT' AND coalesce(v_job.next_attempt_at, now()) <= now() THEN
      UPDATE workflow.jobs
      SET state = 'READY',
          next_attempt_at = NULL,
          lease_owner = NULL,
          lease_until = NULL
      WHERE job_id = v_job.job_id;
      SELECT * INTO v_job FROM workflow.jobs WHERE job_id = v_job.job_id;
    END IF;

    IF v_job.state <> 'READY' THEN
      CONTINUE;
    END IF;

    v_attempt_number := v_job.attempt_count + 1;
    v_attempt_id := gen_random_uuid();

    UPDATE workflow.jobs
    SET state = 'LEASED',
        lease_owner = p_owner,
        lease_version = v_job.lease_version + 1,
        lease_until = now() + make_interval(secs => v_lease_seconds),
        attempt_count = v_attempt_number,
        next_attempt_at = NULL
    WHERE job_id = v_job.job_id
    RETURNING * INTO v_job;

    INSERT INTO workflow.task_attempts (
      attempt_id, job_id, execution_id, lease_version, attempt_number, status
    ) VALUES (
      v_attempt_id, v_job.job_id, v_job.execution_id, v_job.lease_version, v_attempt_number, 'RUNNING'
    );

    SELECT * INTO v_step
    FROM workflow.step_instances si
    WHERE si.step_instance_id = v_job.step_instance_id;

    SELECT * INTO v_process
    FROM workflow.process_instances pi
    WHERE pi.process_id = v_job.process_id;

    v_task := coalesce(v_step.task_definition, '{}'::jsonb);

    SELECT jsonb_build_object(
      'request_schema', av.request_schema,
      'response_schema', av.response_schema,
      'outcomes', av.outcomes,
      'required_policy', av.required_policy
    )
    INTO v_action
    FROM course.action_versions av
    JOIN course.action_state st
      ON st.module = av.module
     AND st.action = av.action
     AND st.version = av.version
    WHERE av.module = v_task ->> 'module'
      AND av.action = v_task ->> 'action'
      AND av.version = (v_task ->> 'action_version')::integer
      AND st.enabled;

    IF v_action IS NULL THEN
      UPDATE workflow.task_attempts
      SET status = 'FAILED',
          error_code = 'action.not_found',
          error_message = 'pinned action is unavailable',
          finished_at = now()
      WHERE attempt_id = v_attempt_id;

      UPDATE workflow.jobs
      SET state = 'DEAD'
      WHERE job_id = v_job.job_id;

      UPDATE workflow.step_instances
      SET state = 'FAILED',
          completed_at = now()
      WHERE step_instance_id = v_job.step_instance_id;

      UPDATE workflow.process_instances
      SET state = 'FAILED',
          updated_at = now()
      WHERE process_id = v_job.process_id;

      PERFORM workflow.append_event(v_job.process_id, v_job.step_instance_id, 'TaskFailed', '{}'::jsonb);
      CONTINUE;
    END IF;

    v_claim := jsonb_build_object(
      'jobId', v_job.job_id,
      'processId', v_job.process_id,
      'executionId', v_job.execution_id,
      'attemptId', v_attempt_id,
      'leaseVersion', v_job.lease_version,
      'processData', v_process.process_data,
      'action', workflow.build_action_contract(v_task, v_action)
    );

    PERFORM workflow.append_event(
      v_job.process_id,
      v_job.step_instance_id,
      'JobClaimed',
      jsonb_build_object(
        'jobId', v_job.job_id::text,
        'owner', p_owner,
        'leaseVersion', v_job.lease_version,
        'attemptId', v_attempt_id::text
      )
    );

    RETURN NEXT v_claim::json;
  END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.finish_job(
  p_job_id uuid,
  p_owner text,
  p_lease_version bigint,
  p_outcome text,
  p_result jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_job workflow.jobs%ROWTYPE;
  v_step workflow.step_instances%ROWTYPE;
  v_attempt workflow.task_attempts%ROWTYPE;
  v_target text;
  v_map jsonb;
  v_process workflow.process_instances%ROWTYPE;
BEGIN
  SELECT *
  INTO v_job
  FROM workflow.jobs j
  WHERE j.job_id = p_job_id
  FOR UPDATE;

  IF NOT FOUND
     OR v_job.state <> 'LEASED'
     OR v_job.lease_owner IS DISTINCT FROM p_owner
     OR v_job.lease_version <> p_lease_version THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'job lease is stale or no longer owned by caller'
    );
  END IF;

  SELECT *
  INTO v_attempt
  FROM workflow.task_attempts ta
  WHERE ta.job_id = p_job_id
    AND ta.lease_version = p_lease_version
    AND ta.status = 'RUNNING'
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'running attempt was not found for lease'
    );
  END IF;

  SELECT * INTO v_step FROM workflow.step_instances WHERE step_instance_id = v_job.step_instance_id;
  SELECT * INTO v_process FROM workflow.process_instances WHERE process_id = v_job.process_id;

  SELECT fv.map_definition
  INTO v_map
  FROM workflow.flow_versions fv
  WHERE fv.flow_name = v_process.flow_name
    AND fv.flow_version = v_process.flow_version;

  v_target := workflow.find_transition_target(v_map, v_step.step_key, p_outcome);
  IF v_target IS NULL THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'workflow.unknown_outcome',
      'message', 'outcome is not mapped in pinned flow version'
    );
  END IF;

  UPDATE workflow.task_attempts
  SET status = 'SUCCEEDED',
      outcome = p_outcome,
      finished_at = now()
  WHERE attempt_id = v_attempt.attempt_id;

  UPDATE workflow.jobs
  SET state = 'SUCCEEDED',
      lease_owner = NULL,
      lease_until = NULL
  WHERE job_id = p_job_id;

  UPDATE workflow.step_instances
  SET state = 'COMPLETED',
      outcome = p_outcome,
      completed_at = now()
  WHERE step_instance_id = v_job.step_instance_id;

  PERFORM workflow.append_event(
    v_job.process_id,
    v_job.step_instance_id,
    'TaskSucceeded',
    jsonb_build_object('outcome', p_outcome)
  );

  PERFORM workflow.advance_process(v_job.process_id, v_step.step_key, p_outcome, p_result);

  RETURN jsonb_build_object('status', 'ok');
END;
$$;

CREATE OR REPLACE FUNCTION workflow.fail_job(
  p_job_id uuid,
  p_owner text,
  p_lease_version bigint,
  p_error_code text,
  p_retryable boolean,
  p_error_message text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_job workflow.jobs%ROWTYPE;
  v_attempt workflow.task_attempts%ROWTYPE;
  v_failed_count integer;
  v_delay_ms integer;
BEGIN
  SELECT *
  INTO v_job
  FROM workflow.jobs j
  WHERE j.job_id = p_job_id
  FOR UPDATE;

  IF NOT FOUND
     OR v_job.state <> 'LEASED'
     OR v_job.lease_owner IS DISTINCT FROM p_owner
     OR v_job.lease_version <> p_lease_version THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'job lease is stale or no longer owned by caller'
    );
  END IF;

  SELECT *
  INTO v_attempt
  FROM workflow.task_attempts ta
  WHERE ta.job_id = p_job_id
    AND ta.lease_version = p_lease_version
    AND ta.status = 'RUNNING'
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'running attempt was not found for lease'
    );
  END IF;

  UPDATE workflow.task_attempts
  SET status = 'FAILED',
      error_code = p_error_code,
      error_message = p_error_message,
      finished_at = now()
  WHERE attempt_id = v_attempt.attempt_id;

  v_failed_count := workflow.failed_attempt_count(p_job_id);

  IF p_retryable AND v_failed_count < v_job.max_attempts THEN
    v_delay_ms := coalesce((v_job.retry_delays_ms ->> (v_failed_count - 1))::integer, 0);

    UPDATE workflow.jobs
    SET state = 'RETRY_WAIT',
        lease_owner = NULL,
        lease_until = NULL,
        next_attempt_at = now() + make_interval(secs => (v_delay_ms::numeric / 1000.0))
    WHERE job_id = p_job_id;

    PERFORM workflow.append_event(
      v_job.process_id,
      v_job.step_instance_id,
      'TaskRetryScheduled',
      jsonb_build_object('attemptNumber', v_attempt.attempt_number, 'delayMs', v_delay_ms)
    );

    RETURN jsonb_build_object('status', 'ok', 'result', jsonb_build_object('state', 'RETRY_WAIT'));
  END IF;

  UPDATE workflow.jobs
  SET state = 'DEAD',
      lease_owner = NULL,
      lease_until = NULL,
      next_attempt_at = NULL
  WHERE job_id = p_job_id;

  UPDATE workflow.step_instances
  SET state = 'FAILED',
      completed_at = now()
  WHERE step_instance_id = v_job.step_instance_id;

  UPDATE workflow.process_instances
  SET state = 'FAILED',
      updated_at = now()
  WHERE process_id = v_job.process_id;

  PERFORM workflow.append_event(
    v_job.process_id,
    v_job.step_instance_id,
    'TaskFailed',
    jsonb_build_object('errorCode', p_error_code)
  );

  RETURN jsonb_build_object('status', 'ok', 'result', jsonb_build_object('state', 'DEAD'));
END;
$$;

ALTER FUNCTION workflow.lease_seconds() OWNER TO course_owner;
ALTER FUNCTION workflow.append_event(uuid, uuid, text, jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.find_step_definition(jsonb, text) OWNER TO course_owner;
ALTER FUNCTION workflow.find_transition_target(jsonb, text, text) OWNER TO course_owner;
ALTER FUNCTION workflow.normalize_step_type(text) OWNER TO course_owner;
ALTER FUNCTION workflow.merge_process_data(jsonb, jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.failed_attempt_count(uuid) OWNER TO course_owner;
ALTER FUNCTION workflow.mark_running_attempt_stale(uuid) OWNER TO course_owner;
ALTER FUNCTION workflow.enter_step(uuid, text) OWNER TO course_owner;
ALTER FUNCTION workflow.advance_process(uuid, text, text, jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.build_action_contract(jsonb, jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.claim_jobs(text, integer) OWNER TO course_owner;
ALTER FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean, text) OWNER TO course_owner;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean, text) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.enter_step(uuid, text) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.find_transition_target(jsonb, text, text) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.append_event(uuid, uuid, text, jsonb) TO course_publication;
