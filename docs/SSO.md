# SSO

See [AUTHENTICATION.md](AUTHENTICATION.md) for local Keycloak setup, supported `.env` modes, and required values.

The implementation uses OIDC code flow with PKCE, allowlisted subjects, server-side sessions, Secure/HttpOnly cookies, and CSRF protection. Provider tokens are not exposed to JavaScript. API requests return 401/403 instead of login redirects.

For remote hosting, serve the UI and `/api` on one HTTPS origin. Keep the API private, forward the original host and HTTPS scheme, and set `ForgeDock__Oidc__TrustedProxies__0` if the proxy is not on loopback.
