# ADR 001: Single-host PostgreSQL queue and Docker CLI

Status: accepted for the first MVP.

## Context

A single trusted operator needs reproducible Git/Docker deployments and durable history on one Linux host. The original repository contains .NET Clean Architecture projects but no implementation. Reliability and understandable behavior take precedence over infrastructure breadth.

## Decision

Use .NET 10, PostgreSQL/EF migrations, a separate serial background worker, and React/TypeScript. PostgreSQL deployment rows form the durable queue; a session advisory lock excludes a second worker. Do not add Redis, RabbitMQ, or Kafka without a demonstrated requirement. Delivery means one attempt per queued row; manual redeploy is retry. Interrupted work fails explicitly rather than silently replaying non-idempotent route changes.

Use Docker/Git CLI through safe argument arrays and captured output. Use a dedicated nginx container/network with project-owned files validated before reload. Authenticate the single operator with a random bearer token. Encrypt environment values with AES-GCM using an external shared key. Poll durable log cursors instead of introducing streaming infrastructure.

## Alternatives

RabbitMQ would add message acknowledgement and retry semantics but would still require deployment idempotency and DB coordination. Kafka does not match the small job queue. Redis would introduce another state store without removing PostgreSQL requirements. A Docker SDK would provide typed API access but adds dependency and stream handling complexity. Cookie identity would require more identity/CSRF management for the initial single-operator scope. SSE would improve latency but polling is sufficient for the first vertical slice.

## Consequences

Few moving parts, clear durable history, inspectable commands, and easy local debugging. One deployment executes at a time. Stronger fencing, route/database crash reconciliation, independent health monitoring, reliable container log cursors, retention, identity, sandboxed builds, and production serving remain required before wider use. See SECURITY.md and ARCHITECTURE.md.
