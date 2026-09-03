# C4 Container Diagram — Week 1

```mermaid
C4Container
    title ModuleDev Week 1 — Container Diagram

    Person(client, "HTTP Client", "Calls published actions with JWT")

    Container_Boundary(system, "Course Solution") {
        Container(gateway, "gateway", "ASP.NET Core", "External entrypoint on :8080. Route whitelist, health proxy, no business logic.")
        Container(api, "api", "ASP.NET Core", "Generic action runtime: JWT, schema validation, idempotency, api.invoke in one transaction.")
        Container(cli, "cli", ".NET Console", "Migration apply, manifest validate/publish/list/activate/disable.")
        ContainerDb(postgres, "postgres", "PostgreSQL 16", "Authoritative state: action catalog, idempotency, operations, events, dispatches.")
    }

    Rel(client, gateway, "HTTPS/HTTP", "POST /api/{module}/{action}, OpenAPI, health")
    Rel(gateway, api, "HTTP", "Compose DNS http://api:8080")
    Rel(api, postgres, "Npgsql", "course_runtime: api.invoke, idempotency, catalog read")
    Rel(cli, postgres, "Npgsql", "course_publication: manifest publish; postgres: migrations")
```

## Responsibilities

| Container | Trust | Data access |
|---|---|---|
| gateway | Untrusted HTTP surface | None — pure reverse proxy |
| api | JWT verification, policy at HTTP boundary | `api.invoke` only; no direct DML on domain tables |
| cli | Publication credentials | `course_publication` + migration role |
| postgres | Source of truth | All durable state |

## Key flows

1. **Action execution:** Client → gateway → api → (validate JWT, schema) → BEGIN → api.invoke → target function → validate outcome/result → COMMIT or ROLLBACK.
2. **Publication:** Operator → cli → INSERT action_versions + action_state (immutable version, mutable enabled/is_default).
3. **Recovery:** Recreate gateway/api containers; PostgreSQL volume persists operations and idempotency records.
