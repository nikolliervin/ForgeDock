# Security

## Threat model

This MVP is for one trusted operator on a trusted Linux host. The operator controls repositories and can effectively cause code execution on the deployment machine. It is not a hostile multi-tenant build service. Docker daemon access is effectively host-root access; never expose an unauthenticated daemon or socket over a network. API and infrastructure bind to loopback by default.

## Implemented protections

Management endpoints require a random bearer token of at least 32 characters. Comparison uses fixed-time SHA-256 digest comparison. The token stays in browser memory. No cookie authentication means management requests do not rely on ambient credentials. CORS is not enabled; development uses the same-origin Vite proxy.

Git URLs must use HTTPS and exclude embedded credentials, IP literals, loopback, nonstandard ports, query strings, and fragments. Branches and Dockerfile paths are validated. Processes receive argument arrays. Git redirects and alternate protocols are disabled. Dockerfile path components cannot be symbolic links. These checks do not fully prevent DNS rebinding or public names resolving to private addresses; worker network egress must be restricted for untrusted inputs.

Environment secrets are encrypted using AES-256-GCM with random nonces and authentication tags. The key is external to the database. Responses expose only variable names. Temporary application environment files use mode 0600 and are removed after container creation. Exact secret values are redacted from user-visible logs. Docker retains plaintext environment values in its container metadata; host/Docker administrators remain trusted. Short, encoded, or transformed secrets may evade redaction. Database history retains encrypted secrets for rollback.

Application containers have resource limits, drop Linux capabilities, and disallow privilege escalation. They do not expose Docker sockets or host ports and are not privileged. Ownership labels protect named infrastructure reuse and previous application shutdown.

## Limitations and production hardening

Shared tokens provide no user roles, audit attribution, lockout, or managed rotation. Local HTTP is intended for loopback only; use TLS before remote access. Images can run as root, builds can access network resources, and Docker is not a security boundary for hostile tenants. SSRF and DNS-rebinding defenses need egress isolation and resolved-address validation. No build sandbox, quotas across projects, log retention, source cleanup, or image cleanup exists. Route and database updates need crash reconciliation. Rate limiting and exhaustive API authorization tests remain necessary.

Use isolated worker hosts, rootless/container sandboxing where suitable, restricted network egress, external secret management, per-user identity/authorization, request rate limits, TLS, backups, image policy, pinned production image digests, telemetry and audit logs before exposing a production service. Back up `ForgeDock__SecretKey`; replacing it makes historical variables undecryptable.
