CREATE OR REPLACE FUNCTION operation.get_handler(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = course, operation, pg_temp, public
AS $$
DECLARE
  v_row course.operations%ROWTYPE;
BEGIN
  SELECT * INTO v_row
  FROM course.operations
  WHERE operation_id = (p_payload ->> 'operationId')::uuid;

  IF NOT FOUND THEN
    RETURN jsonb_build_object(
      'status', 'error',
      'code', 'operation.not_found',
      'message', 'operation was not found'
    );
  END IF;

  RETURN jsonb_build_object(
    'outcome', 'FOUND',
    'result', jsonb_build_object(
      'operationId', v_row.operation_id::text,
      'requestId', v_row.request_id,
      'operationKind', v_row.operation_kind,
      'amount', to_char(v_row.amount, 'FM9999999999999999.00'),
      'currency', v_row.currency,
      'status', v_row.status
    )
  );
END;
$$;

ALTER FUNCTION operation.get_handler(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION operation.get_handler(jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION operation.get_handler(jsonb, jsonb) TO course_owner;
