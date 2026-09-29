# Deploying ForgeDock

Current supported demonstration setup runs the API, worker, and Vite on a trusted Linux host, with PostgreSQL/nginx in Docker. Follow [DEVELOPMENT.md](DEVELOPMENT.md). The worker must access the Docker CLI/socket and the absolute runtime route directory. Keep the proxy/container network consistent with worker settings.

`make build` compiles the backend and produces `web/dist`. Set `ForgeDock__WebRoot` to the absolute `web/dist` directory to serve the built dashboard directly from the API at port 5080 with same-origin API calls. Packaged service units, TLS, backup automation, and remote management exposure are not yet supplied. Do not mistake Vite development serving for a hardened production installation.

Before remote deployment, review [SECURITY.md](SECURITY.md), add a TLS reverse proxy for the management API and built frontend, maintain same-origin `/api` routing, configure process supervision, back up PostgreSQL and the encryption key, restrict worker egress, and plan retention. Application routing currently supports local `.localhost` names on port 8088 only.
