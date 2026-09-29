# Compose lifecycle fixture

Two services: a Node HTTP application and Redis with a persistent named volume. The HTTP health endpoint checks Redis connectivity. Change `web/version.txt` to build a visibly different release. The request counter survives stack updates and rollback.

For ForgeDock, publish this directory in a public Git repository and select Compose mode, file `compose.yaml`, public service `web`, port `3000`, health path `/health`. `DEMO_LABEL` is an optional interpolation variable. The opt-in Docker integration test builds this fixture locally and verifies changed-page rollback, persistence, stop, failed startup, and scoped deletion.
