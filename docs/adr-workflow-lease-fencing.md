# Workflow lease and fencing

## Context

Week 2 introduces concurrent workers claiming the same job queue. At-least-once execution requires lease versioning and stale completion rejection.

## Decision

- Each job has monotonic `lease_version`; finish accepts only matching `(jobId, owner, leaseVersion)`.
- Reclaim creates new `attemptId`, marks prior attempt `STALE` without consuming retry budget.
- Subject effect and `finish_job` commit in one transaction with `api.invoke`.

## Consequences

- Crash after claim → reclaim after lease expiry; stale finish returns `workflow.lease_stale`.
- Crash after action before finish → rollback leaves zero partial effects; retry creates one effect.
