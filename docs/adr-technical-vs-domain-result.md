# ADR: Technical vs Domain Result

## Status

Accepted

## Context

Action execution produces multiple layers of outcome:

- **Domain result:** Business data from PostgreSQL target function (e.g. operation record, probe canary marker).
- **Technical envelope:** HTTP status, error codes, correlationId, rollback semantics.

The contract requires that invalid domain outcomes roll back all side effects, while controlled domain errors (e.g. `operation.not_found`) return predictable error envelopes without 500.

## Decision

### Layer separation

```
Target function  →  { outcome, result } or { status: "error", code, message }
       ↓
api.invoke       →  normalizes to { status, outcome?, result?, code?, message? }
       ↓
HTTP executor    →  validates outcome ∈ manifest.outcomes, result ∈ response_schema
                   →  COMMIT or full ROLLBACK
       ↓
HTTP response    →  { status: "ok", outcome, result, meta } | { status: "error", code, ... }
```

### Rollback matrix

| Condition | HTTP | Transaction |
|---|---|---|
| Request schema invalid | 422 `payload.invalid` | No invoke |
| Policy denied | 403 `access.denied` | No invoke (HTTP) / no target call (DB) |
| Target returns `status: "error"` | Mapped code (e.g. 404 for `operation.not_found`) | **ROLLBACK** — no durable effect |
| Unknown outcome | 500 `action.contract_violation` | **ROLLBACK** |
| Result fails response schema | 500 `action.contract_violation` | **ROLLBACK** |
| Success | 200 + declared outcome | **COMMIT** |

### Idempotency interaction

Successful envelopes are stored in `idempotency_records.response_envelope` only after commit. Failed requests do not cache success. Replay returns the original committed result without re-invoking target.

Concurrent identical keys rely on PostgreSQL row locks (`INSERT ON CONFLICT` + `SELECT FOR UPDATE` + advisory lock), not in-process mutexes.

### Dispatch audit

`action_dispatches` records correlationId, payload_hash (SHA-256 hex), status OK/ERROR, and outcome. Rows are inserted inside `api.invoke` within the same transaction — rolled back together with domain writes on contract violation.

## Consequences

- Canary/probe tables stay empty after rollback scenarios (verified by autocheck).
- `operation.not_found` is a domain error surfaced as 404, not an unhandled exception.
- Clients always receive contract-shaped JSON; 500 responses never include SQL, stack traces, or connection strings.
