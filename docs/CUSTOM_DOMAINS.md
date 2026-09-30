# Custom domains and automatic HTTPS

ForgeDock supports apex domains and subdomains for application projects. An opt-in Caddy edge obtains certificates from Let's Encrypt, redirects HTTP to HTTPS, and renews certificates automatically. The management API, dashboard, PostgreSQL, and nginx development proxy keep their loopback bindings.

## Prepare a public server

Use a hostname you control, such as `deploy.example.com`, as the server's DNS target. Add A/AAAA records pointing it to the server's actual public IP addresses. Use an email you monitor for certificate registration.

Configure these values in the server's private `.env` (replace the documentation placeholders):

```bash
ForgeDock__Domains__Enabled=true
ForgeDock__Domains__Target=deploy.example.com
ForgeDock__Domains__Addresses=203.0.113.10
ForgeDock__Domains__Email=ops@example.com
ForgeDock__Domains__BindAddress=0.0.0.0
ForgeDock__Domains__HttpPort=80
ForgeDock__Domains__HttpsPort=443
```

`Addresses` is a comma-separated list of every public IPv4/IPv6 address allowed for application domains. Do not advertise an AAAA address unless IPv6 traffic reaches the server. Use `BindAddress=::` if publishing the edge on IPv6; confirm the host's Docker networking and firewall forward both required ports correctly.

Ports 80 and 443 must be reachable from the public internet and free on the server. If the machine is behind a router, forward those ports to the edge. Public certificate validation cannot be completed with only localhost access.

Run `./scripts/start-local.sh` after configuring `.env`, or run `make infra`, `make edge`, `make migrate`, and then start the API/worker/dashboard manually. Restart the API and worker after changing hosting settings. A configured HTTPS edge uses the pinned Caddy 2.11.4 image. Its administration endpoint is reachable only inside the edge container.

Custom domains are disabled by default. If enabled without explicit bind/port settings, the edge stays on `127.0.0.1:8080` and `127.0.0.1:8443`, suitable for local infrastructure checks rather than public certificate issuance.

If an existing owned edge container has different port bindings, stop and remove that container before running `make edge` again. Keep its certificate data directory:

```bash
docker stop forgedock-edge
docker rm forgedock-edge
make edge
```

## Attach a domain

Open a project and select **Domains**. Enter a hostname such as `app.example.com` and select **Add domain**. Do not include a scheme, path, port, or wildcard. International names are normalized to ASCII/Punycode. A hostname can belong to only one project at a time.

ForgeDock displays the exact DNS records to add:

- TXT at `_forgedock.app.example.com` with the generated ownership token.
- A/AAAA records for `app.example.com` pointing to the configured server addresses.

For subdomains, a CNAME to `deploy.example.com` can replace the A/AAAA records. Keep the TXT record. DNS providers often want relative names: in the `example.com` zone, use `app` and `_forgedock.app`. For an apex domain, use A/AAAA records at `@` and the ownership record at `_forgedock`.

Disable DNS proxying while verifying; every resolved A/AAAA address must match an allowed server address. Remove stale AAAA records as well as incorrect A records. The worker uses public DNS resolvers, so local hosts-file entries do not count as verification.

## Verification and certificate status

The worker checks pending DNS automatically about once per minute. **Verify DNS** requests another check. After TXT ownership and address routing pass, the edge requests a certificate. The dashboard distinguishes awaiting DNS, awaiting an application deployment, certificate issuance, active HTTPS, and errors.

Deploy the application if it does not have an active version. Existing local project URLs remain available. Custom domains follow successful deployments, restart, and rollback through the same health-checked nginx route. Stopping a project makes its domains unavailable; they remain attached for a later restart. Unknown hostnames return 404 rather than another project's application.

The worker probes the certificate actually served by the edge using TLS SNI, records its expiration, and reports whether it is trusted. HTTPS request headers preserve the original hostname and scheme for the application.

## Renewal and persistence

Caddy manages renewals independently of the worker. Keep the edge running and preserve `.runtime/edge/data`; it contains certificate private keys and ACME account state. Its filesystem permissions are private to the operator. Back it up with your server configuration. Domain certificate status is refreshed while the worker is running.

Removing a domain queues a configuration update and frees the hostname after the edge applies it. Retained certificate data is kept; removing a domain does not delete the project or its database. Deleting a project removes its domain records and the worker reconciles the edge configuration.

## Testing and troubleshooting

For test issuances, use Let's Encrypt's staging directory before requesting production certificates:

```bash
ForgeDock__Domains__AcmeDirectory=https://acme-staging-v02.api.letsencrypt.org/directory
```

Staging certificates are not publicly trusted. The dashboard identifies test-authority configurations and untrusted test certificates. Return to the default production directory when ready, and restart the API/worker.

Check edge logs and the current DNS records if issuance takes longer than expected:

```bash
docker logs --tail 100 forgedock-edge
docker exec forgedock-proxy nginx -t
```

Ensure ports 80/443 are reachable, all published addresses are correct, CAA records permit Let's Encrypt, and the worker can reach public DNS on port 53. ACME providers impose rate limits, so repeated production tests should use staging instead.

This release does not support wildcard domains, DNS-provider API integration, or importing external certificates.

References: [Caddy automatic HTTPS](https://caddyserver.com/docs/automatic-https), [Caddy global options](https://caddyserver.com/docs/caddyfile/options).
