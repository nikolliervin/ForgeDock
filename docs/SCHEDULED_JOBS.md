# Scheduled jobs

Open a project's **Jobs** page to define an application task. Use a name, shell command, repeat interval, and execution timeout. An interval of zero creates a manual-only task; recurring intervals range from 1 to 10080 minutes. Timeouts range from 1 to 900 seconds. Pause recurring tasks without deleting their history, or run a task immediately.

Runs use the active release's retained image and an encrypted snapshot of the project's environment at queue time. Editing variables later does not alter queued runs. Deploy an application before running tasks. Tasks execute in separate constrained containers with an init process, on the application network; application volumes are not mounted. Commands must be available in the image and compatible with its shell. Use network database connections for migrations or maintenance.

The scheduler coalesces missed intervals rather than replaying every missed occurrence, and permits one pending run per task. Conflicting project operations and backups prevent new runs. Each run records state, timestamps, final exit code, bounded output, truncation status, and failure reason. Output capture is limited to 65536 characters and redacts configured secrets. Enable project failure notifications to receive failed-run alerts.

Task containers are removed after execution. Interrupted runs fail visibly during worker recovery; they are not silently replayed. Storage retention protects images referenced by pending runs. Apply the ScheduledJobs migration before starting the updated API and worker; see [development](DEVELOPMENT.md).
