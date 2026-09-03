CREATE OR REPLACE FUNCTION workflow.get_handler(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = workflow, course, pg_temp, public
AS $$
DECLARE
  v_process_id uuid;
  v_process jsonb;
  v_steps jsonb;
  v_jobs jsonb;
  v_attempts jsonb;
BEGIN
  BEGIN
    v_process_id := (p_payload ->> 'processId')::uuid;
  EXCEPTION
    WHEN invalid_text_representation THEN
      RETURN jsonb_build_object(
        'status', 'error',
        'code', 'request.invalid',
        'message', 'processId must be a uuid'
      );
  END;

  SELECT jsonb_build_object(
    'processId', pi.process_id::text,
    'businessKey', pi.business_key,
    'flowName', pi.flow_name,
    'flowVersion', pi.flow_version,
    'state', pi.state,
    'currentStepKey', pi.current_step_key,
    'createdAt', pi.created_at,
    'updatedAt', pi.updated_at
  )
  INTO v_process
  FROM workflow.process_instances pi
  WHERE pi.process_id = v_process_id;

  IF v_process IS NULL THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'process.not_found',
      'message', 'process was not found'
    );
  END IF;

  SELECT coalesce(jsonb_agg(
    jsonb_build_object(
      'stepInstanceId', si.step_instance_id::text,
      'processId', si.process_id::text,
      'stepKey', si.step_key,
      'stepType', si.step_type,
      'state', si.state,
      'outcome', si.outcome,
      'enteredAt', si.entered_at,
      'completedAt', si.completed_at
    )
    ORDER BY si.entered_at, si.step_instance_id
  ), '[]'::jsonb)
  INTO v_steps
  FROM workflow.step_instances si
  WHERE si.process_id = v_process_id;

  SELECT coalesce(jsonb_agg(
    jsonb_build_object(
      'jobId', j.job_id::text,
      'processId', j.process_id::text,
      'stepInstanceId', j.step_instance_id::text,
      'executionId', j.execution_id::text,
      'state', j.state,
      'leaseOwner', j.lease_owner,
      'leaseVersion', j.lease_version,
      'leaseUntil', j.lease_until,
      'attemptCount', j.attempt_count,
      'nextAttemptAt', j.next_attempt_at
    )
    ORDER BY j.job_id
  ), '[]'::jsonb)
  INTO v_jobs
  FROM workflow.jobs j
  WHERE j.process_id = v_process_id;

  SELECT coalesce(jsonb_agg(
    jsonb_build_object(
      'attemptId', ta.attempt_id::text,
      'jobId', ta.job_id::text,
      'executionId', ta.execution_id::text,
      'leaseVersion', ta.lease_version,
      'attemptNumber', ta.attempt_number,
      'status', ta.status,
      'outcome', ta.outcome,
      'errorCode', ta.error_code,
      'startedAt', ta.started_at,
      'finishedAt', ta.finished_at
    )
    ORDER BY ta.attempt_number
  ), '[]'::jsonb)
  INTO v_attempts
  FROM workflow.task_attempts ta
  JOIN workflow.jobs j ON j.job_id = ta.job_id
  WHERE j.process_id = v_process_id;

  RETURN jsonb_build_object(
    'outcome', 'FOUND',
    'result', jsonb_build_object(
      'process', v_process,
      'steps', v_steps,
      'jobs', v_jobs,
      'attempts', v_attempts
    )
  );
END;
$$;

ALTER FUNCTION workflow.get_handler(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION workflow.get_handler(jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION workflow.get_handler(jsonb, jsonb) TO course_owner;

CREATE OR REPLACE FUNCTION api.invoke(
  p_module text,
  p_action text,
  p_version integer,
  p_context jsonb,
  p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = course, api, payment, operation, workflow, probe, pg_temp, public
AS $$
DECLARE
  v_def record;
  v_required_scope text;
  v_context_scopes jsonb;
  v_sql text;
  v_result jsonb;
  v_outcome text;
  v_payload_hash text;
BEGIN
  SELECT av.*, st.enabled, st.is_default
  INTO v_def
  FROM course.action_versions av
  JOIN course.action_state st
    ON st.module = av.module AND st.action = av.action AND st.version = av.version
  WHERE av.module = p_module AND av.action = p_action AND av.version = p_version;

  IF NOT FOUND OR NOT v_def.enabled THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'action.not_found',
      'message', 'action version is unknown or disabled'
    );
  END IF;

  v_context_scopes := coalesce(p_context -> 'scopes', '[]'::jsonb);
  IF jsonb_typeof(v_context_scopes) <> 'array' THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'access.denied',
      'message', 'invalid trusted context'
    );
  END IF;

  FOR v_required_scope IN
    SELECT jsonb_array_elements_text(v_def.required_policy)
  LOOP
    IF NOT EXISTS (
      SELECT 1
      FROM jsonb_array_elements_text(v_context_scopes) scope(value)
      WHERE scope.value = v_required_scope
    ) THEN
      RETURN jsonb_build_object(
        'status', 'error',
        'code', 'access.denied',
        'message', 'insufficient policy'
      );
    END IF;
  END LOOP;

  v_payload_hash := course.sha256_hex(p_payload::text);

  v_sql := format(
    'SELECT %I.%I($1::jsonb, $2::jsonb)',
    v_def.target_schema,
    v_def.target_function
  );

  BEGIN
    EXECUTE v_sql INTO v_result USING p_context, p_payload;
  EXCEPTION
    WHEN undefined_function THEN
      RETURN jsonb_build_object(
        'status', 'error',
        'code', 'action.not_found',
        'message', 'target is not registered'
      );
    WHEN OTHERS THEN
      RAISE LOG 'api.invoke target failure for %.% v%: %', p_module, p_action, p_version, SQLERRM;
      RETURN jsonb_build_object(
        'status', 'error',
        'code', 'internal.error',
        'message', 'target execution failed'
      );
  END;

  IF v_result IS NULL THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'internal.error',
      'message', 'target returned null'
    );
  END IF;

  IF coalesce(v_result ->> 'status', 'ok') = 'error' THEN
    INSERT INTO course.action_dispatches (
      correlation_id, request_id, module, action, version, principal,
      payload_hash, status, outcome
    ) VALUES (
      (p_context ->> 'correlationId')::uuid,
      p_context ->> 'requestId',
      p_module, p_action, p_version,
      p_context ->> 'principal',
      v_payload_hash,
      'ERROR',
      NULL
    );
    RETURN v_result;
  END IF;

  v_outcome := v_result ->> 'outcome';
  IF v_outcome IS NULL OR NOT EXISTS (
    SELECT 1 FROM jsonb_array_elements_text(v_def.outcomes) allowed(value)
    WHERE allowed.value = v_outcome
  ) THEN
    INSERT INTO course.action_dispatches (
      correlation_id, request_id, module, action, version, principal,
      payload_hash, status, outcome
    ) VALUES (
      (p_context ->> 'correlationId')::uuid,
      p_context ->> 'requestId',
      p_module, p_action, p_version,
      p_context ->> 'principal',
      v_payload_hash,
      'ERROR',
      v_outcome
    );
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'action.contract_violation',
      'message', 'unknown outcome'
    );
  END IF;

  INSERT INTO course.action_dispatches (
    correlation_id, request_id, module, action, version, principal,
    payload_hash, status, outcome
  ) VALUES (
    (p_context ->> 'correlationId')::uuid,
    p_context ->> 'requestId',
    p_module, p_action, p_version,
    p_context ->> 'principal',
    v_payload_hash,
    'OK',
    v_outcome
  );

  RETURN jsonb_build_object(
    'status', 'ok',
    'outcome', v_outcome,
    'result', coalesce(v_result -> 'result', '{}'::jsonb)
  );
END;
$$;

ALTER FUNCTION api.invoke(text, text, integer, jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA api FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime, workflow_worker;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean, text) TO workflow_worker;
