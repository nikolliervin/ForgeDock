# Project services and automation verification

Verified on the local Linux/Docker development host on 2026-10-01.

## Features and guides

| Feature | Dashboard location | Setup and operating guide |
| --- | --- | --- |
| Deployment notifications | Settings → Deployment notifications | [NOTIFICATIONS.md](NOTIFICATIONS.md), `/docs/notifications` |
| Managed PostgreSQL / Redis | Databases | [DATABASE_SERVICES.md](DATABASE_SERVICES.md), `/docs/database-services` |
| Backups and restore | Backups | [BACKUPS.md](BACKUPS.md), `/docs/backups` |
| Pull request previews | Previews | [PREVIEW_ENVIRONMENTS.md](PREVIEW_ENVIRONMENTS.md), `/docs/preview-environments` |
| Resource limits and alerts | Settings → Resource controls | [RESOURCE_CONTROLS.md](RESOURCE_CONTROLS.md), `/docs/resource-controls` |
| Stack defaults and starter ZIPs | New project → Project template | [PROJECT_TEMPLATES.md](PROJECT_TEMPLATES.md), `/docs/project-templates` |

## Checks performed

- Backend and production frontend builds pass. EF reports no pending model changes; all five new migrations apply to an isolated test database and to the running development control database.
- The complete 159-test backend suite passed with Docker integration enabled. The subsequently added SMTP integration test also passed, for 160 backend checks overall. Real PostgreSQL and Redis containers preserved data after recreation, restored backup data after modifications, and preserved restored data after another recreation. The existing Compose persistence/rollback test passed with routed-service resource limits enabled.
- Resource integration tests inspected actual Docker memory/CPU/restart policy settings, then verified a crashed owned application generated a durable alert and notification once, with cooldown and disabled-monitoring behavior.
- A local fake SMTP server verified success and failure emails, exact deployment links, persistence across sender instances, recipient failure retry, and suppression of duplicate completed deliveries. No messages were sent to an external email, Slack, or Discord account during verification.
- Isolated API/PostgreSQL integration checks cover authenticated settings, write-only notification secrets, destination validation, concurrent database creation, connection variables, backup schedules, restore confirmation/project boundaries, signed PR events, concurrent delivery deduplication, pinned revisions, production-secret isolation, independent preview databases, close cancellation, stale events, resource settings, embedded template archives, and atomic project/database creation.
- Twenty-three distinct browser checks passed across docs, navigation, console, environments, metrics, domains, webhook settings, templates, and the new services workflow. All 19 documentation articles support direct navigation. Settings workflows also passed after the two-column layout change.
- Desktop settings were visually inspected at 1600px, with independent left and right columns. At 390px, cards stack without horizontal overflow.
- The running API/worker were updated, the preview endpoint's stale-process 404 was resolved, and the user's `cozyc PR #2` preview reached Running through a real GitHub pull-request event.

## Reproduce

```bash
dotnet build ForgeDock.sln
npm run build --prefix web
bash scripts/with-env.sh env FORGEDOCK_DOCKER_TESTS=1 dotnet test tests/ForgeDock.Tests --no-restore
bash scripts/with-env.sh python3 tests/integration/github-webhooks.py
bash scripts/with-env.sh dotnet ef migrations has-pending-model-changes --no-build \
  --project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api
./web/node_modules/.bin/playwright test --config web/playwright.config.ts \
  web/e2e/docs.spec.ts web/e2e/navigation.spec.ts web/e2e/console.spec.ts \
  web/e2e/environment.spec.ts web/e2e/metrics.spec.ts web/e2e/domains.spec.ts \
  web/e2e/webhooks.spec.ts web/e2e/build-settings.spec.ts \
  web/e2e/templates.spec.ts web/e2e/platform-features.spec.ts
```

Docker checks require local infrastructure and use their own temporary resources. API integration checks create and drop their own control database and never run the deployment worker. Browser checks require the Vite dashboard at port 5173 and mock management API responses.

## Practical limits

Slack/Discord live delivery and public wildcard-preview certificate issuance require the operator's own channel/domain setup and were not tested against external accounts. SMTP requires an existing mail service; no paid provider is mandatory. Backup storage is local and requires an off-host copy for host-loss protection. Restores interrupt apps, and interrupted restores require operator inspection. Previews deploy same-repository non-draft PRs only, do not inherit production secrets, and retain deleted-preview volumes/artifacts for separate cleanup. Resource monitoring samples every 30 seconds; runtime limits do not constrain builds. The single trusted operator and host-isolation limitations still apply.
