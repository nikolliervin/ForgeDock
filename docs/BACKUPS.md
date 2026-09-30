# Database backups and restore

In **Project → Backups**, choose **Back up now**, or set an hourly, six-hourly, daily, or weekly schedule for each managed database. The worker persists due dates and jobs in PostgreSQL, catches up after downtime, and executes database jobs serially with deployments. PostgreSQL uses `pg_dump` custom archives; Redis uses a consistent RDB snapshot. Both are encrypted using the platform secret key.

Select **Restore this backup** and confirm. ForgeDock temporarily stops the active app (the whole stack for Compose), restores data, then restarts it. PostgreSQL restores into a staging database and swaps database names atomically after validation. Redis validates the snapshot before replacing its persisted data and rebuilds append-only persistence. Existing data is replaced; choose the correct project and timestamp.

Successful backups live in `.runtime/backups/*.fgbackup` (or the configured runtime path), with private filesystem permissions. Keep the platform secret key: losing it makes backups unreadable. Preserve a separate off-host copy of this directory, the control database, and the key; a backup on the same disk does not protect against disk loss.

The default retention is seven successful backups per service; change `ForgeDock__BackupRetentionCount` (1–100) in the worker environment. Expired records stay in history but their files are removed. Manual and scheduled backups share this retention policy. Project deletion retains backup files and database volumes; an operator may remove them after checking they are no longer needed.

Backups apply to managed PostgreSQL and Redis services, not arbitrary databases inside repository Compose files or the platform control database. Interrupted jobs are marked failed on worker startup. If the worker stops during a restore, inspect the database and restart or redeploy the app before retrying. Restore requires free disk space for decrypted files and, for PostgreSQL, a staging database.

Sources: [PostgreSQL pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html), [Redis persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/).
