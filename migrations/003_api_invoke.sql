CREATE OR REPLACE FUNCTION course.sha256_hex(input text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT encode(digest(input, 'sha256'), 'hex');
$$;

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
SET search_path = course, api, payment, operation, probe, pg_temp, public
AS $$
DECLARE
  v_def record;
  v_state record;
  v_required_scope text;
  v_context_scopes jsonb;
  v_sql text;
  v_result jsonb;
  v_outcome text;
  v_payload_hash text;
  v_status text;
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
REVOKE ALL ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime;
