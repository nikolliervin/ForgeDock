# Troubleshooting

- **Docker socket missing:** The initial Fedora environment had Docker installed but its service inactive. Start it using `sudo systemctl start docker`; confirm `docker version` reports both client and server.
- **`docker compose` unknown:** This environment lacks the Compose plugin. `make infra` uses Docker CLI and remains supported. `compose.yaml` also describes the stack, but the script is the verified setup in this environment.
- **Missing required API configuration:** Use `make init` and start processes with the provided Make targets. API requires the connection string, token, and base64 32-byte encryption key.
- **EF target/migrations assembly mismatch:** Migrations belong to Infrastructure. Use `--project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api`.
- **Initial migrations show missing history table before succeeding:** EF may log a failed query before creating `__EFMigrationsHistory` on a new database. Check the command exit status and final applied-migration result.
- **Existing named resources refused:** Startup requires `io.forgedock.managed=true`; it will not adopt unrelated resources with matching names.
- **SELinux denies route reads:** The startup script mounts the owned routes directory using `:ro,z`. Keep the runtime directory on a filesystem supporting SELinux labels.
- **Build or startup fails:** Open deployment logs; verify Dockerfile, configured port, and application binding to `0.0.0.0`. Deployments are never Running until their HTTP health endpoint succeeds.
- **Health timeout:** Proxy and application containers must share the configured network. Health path must be reachable inside Docker. A long build delays periodic health checks.
- **Worker refuses to start:** Only one worker may hold the PostgreSQL advisory lock. Stop the existing worker before starting another.
- **Interrupted deployment:** Restart marks it Failed. Inspect labeled containers and routes before redeploying; automatic crash-time reconciliation is incomplete.
- **Encrypted variables cannot be read:** API/worker must share the original `ForgeDock__SecretKey`. Restore the key from backup; rotating it without re-encryption does not preserve historical values.

- **Lifecycle browser checks act before restart completes:** Wait for the new deployment ID returned by the restart request. An older Running row can remain visible briefly while polling catches up. The acceptance test uses the new row's ID, and dashboard polling preserves actionable errors until explicitly dismissed or another action starts.
