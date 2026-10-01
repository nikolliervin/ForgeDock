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

Successful backups live in `.runtime/backups/*.fgbackup` (or the configured runtime path), with private filesystem permissions and encryption using the platform secret key. Losing the key makes backups unreadable. Configure the S3-compatible store below for automatic off-host copies. Preserve the control database and the key separately; a backup on the same disk does not protect against disk loss.

The default retention is seven successful backups per service; change `ForgeDock__BackupRetentionCount` (1–100) in the worker environment. Expired records stay in history but their files are removed. Manual and scheduled backups share this policy. Project deletion retains backup files and database volumes; an operator may remove them after checking they are no longer needed.

Backups apply to managed PostgreSQL, Redis, MySQL, SQL Server Express, and MongoDB services. They do not cover arbitrary databases inside repository Compose files or the platform control database. Application database backups do not include instance-level users, logins, or other databases. Restore requires free disk space for decrypted files, staging data, and recovery backups.

Sources: [PostgreSQL pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html), [Redis persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/), [MySQL mysqldump](https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html), [SQL Server backup and restore in containers](https://learn.microsoft.com/en-us/sql/linux/migrate/tutorial-restore-backup-sql-server-container?view=sql-server-ver16), [MongoDB mongodump](https://www.mongodb.com/docs/database-tools/mongodump/), [MongoDB mongorestore](https://www.mongodb.com/docs/database-tools/mongorestore/).

## Automatic off-host storage

Create a private bucket on AWS S3 or an S3-compatible service, then configure the **worker** environment:

```dotenv
ForgeDock__BackupS3__Enabled=true
ForgeDock__BackupS3__Endpoint=https://s3.us-east-1.amazonaws.com
ForgeDock__BackupS3__Region=us-east-1
ForgeDock__BackupS3__Bucket=my-forgedock-backups
ForgeDock__BackupS3__Prefix=production-forgedock
ForgeDock__BackupS3__AccessKey=replace_me
ForgeDock__BackupS3__SecretKey=replace_me
ForgeDock__BackupRetentionCount=7
ForgeDock__BackupLocalRetentionCount=2
```

Apply the `OffHostBackups` migration and restart the API and worker. Credentials are worker configuration only and are removed from child-process environments. Use an instance-specific prefix and permissions for `s3:PutObject`, `s3:GetObject`, and `s3:DeleteObject` on that prefix. The bucket must already exist; ForgeDock does not create buckets or change their policies. HTTPS origins are required. `ForgeDock__BackupS3__AllowHttp=true` is available solely for isolated local test servers.

New backups are encrypted before upload. The dashboard shows **Pending**, **Uploaded**, **Failed**, or **Disabled** remote status independently of database snapshot success. Failed uploads preserve the local file and retry every five minutes (maintenance runs once a minute when the serial worker is available). Retries reuse the same object key. Existing local-only backups are not automatically uploaded. Remote errors do not include provider messages or credentials.

`BackupRetentionCount` (1–100) governs retained successful snapshots per service, including remote objects. `BackupLocalRetentionCount` (0–100, defaulting to the total retention) governs local copies only after successful upload. Pending/failed uploads, queued/running restore sources, and uploaded backups while remote storage is disabled are preserved. Failed retention operations retry on a later maintenance cycle. Versioned buckets may retain noncurrent object versions; configure bucket lifecycle rules for those separately.

Choose **Restore this backup** as usual. If the local file is missing, ForgeDock downloads the recorded remote object into a private temporary file, checks its saved size, and authenticates the full encrypted archive before replacing database data. A failed download leaves an existing local archive intact. The endpoint, region, bucket, and object key are recorded with each backup, so changing the prefix does not redirect old backups. Changing endpoint/region/bucket requires restoring the previous configuration to access those backups; credentials can rotate without changing their location. Automatic store migration and remote bucket discovery/import are not included.

Off-host archives still require the original platform secret key and control database for disaster recovery. Project deletion retains remote objects, matching local backup preservation; remove orphaned objects separately after confirming they are no longer needed.

The implementation uses the [AWS SDK for .NET S3 API](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/csharp_s3_code_examples.html) with path-style addressing for compatible endpoints. Provider-specific compatibility should be verified before relying on the store.
