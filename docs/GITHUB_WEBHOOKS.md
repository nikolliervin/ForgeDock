# GitHub auto-deploy

Each project can opt into automatic deployments on GitHub push events. The endpoint verifies the raw request body using the project's encrypted HMAC-SHA256 secret, checks the configured GitHub repository and branch, then atomically stores a delivery receipt and a queued deployment. Builds run through the existing worker. The queued deployment pins the pushed `after` SHA and snapshots the current project configuration and encrypted environment variables.

## Prepare the API

Apply migrations and restart the API and worker after updating:

```sh
bash scripts/with-env.sh dotnet ef database update --project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api
```

## Test locally with Cloudflare

This is the local setup used to verify GitHub deliveries. It requires Docker on the Linux host and needs no Cloudflare account or domain. The temporary address stops working when the tunnel stops.

1. Start ForgeDock from the repository directory and keep its terminal open:

   ```sh
   bash scripts/start-local.sh
   ```

2. Open a second terminal and start the tunnel:

   ```sh
   docker run --rm --network host cloudflare/cloudflared:latest tunnel --url http://127.0.0.1:5080
   ```

3. Find the `https://...trycloudflare.com` address in the tunnel output. Keep this terminal open too. The tunnel connects to the API on **5080**, not the dashboard on 5173 or deployed applications on 8088.
4. Open your project in ForgeDock. In Settings, confirm the branch matches the branch you will push to: `master` and `main` are different branches.
5. Scroll to **GitHub auto-deploy** and click **Enable auto-deploy**. Paste the Cloudflare address into **Webhook payload URL**, then click outside the field. ForgeDock adds `/api/webhooks/github/<project-id>` automatically.
6. Copy the **complete payload URL**, including the project path, and the **Webhook secret**. If the secret is hidden, click **Rotate webhook secret**, confirm, and copy the new value. Update GitHub whenever you rotate it.
7. Follow the GitHub repository setup below. Saving the webhook sends a `ping`; ForgeDock should show **GitHub connection verified** within about 10 seconds.

For example:

```text
Cloudflare address:
https://your-tunnel.trycloudflare.com

GitHub Payload URL:
https://your-tunnel.trycloudflare.com/api/webhooks/github/<project-id>
```

GitHub uses the full URL. The Cloudflare address alone does not identify the project.

The edited setup URL is remembered in your browser. It does not update GitHub automatically, start a tunnel, or change server routing. If you restart Cloudflare and receive a different hostname, update the payload URL in both ForgeDock and GitHub. You do not need `ForgeDock__WebhookBaseUrl` for this local test.

This quick-tunnel command exposes the API service temporarily; management routes still require the management token, and the webhook receiver checks its own secret. Stop the tunnel when finished. For a permanent server, use the route-restricted proxy described below.

## Connect a repository

1. Use a normal `https://github.com/owner/repo.git` project URL and select the branch to deploy.
2. Open project **Settings → GitHub auto-deploy → Enable auto-deploy**.
3. For a temporary tunnel, paste its HTTPS address into the editable payload URL field. The project webhook path is added when you leave the field, and the URL is remembered in this browser. Copy the complete payload URL and secret. The secret is shown only on creation or rotation; normal API reads never disclose it.
4. In GitHub, open repository **Settings → Webhooks → Add webhook**. Enter the URL, choose `application/json`, paste the secret, select **Just the push event**, and leave SSL verification enabled.
5. Save. The initial `ping` delivery should show **GitHub connection verified** in ForgeDock.
6. Push to the configured branch. The matching delivery should show **Deployment queued**, and the deployment history will show **GitHub push**.

Private repositories also need the worker's `ForgeDock__GitHubToken` configured with repository read access. The webhook secret authenticates deliveries; it does not grant Git cloning access.

## Permanent public endpoint

GitHub needs a publicly reachable HTTPS endpoint. Keep the management API on loopback and configure a host-side HTTPS reverse proxy to forward only `POST /api/webhooks/github/*` to `127.0.0.1:5080`. Preserve the request body and `X-Hub-Signature-256`, `X-GitHub-Event`, and `X-GitHub-Delivery` headers. Neither webhook secrets nor the management token belong in the URL.

For example, with Caddy running directly on the same host as the API and managing the HTTPS listener:

```caddyfile
hooks.example.com {
    @github {
        method POST
        path /api/webhooks/github/*
    }
    handle @github {
        reverse_proxy 127.0.0.1:5080
    }
    handle {
        respond "Not found" 404
    }
}
```

Point the webhook hostname's DNS to your server and provide a trusted HTTPS certificate. Use your existing public proxy if it already owns ports 80/443; the example above is a host-side configuration, not a configuration for ForgeDock's managed application edge container. The application edge routes application domains and does not expose the API automatically.

Set the public origin in the server `.env` and restart the API:

```dotenv
ForgeDock__WebhookBaseUrl=https://hooks.example.com
```

This setting controls the displayed setup URL. It does not provision DNS, HTTPS, or proxy routing. For local testing, use the Cloudflare setup above; a `localhost` URL will not work from GitHub.

## Make a test push

Make a normal change and commit it to the configured branch, or create an empty test commit from your application's Git checkout:

```sh
git branch --show-current
git commit --allow-empty -m "Test ForgeDock auto-deploy"
git push
```

Confirm the current branch is the branch configured in ForgeDock before committing. These commands are for the repository being deployed, not necessarily ForgeDock's own source repository.

After the push:

- Settings should show **Deployment queued**.
- Deployments should contain a new entry labeled **GitHub push**.
- Its build logs should progress through cloning, building, and health checks.
- A successful build switches the application to the new version. If the build fails, inspect its logs; receiving the webhook successfully does not guarantee the application builds successfully.

To check duplicate protection, open GitHub's webhook **Recent deliveries**, select the already accepted push, and click **Redeliver**. ForgeDock should acknowledge it as a duplicate without creating another deployment.

## Find the webhook secret

The secret appears in **Project Settings → GitHub auto-deploy** when auto-deploy is first enabled. Copy it into GitHub's **Secret** field. It is encrypted on the server and is not returned by ordinary settings reads. Leaving the page or hiding the value removes it from the setup view.

If you lost it, choose **Rotate webhook secret**, confirm, and copy the new value into GitHub. Rotation invalidates the previous secret immediately. Do not paste webhook secrets or GitHub tokens into chat, URLs, source files, or screenshots you share.

## Troubleshooting the local setup

| Symptom | What to check |
| --- | --- |
| **Waiting for GitHub** | Keep ForgeDock and Cloudflare running. In GitHub's **Recent deliveries**, inspect the `ping` response and redeliver it after fixing the URL or secret. |
| GitHub returns **404** | Use the full URL including `/api/webhooks/github/<project-id>`, confirm the project has auto-deploy configured, and use the current Cloudflare hostname. Restart ForgeDock after installing API changes. |
| GitHub returns **401** | Copy the current webhook secret into GitHub's Secret field. If you rotated it, replace the old secret and save. |
| GitHub returns **415** | Set the webhook Content type to **application/json**. |
| Connection verified, but a push does nothing | Enable auto-deploy and check the repository and exact branch. A push to `master` will not deploy a project configured for `main`. Check the last delivery result for an ignored branch or repository. |
| GitHub returns **409** | Wait for the project's stop/delete operation to finish, then redeliver the rejected push. |
| Cloning says **terminal prompts disabled** | For a private repository, set `ForgeDock__GitHubToken` in the server `.env` and restart the worker. The webhook secret cannot authenticate Git cloning. |
| Private clone still fails after editing `.env` | Confirm the token has the selected repository and **Contents: Read-only** access, obtain any required organization approval, and restart ForgeDock so the worker loads the new token. |
| Tunnel URL stopped working | Restart the tunnel and update GitHub's payload URL if the new hostname differs. |

For private GitHub repositories, the `.env` setting is:

```dotenv
ForgeDock__GitHubToken=github_pat_your_actual_token
```

Keep this file private. Restarting the workspace loads the setting; changing the file alone does not update a running worker.

## Delivery behavior

- Repository identities are compared case-insensitively, accepting URLs with or without `.git`; branch names are compared exactly.
- Tags, branch deletions, other branches/repositories, and non-push events do not queue deployments.
- Delivery IDs are deduplicated per project in PostgreSQL, including concurrent redeliveries and API restarts. Receipts remain even if a failed deployment history entry is deleted.
- The receiver acknowledges after committing the queue entry rather than waiting for a build. A successful delivery can still result in a failed build; inspect deployment logs.
- Delivery order determines queue order. Every matching delivery queues its exact commit, including force pushes. GitHub can deliver events out of order; use deployment history to inspect or roll back a revision.
- Pending project operations return `409` without recording the delivery so it can be redelivered later. Existing deployments can finish while subsequent pushes wait in the queue.
- Disabling auto-deploy ignores future signed deliveries and does not cancel deployments already queued. Those can be cancelled in deployment history.
- Rotating the secret immediately rejects the old signature. Update the GitHub webhook's secret to the newly displayed value.
- Payloads must be JSON and are limited to 2 MiB. Invalid/missing signatures return `401`; malformed payloads or delivery headers return `400`; unconfigured projects return `404`.
- The dashboard shows the last authenticated, recorded delivery. Rejected requests are visible in GitHub's **Recent deliveries** rather than stored as trusted delivery records.
- GitHub does not automatically retry every failed delivery. After recovering from downtime or an operation conflict, use GitHub's **Redeliver** control. Previously accepted deliveries stay deduplicated.

See [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/), [GitHub signature verification](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries), [GitHub webhook best practices](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks), and [Caddy route handling](https://caddyserver.com/docs/caddyfile/directives/handle).

## Verification

```sh
dotnet test tests/ForgeDock.Tests
bash scripts/with-env.sh python3 tests/integration/github-webhooks.py
```

The integration test creates and drops its own PostgreSQL database and starts a temporary API without a worker. It checks signature rejection, ping handling, repository/branch/tag/deletion filtering, concurrent deduplication, commit pinning, environment snapshots, disabling, secret rotation, and pending-operation conflicts. It does not modify existing projects or build application images.
