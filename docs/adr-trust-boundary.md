# ADR: Trust Boundary

## Status

Accepted

## Context

Week 1 requires a database-first action runtime where:

- Clients choose only published actions and payloads, never database targets.
- JWT claims define principal, consumer, and scopes.
- Policy is enforced at HTTP and again inside `api.invoke`.
- Runtime role must not mutate domain tables directly.

## Decision

### Two enforcement layers

1. **HTTP boundary (api service):** Verify JWT signature, claim types, issuer/audience/expiry. Build trusted `context` server-side. Reject insufficient scopes before opening a transaction. Validate request JSON Schema.

2. **Database boundary (`api.invoke`):** SECURITY DEFINER function owned by `course_owner` (NOLOGIN). Re-reads catalog, re-checks scopes from trusted context, calls only registered target functions via fixed `search_path`. Inserts dispatch audit rows.

### Context trust rules

| Field | Source |
|---|---|
| principal | JWT `sub` |
| consumer | JWT `consumer` |
| scopes | JWT `scope` (space-separated) |
| correlationId | Generated UUID per HTTP request |
| requestId | Idempotency-Key header when present |
| deadline | `now + timeout_ms` from manifest |

Payload fields cannot override any context or target field.

### PostgreSQL roles

| Role | Capabilities |
|---|---|
| `course_owner` | Owns schemas/functions; NOLOGIN |
| `course_publication` | Publish manifests, manage action_state |
| `course_runtime` | EXECUTE `api.invoke`, read catalog, idempotency R/W, dispatch INSERT |
| `postgres` | Migrations only (api startup, cli) |

`course_runtime` has SELECT on domain tables but REVOKE on INSERT/UPDATE/DELETE for `operations` and `operation_events`. Mutation probes against autocheck views fail.

### Gateway isolation

Gateway has no PostgreSQL connection and no JWT validation logic beyond forwarding headers. It cannot leak signing keys or tokens into logs.

## Consequences

- Policy bypass requires compromising JWT signing key or PostgreSQL superuser (out of threat model).
- All domain writes flow through registered functions inside the same transaction as HTTP executor rollback.
- New actions require manifest + target function registration, not C# code changes.
