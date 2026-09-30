# Pull request preview environments

1. Enable GitHub auto-deploy in project Settings and configure the signed webhook using [the Cloudflare guide](GITHUB_WEBHOOKS.md).
2. Open **Previews → Enable previews**.
3. In GitHub's webhook settings, select **Let me select individual events**, then enable **Pushes** and **Pull requests**.
4. Open a non-draft pull request targeting the project's configured branch, or push another commit to an existing one. A preview project appears with its own deployment history and app URL.

Each preview deploys the exact head commit. Updates redeploy the same preview URL. Closing or merging the PR queues deletion of its route and containers. Duplicate deliveries do not create duplicate deployments; older PR events are ignored after a newer close event. GitHub webhook delivery order is not guaranteed, so status is tracked using the PR update timestamp.

Previews accept same-repository PRs only. Forks and draft PRs are skipped. Production environment variables are never inherited. If the parent has managed PostgreSQL, Redis, MySQL, SQL Server Express, or MongoDB, the preview gets fresh, independent services and connection variables. Open the preview project's Environment tab to configure other variables; they persist for future PR updates. Repository Compose stacks retain their existing project isolation. Use non-production credentials for any external services.

Local previews use `http://<preview-project-id-without-hyphens>.localhost:8088`. For public previews, configure public HTTPS hosting as described in [CUSTOM_DOMAINS.md](CUSTOM_DOMAINS.md), create a wildcard DNS record `*.previews.example.com` pointing to the host, and set `ForgeDock__PreviewBaseDomain=previews.example.com` in the API environment. URLs become `https://pr-<number>-<parent-project-id>.previews.example.com`. The operator-provided suffix is trusted; individual PR domains do not need TXT verification. The HTTPS edge obtains individual certificates, so HTTP certificate validation must reach the host. Certificate issuance is subject to the CA's rate limits.

Disabling previews stops new deployments but leaves close-event cleanup enabled while the GitHub webhook is enabled. Existing PRs are discovered when an eligible event arrives; enabling does not scan GitHub. Deleted preview volumes, backups, retained images, and source folders require separate operator cleanup, as with ordinary projects. Previews use this host's build and runtime capacity.

Source: [GitHub pull request webhook events](https://docs.github.com/en/webhooks/webhook-events-and-payloads#pull_request).
