# Managed database services

Open **Project → Databases** and add PostgreSQL, Redis, MySQL, SQL Server Express, or MongoDB. Wait for Running, then deploy the app. The same choices are available when creating a project.

| Service | Image | App database / user | Connection variable | Memory |
| --- | --- | --- | --- | --- |
| PostgreSQL | `postgres:17-alpine` | `app` / `app` | `DATABASE_URL` (PostgreSQL URI) | 512 MiB |
| Redis | `redis:7.4-alpine` | database 0, password required | `REDIS_URL` | 512 MiB |
| MySQL | `mysql:8.4` | `app` / `app` | `MYSQL_URL` (MySQL URI) | 512 MiB |
| SQL Server Express | `mcr.microsoft.com/mssql/server:2022-latest` | `app` / `app` | `SQLSERVER_CONNECTION_STRING` (ADO.NET format) | 2048 MiB |
| MongoDB | `mongo:8.0` | `app` / `app`, authentication database `app` | `MONGODB_URL` | 512 MiB |

ForgeDock generates and encrypts random passwords. MySQL and MongoDB app users are scoped to the app database; SQL Server's app login owns only that database. Redis uses append-only persistence. SQL Server Express is free, requires an x86-64 host, and limits each database to 10 GB. Creation requires accepting Microsoft's SQL Server license terms. Its connection string trusts the container's self-signed certificate on the private network; change this if you require certificate verification.

Your app reads its connection variable with the appropriate database client. Existing deployments keep their original environment, so deploy again after adding a service. An existing variable with the same name must be removed before provisioning; ForgeDock never silently replaces your external database. PostgreSQL, MySQL, and SQL Server use distinct variable names so they can coexist in one project.

Services run on a private Docker network for their project. The app joins it at deployment time (for Compose, only the routed service joins). Database ports are not published on the host. Data lives in the named volume `forgedock-db-data-<service ID without hyphens>` and survives container restarts and app redeployments. Keep the platform secret key and control database safe: they contain the credentials needed to reconnect.

Use **Start / retry** if provisioning fails or the project was stopped. Stopping a project stops its databases. Deleting a project removes database containers but retains volumes; reclaim them manually after confirming the data is no longer needed. The management PostgreSQL database remains separate from application databases.

Each database receives one CPU and a 256-process limit. Images are pulled on first provisioning, so the host needs access to Docker Hub and, for SQL Server, Microsoft Container Registry. Readiness waits up to three minutes after container creation. Preview environments get fresh, independent copies of all managed database types.

Sources: [SQL Server containers](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver16), [SQL Server editions and limits](https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2022?view=sql-server-ver16), [SQL Server license terms](https://go.microsoft.com/fwlink/?LinkId=746388).
