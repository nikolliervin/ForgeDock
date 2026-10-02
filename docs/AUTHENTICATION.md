# Authentication

Choose `ForgeDock__Auth__Mode` in `.env` and restart the API. One provider is active at a time.

## Local Keycloak (current setup)

1. Run `make keycloak`, then start the app with `scripts/start-local.sh`.
2. Trust the localhost-only HTTPS certificate: `sudo bash scripts/trust-keycloak-local.sh`. Restart your browser if needed.
3. Open **https://localhost:5443**. Sign in with `OperatorUsername` / `OperatorPassword` from `.env`, change the temporary password, and scan the phone authenticator’s QR code.

Keycloak admin: **https://localhost:8444/admin/**. Use `AdminUsername` / `AdminPassword` from `.env`. These are separate from the operator account.

The generated realm requires password + phone authenticator codes. Set `ForgeDock__Keycloak__SecondFactor=webauthn` for security keys/passkeys, or `totp` for phone codes; run `make keycloak` to apply it. Registration is closed. Add users in Keycloak, add their user IDs to `AllowedSubjects__1`, etc., then restart the API. All approved users have full access.

## `.env` modes and keys

| `ForgeDock__Auth__Mode` | `ForgeDock__Oidc__Authority` |
| --- | --- |
| `keycloak` | `https://localhost:8444/realms/forgedock` |
| `oidc` | Your provider's HTTPS issuer |
| `entra` | `https://login.microsoftonline.com/TENANT-ID/v2.0` |
| `auth0` | `https://TENANT.REGION.auth0.com/` |
| `okta` | `https://ORG.okta.com/oauth2/default` |
| `authentik` | `https://auth.example.com/application/o/forgedock/` |
| `token` | Not used; requires `ForgeDock__ApiToken` (32+ random characters, no MFA) |

Every SSO mode requires:

```dotenv
ForgeDock__Auth__Mode=keycloak
ForgeDock__Oidc__Authority=https://localhost:8444/realms/forgedock
ForgeDock__Oidc__PublicOrigin=https://localhost:5443
ForgeDock__Oidc__ClientId=forgedock
ForgeDock__Oidc__ClientSecret=your-confidential-client-secret
ForgeDock__Oidc__AllowedSubjects__0=approved-user-subject-id
```

Register `PublicOrigin/api/auth/callback` with the provider. Use immutable `sub` IDs, not emails. Require passkey/security-key MFA in the provider. SSO disables the shared token. Unknown modes or incomplete configuration fail startup.

`Keycloak__LocalEnabled=true` starts the local stack; use `false` for remote providers. Optional local keys: `Keycloak__HttpsPort=8444`, `Keycloak__DashboardPort=5443`, `Keycloak__Image=quay.io/keycloak/keycloak:26.8.0` (prefix each with `ForgeDock__`).

For Google/GitHub/Microsoft login choices, use Keycloak's Identity providers. Brokered login needs a browser/post-login flow that also enforces a second factor; the supplied local flow does not enable social sign-in.

## Operational notes

Sessions last eight hours. Sign out revokes the ForgeDock session; API restarts revoke all sessions. Provider sessions remain active. The session store supports one API instance.

Local ports bind only to loopback. Back up `.env` and `.runtime/keycloak/postgres`; keep certificate keys private. Realm import only initializes a new database. Change existing users/client settings in Keycloak; restart does not reset credentials. After first login, the initial `OperatorPassword` in `.env` is stale. Changed container ports/secrets/images require recreating the affected container while retaining its database.
