# Automatic builds

Choose **Auto** when creating a project to deploy a public HTTPS repository without writing a Dockerfile. Auto checks the configured Dockerfile path (default `Dockerfile`) first. If the file exists, it uses Docker. Otherwise, it runs Railpack against the repository root. Existing projects retain their saved deployment type.

## Setup

`./scripts/start-local.sh` installs the pinned, checksum-verified Railpack executable and starts a local BuildKit container automatically. For manual startup, run `make railpack` before `make worker`. Linux x86_64 and aarch64 are supported by the installer. BuildKit runs as a privileged Docker container, with no published ports, on the trusted worker host. Stop it explicitly with `docker stop forgedock-buildkit`.

The worker defaults to `.runtime/tools/railpack` under `ForgeDock__RuntimePath` and `docker-container://forgedock-buildkit`. Override these with `ForgeDock__RailpackPath` and `ForgeDock__BuildKitHost` in your private `.env` when using a separately managed builder.

## Project settings

Set the repository, branch, container port, and HTTP health path. For Railpack builds, optional **Build command** and **Start command** fields override detection. They are passed to Railpack as literal arguments; Railpack executes application commands inside the build/runtime container. Repository `railpack.json` configuration is also supported. Overrides do not affect Dockerfile builds.

The runtime gets `PORT` set to the configured container port unless the project already has a saved `PORT` variable. Make your app listen on `0.0.0.0` at that port. Health checking and routing use the configured container port, so a saved `PORT` must agree with it.

Project environment variables are runtime-only; ForgeDock does not pass them to Railpack builds. Platform credentials are excluded from the build process environment. Projects that need private package installation or secrets at build time require further integration. The build context is the repository root; use a Dockerfile or Compose for custom monorepo contexts.

## Deployment behavior

Build logs identify the selected builder. Unsupported runtimes or missing start commands fail the deployment; there is no guessed runtime fallback. A build failure leaves the existing application route serving its previous version. Successful builds use the same health checks, container limits, routes, stop/restart, and retained-image rollback flow as Dockerfile deployments. Rollback reuses the original image and configuration without rebuilding.

## References

- [Railpack CLI](https://railpack.com/reference/cli/)
- [Railpack configuration](https://railpack.com/config/file/)
- [Railpack supported languages](https://railpack.com/)
