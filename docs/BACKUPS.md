# Database backups and restore

In **Project → Backups**, choose **Back up now**, or set an hourly, six-hourly, daily, or weekly schedule for each managed database. The worker persists due dates and jobs in PostgreSQL, catches up after downtime, and executes database jobs serially with deployments. All five database types share encryption, retention, scheduling, and dashboard restore.

| Database | Backup format | Restore behavior |
| --- | --- | --- |
| PostgreSQL | `pg_dump` custom archive for `app` | Validate and restore into a staging database, then swap database names atomically. |
| Redis | Consistent RDB snapshot | Validate the snapshot, replace persisted data, and rebuild append-only persistence. |
| MySQL | `mysqldump` of `app`, including routines, events, and triggers | Validate by importing into a temporary database; replace `app` and import the dump. |
| SQL Server Express | Native copy-only `.bak` with checksums for `app` | Run `RESTORE VERIFYONLY`, disconnect clients, and restore `app` with checksum verification. |
| MongoDB | Compressed `mongodump` archive of `app`, including indexes | Dry-run validation, then replace the app database and restore its collections and indexes. |

MongoDB backups briefly stop the active app (the full stack for Compose) to prevent app writes during the dump. Standalone MongoDB dumps are not point-in-time snapshots while external clients write, so avoid direct external writes during backup. MySQL uses a single transaction for InnoDB tables; avoid schema changes during backup, and use InnoDB for consistent transactional data.

Select **Restore this backup** and confirm. ForgeDock temporarily stops the active app (the whole stack for Compose), replaces current database data, then restarts it. Choose the correct project and timestamp. MySQL, SQL Server, and MongoDB create a temporary pre-restore recovery backup after validation and attempt to recover it if replacement fails. Replacement is not an atomic database swap for those engines. If both restore and recovery fail, the recovery file remains inside the database container at `/tmp/forgedock-restore-<id>.safety`; inspect and recover it before removing the container. Interrupted jobs are marked failed on worker startup; inspect the database and restart or redeploy the app before retrying.

Successful backups live in `.runtime/backups/*.fgbackup` (or the configured runtime path), with private filesystem permissions and encryption using the platform secret key. Losing the key makes backups unreadable. Preserve an off-host copy of this directory, the control database, and the key; a backup on the same disk does not protect against disk loss.

The default retention is seven successful backups per service; change `ForgeDock__BackupRetentionCount` (1–100) in the worker environment. Expired records stay in history but their files are removed. Manual and scheduled backups share this policy. Project deletion retains backup files and database volumes; an operator may remove them after checking they are no longer needed.

Backups apply to managed PostgreSQL, Redis, MySQL, SQL Server Express, and MongoDB services. They do not cover arbitrary databases inside repository Compose files or the platform control database. Application database backups do not include instance-level users, logins, or other databases. Restore requires free disk space for decrypted files, staging data, and recovery backups.

Sources: [PostgreSQL pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html), [Redis persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/), [MySQL mysqldump](https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html), [SQL Server backup and restore in containers](https://learn.microsoft.com/en-us/sql/linux/migrate/tutorial-restore-backup-sql-server-container?view=sql-server-ver16), [MongoDB mongodump](https://www.mongodb.com/docs/database-tools/mongodump/), [MongoDB mongorestore](https://www.mongodb.com/docs/database-tools/mongorestore/).
