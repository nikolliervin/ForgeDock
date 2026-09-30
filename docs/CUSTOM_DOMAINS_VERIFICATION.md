# Custom domains verification — 2026-09-30

- Full .NET solution build: zero errors and warnings.
- Backend tests: 55 passed; existing opt-in Compose integration test skipped.
- React production build: TypeScript and Vite passed.
- Browser checks: 10 passed across docs, navigation, and Domains workflows, including mobile layout, creation, DNS records, clipboard, verification, removal, disabled hosting, and duplicate errors.
- Live API: anonymous hosting access rejected; domain listing succeeds for existing projects. On a temporary project, normalization, invalid hostname rejection, TXT/address records, duplicate conflicts, verification queuing, removal queuing, and verification/removal conflict were checked. The temporary project was deleted afterward.
- Local database migrations are applied; EF reports no pending model changes.
- Managed edge starts successfully on loopback and validates its Caddy configuration.
- `python3 scripts/test-https-local.py`: isolated local Pebble ACME issuance passed with certificate hostname and future expiration checks, actual HTTPS response and forwarded scheme/host/path, automatic HTTP redirect, and unknown-host 404. Test containers and network are removed automatically.

The Pebble test authority bypasses public challenge validation and uses an untrusted test CA. Production DNS ownership verification is separately covered by backend tests; actual public DNS verification and public Let's Encrypt issuance were not exercised because no operator-owned public hostname/server was provided. Automatic renewal is delegated to Caddy and was not observed over a renewal period. The browser tests use mocked APIs; live API checks separately cover persistence and validation.
