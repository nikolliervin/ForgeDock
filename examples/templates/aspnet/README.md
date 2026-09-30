# ForgeDock aspnet starter

Create your own GitHub repository from these files. Select the matching project template in ForgeDock, paste your HTTPS repository URL, and set the branch you pushed (main by default). If you put these files in a subfolder, change Root directory; Compose files are relative to the repository root.

The health endpoint is /health. The server listens on 0.0.0.0 and the configured PORT (Vite's nginx image uses 8080). Edit build/start commands and health checks to match your application as it grows.

For Node, Next.js, or Vite, run npm install locally and commit package-lock.json before deploying. The Vite Dockerfile requires this lockfile. Dependency ranges are starter defaults; update and review your dependencies before production use.

Managed PostgreSQL and Redis can be added from Databases and supply DATABASE_URL / REDIS_URL after redeployment. This example reports health without validating database connectivity; add your client and migrations when using storage. The Compose template includes a private Redis container and persistent volume.
