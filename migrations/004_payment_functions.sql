CREATE OR REPLACE FUNCTION payment.request_handler(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = course, payment, pg_temp, public
AS $$
DECLARE
  v_operation_id uuid;
  v_request_id text;
  v_payload_hash text;
BEGIN
  v_request_id := p_context ->> 'requestId';
  v_payload_hash := course.sha256_hex(p_payload::text);
  v_operation_id := gen_random_uuid();

  INSERT INTO course.operations (
    operation_id, request_id, operation_kind, amount, currency, status
  ) VALUES (
    v_operation_id,
    v_request_id,
    p_payload ->> 'operationKind',
    (p_payload ->> 'amount')::numeric(18, 2),
    p_payload ->> 'currency',
    'CREATED'
  );

  INSERT INTO course.operation_events (operation_id, event_type, payload_hash)
  VALUES (v_operation_id, 'OPERATION_CREATED', v_payload_hash);

  RETURN jsonb_build_object(
    'outcome', 'CREATED',
    'result', jsonb_build_object(
      'operationId', v_operation_id::text,
      'requestId', v_request_id,
      'operationKind', p_payload ->> 'operationKind',
      'amount', p_payload ->> 'amount',
      'currency', p_payload ->> 'currency',
      'status', 'CREATED'
    )
  );
END;
$$;

ALTER FUNCTION payment.request_handler(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION payment.request_handler(jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION payment.request_handler(jsonb, jsonb) TO course_owner;
