# Application metrics

Open a project’s **Metrics** tab for CPU, memory, transfer rates, resource history, and service details. Select a 1-hour, 6-hour, or 24-hour range. Compose projects can filter by service. Hover over charts to inspect samples.

A separate `MetricsCollector` worker service samples running, managed application containers roughly every 30 seconds, even while deployments build. Transaction-scoped PostgreSQL advisory locking prevents concurrent collectors from writing duplicate batches. Docker commands have a 20-second collection deadline; failures retry next interval without terminating deployments. Infrastructure containers are excluded. Single-application samples require the active deployment ownership label; Compose samples require the owned project/stack labels.

Samples are persisted in PostgreSQL. The authenticated `GET /api/projects/{id}/metrics?range=1h&service=web` endpoint returns chart points, latest service samples, available services, and freshness. Invalid ranges return 400 and unknown projects return 404. Raw history is retained for 24 hours, with cleanup every 15 minutes while the collector runs. Project deletion cascades metric rows.

Charts sum service CPU/memory at each collection timestamp before averaging within 30-second, 2-minute, or 5-minute buckets. CPU can exceed 100% because 100% represents one core. Memory follows Docker’s Linux cache-excluded measurement and is rounded by Docker’s CLI. Memory limits reflect Docker’s reported limit, which may be host memory for unconstrained Compose services. Disk and network counters are cumulative since container startup, and processes include kernel threads. Network rates use elapsed time per container and are unavailable across restarts, counter resets, first samples, or gaps longer than 90 seconds. Charts show missing intervals as gaps.

The UI polls every 10 seconds and marks active-deployment samples stale after 75 seconds or when the project is stopped. The service table follows the active deployment; historical charts include previous versions in the requested range. Uptime comes from the container start time. No new samples are stored for stopped containers.

Run `./scripts/start-local.sh` to migrate and start everything, or run `make migrate` and restart the API/worker after updating. Allow two collections for traffic rates. HTTP request counts, latency, alerts, host-wide metrics, and exporter integrations are not included.

## Environment entry

The Environment tab supports individual variables and bulk `.env` imports. The bulk endpoint is `PUT /api/projects/{id}/environment` with `{ "content": "NAME=value", "overwrite": false }`. It validates the full batch before one transactional save, encrypts values with the existing project secret key, and returns names/counts only. Up to 100 entries, 256 KiB total, and 16 KiB per single-line value are supported. Comments, export prefixes, and single/double quotes are supported; multiline values and duplicate names are rejected. Variable expansion is not performed. Existing names require explicit replacement; unmentioned variables are preserved. Error messages use line numbers without echoing values. Changes apply to future deployments.

Reference: [Docker container stats](https://docs.docker.com/reference/cli/docker/container/stats/).
