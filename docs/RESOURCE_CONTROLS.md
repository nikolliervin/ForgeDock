# Resource controls and alerts

Open **Project → Settings → Resource controls**. Set CPU cores (0.1–32), memory (64–65536 MiB), and whether to monitor resource incidents. Defaults are one CPU and 512 MiB. Deploy again to apply changes. Every deployment stores its limits, so rolling back also restores the old limits.

Docker enforces the CPU quota by throttling and the memory ceiling with a hard limit; swap is capped at the same amount, preventing additional swap allowance. Apps use `on-failure:5` to restart after crashes, with a maximum of five retries. For Compose, platform settings override the routed app's service and deploy resource limits; other services keep their repository configuration and existing ForgeDock defaults. Managed PostgreSQL/Redis defaults remain separate.

The monitor runs every 30 seconds and records dashboard alerts for:

- An observed out-of-memory exit.
- At least three new Docker restarts, or three consecutive observations of an abnormal exit.
- Three recent metric samples at or above 90% of the CPU quota or memory limit.

CPU percentages are normalized against configured cores: 180% Docker CPU on a two-core quota is 90% of the quota. Docker throttles CPU instead of terminating a container. Resource pressure alerts report sustained usage near the limit. Sampling can miss very short events.

A one-hour cooldown per deployment and incident type prevents repeated messages. Alerts appear in Settings and are queued to any configured Slack, Discord, or SMTP channels, independently of the deployment success/failure choices. Missing or failing notification channels do not affect containers. Deliberately stopped deployments and apps paused for restoration are skipped. These controls limit runtime app resources; they do not cap build workloads or allocate host capacity.

Source: [Docker resource constraints](https://docs.docker.com/engine/containers/resource_constraints/).
