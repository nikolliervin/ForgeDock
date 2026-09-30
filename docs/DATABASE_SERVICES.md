# Managed PostgreSQL and Redis

Open **Project → Databases** and choose **Add PostgreSQL** or **Add Redis**. Wait for Running, then deploy the app. PostgreSQL 17 uses database `app`, user `app`, and an encrypted random password. Redis 7.4 requires a password and enables append-only persistence.

ForgeDock saves `DATABASE_URL` or `REDIS_URL` in encrypted project environment variables. Your app reads that variable with its database client. Existing deployments keep their original environment, so deploy again after adding a service. An existing variable with the same name must be removed before provisioning; ForgeDock never silently replaces your external database.

Services run on a private Docker network for their project. The app joins it at deployment time (for Compose, only the routed service joins). Neither database publishes ports on the host. Data lives in the named volume `forgedock-db-data-<service ID without hyphens>` and survives container restarts and app redeployments. Keep the platform secret key and control database safe: they contain the credentials needed to reconnect.

Use **Start / retry** if provisioning fails or the project was stopped. Stopping a project stops its databases. Deleting a project removes database containers but deliberately retains volumes; reclaim those volumes manually only after confirming you no longer need the data. The management PostgreSQL database remains separate from these application databases.

Defaults are 512 MiB memory, one CPU, and 256 processes per database. PostgreSQL and Redis images are pulled on first provisioning, so the host needs Docker Hub access.
