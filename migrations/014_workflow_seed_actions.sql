INSERT INTO course.action_versions (
  module, action, version, http_method, target_schema, target_function,
  request_schema, response_schema, outcomes, required_policy,
  idempotency_mode, idempotency_scope, timeout_ms, manifest_checksum
) VALUES (
  'workflow', 'get', 1, 'POST', 'workflow', 'get_handler',
  '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["processId"],"properties":{"processId":{"type":"string","format":"uuid"}}}'::jsonb,
  '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["process","steps","jobs","attempts"],"properties":{"process":{"type":"object"},"steps":{"type":"array"},"jobs":{"type":"array"},"attempts":{"type":"array"}}}'::jsonb,
  '["FOUND"]'::jsonb,
  '["workflow:read"]'::jsonb,
  'none', 'none', 5000,
  'seed-workflow-get-v1'
) ON CONFLICT DO NOTHING;

INSERT INTO course.action_state (module, action, version, enabled, is_default)
VALUES ('workflow', 'get', 1, true, true)
ON CONFLICT DO NOTHING;
