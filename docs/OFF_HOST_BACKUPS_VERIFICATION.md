# Off-host backup verification

Verified on 2026-10-01:

- Backend build and test suite pass; optional integration tests remain gated by their environment flags.
- The S3 tests exercise endpoint validation, recorded location validation, retention preservation, truncated downloads, and encrypted upload/download/delete over HTTP to an isolated Moto 5.1.14 S3 emulator.
- A worker integration test uses an isolated PostgreSQL control database, a real `postgres:17-alpine` Docker container, and the S3 emulator. It proves missing-bucket upload failure preserves a completed local snapshot, retry uploads after bucket creation, zero local retention removes the uploaded local file, worker restore downloads it and restores original database data, and a subsequent upload expires the older remote object.
- Production frontend build passes. Both `platform-features.spec.ts` browser tests pass, including uploaded status and confirmed restore.
- `OffHostBackups` migration applies and EF reports no pending model changes.

Reproduce S3 integration checks with an isolated S3 emulator:

```bash
python3 -m venv .runtime/s3-test-venv
.runtime/s3-test-venv/bin/pip install 'moto[server]==5.1.14'
.runtime/s3-test-venv/bin/moto_server -H 127.0.0.1 -p 5019
# Separate terminal; Docker needs the cached postgres:17-alpine image:
FORGEDOCK_TEST_S3_ENDPOINT=http://127.0.0.1:5019 \
  bash scripts/with-env.sh dotnet test ForgeDock.sln
npm --prefix web run build
npm --prefix web run test:e2e -- platform-features.spec.ts
```

Tests create unique buckets/databases/containers and remove them afterward. HTTP is explicitly allowed only for this local test endpoint. The browser checks use API fixtures; the worker test exercises actual backup storage and database restore.

AWS S3 and third-party production endpoints were not exercised because no external storage credentials were supplied. MinIO image pulls were unavailable in this environment, so the protocol round trip uses Moto. Bucket policy, versioning/lifecycle, TLS, and provider-specific behavior require verification with the operator's chosen service. Application pause/restart uses the existing restore lifecycle; this test has no active application container.
