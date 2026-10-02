#!/usr/bin/env python3
"""Prepare private, repeatable local Keycloak configuration without printing secrets."""
import json
import os
from pathlib import Path
import secrets
import shlex
import subprocess
import uuid

root = Path(__file__).resolve().parent.parent
env_path = root / '.env'
if not env_path.exists():
    raise SystemExit('Run make init before configuring Keycloak.')
private = Path(os.environ.get('ForgeDock__RuntimePath', str(root / '.runtime'))) / 'keycloak'
private.mkdir(parents=True, exist_ok=True)
private.chmod(0o700)
for folder in ('tls', 'import', 'data', 'postgres', 'proxy'):
    path = private / folder
    existed = path.exists()
    path.mkdir(exist_ok=True)
    # PostgreSQL owns its data directory after its first launch.
    if folder != 'postgres' or not existed:
        path.chmod(0o700)

# Generated values are stable between launches. Existing private values take priority.
state_path = private / 'setup.json'
state = json.loads(state_path.read_text()) if state_path.exists() else {}
def value(name, default):
    state[name] = os.environ.get(name) or state.get(name) or default
    return state[name]

identity_port = value('ForgeDock__Keycloak__HttpsPort', '8444')
dashboard_port = value('ForgeDock__Keycloak__DashboardPort', '5443')
for port in (identity_port, dashboard_port):
    if not port.isdecimal() or not 1024 <= int(port) <= 65535:
        raise SystemExit('Local HTTPS ports must be between 1024 and 65535.')
if identity_port == dashboard_port:
    raise SystemExit('Dashboard and Keycloak need separate HTTPS ports.')
identity_origin = f'https://localhost:{identity_port}'
dashboard_origin = f'https://localhost:{dashboard_port}'
client_secret = value('ForgeDock__Oidc__ClientSecret', secrets.token_hex(32))
admin_password = value('ForgeDock__Keycloak__AdminPassword', secrets.token_urlsafe(32))
operator_password = value('ForgeDock__Keycloak__OperatorPassword', secrets.token_urlsafe(24))
operator_name = value('ForgeDock__Keycloak__OperatorUsername', 'operator')
operator_subject = value('ForgeDock__Keycloak__OperatorSubject', str(uuid.uuid4()))
database_password = value('ForgeDock__Keycloak__DatabasePassword', secrets.token_hex(32))
admin_name = value('ForgeDock__Keycloak__AdminUsername', 'admin')
image = value('ForgeDock__Keycloak__Image', 'quay.io/keycloak/keycloak:26.8.0')
second_factor = value('ForgeDock__Keycloak__SecondFactor', 'totp')
if second_factor not in ('totp', 'webauthn'):
    raise SystemExit('Keycloak__SecondFactor must be totp or webauthn.')
authenticator = 'auth-otp-form' if second_factor == 'totp' else 'webauthn-authenticator'
enrollment_action = 'CONFIGURE_TOTP' if second_factor == 'totp' else 'webauthn-register'
browser_flow = 'forgedock-browser-' + second_factor
forms_flow = 'forgedock-forms-' + second_factor

settings = {
    'ForgeDock__Auth__Mode': 'keycloak',
    'ForgeDock__Keycloak__LocalEnabled': 'true',
    'ForgeDock__Oidc__Enabled': 'true',
    'ForgeDock__Oidc__Authority': identity_origin + '/realms/forgedock',
    'ForgeDock__Oidc__PublicOrigin': dashboard_origin,
    'ForgeDock__Oidc__ClientId': 'forgedock',
    'ForgeDock__Oidc__ClientSecret': client_secret,
    'ForgeDock__Oidc__AllowedSubjects__0': operator_subject,
    'ForgeDock__DashboardBaseUrl': dashboard_origin,
    **state,
}
# Retain every unrelated setting/comment; replace only settings managed by this setup.
lines = env_path.read_text().splitlines()
found = set()
for i, line in enumerate(lines):
    if '=' in line and not line.lstrip().startswith('#'):
        name = line.split('=', 1)[0].strip()
        if name in settings:
            lines[i] = f'{name}={shlex.quote(settings[name])}'
            found.add(name)
if not any('AUTHENTICATION MODES' in line for line in lines):
    lines += [
        '', '# AUTHENTICATION MODES: see docs/AUTHENTICATION.md for keys and issuer URLs.',
        '# Auth__Mode: keycloak | oidc | entra | auth0 | okta | authentik | token (prefix ForgeDock__).',
        '# SSO requires Oidc__Authority, PublicOrigin, ClientId, ClientSecret, AllowedSubjects__0.',
        '# Local dashboard: https://localhost:' + dashboard_port + ' ; SecondFactor=totp (phone) or webauthn (key).',
        '# Change the initial OperatorPassword at first login and enroll the chosen second factor.',
        '# Keep these generated secrets private. Token mode has no MFA; SSO disables ApiToken.',

    ]
lines += [f'{key}={shlex.quote(val)}' for key, val in settings.items() if key not in found]
env_path.write_text('\n'.join(lines) + '\n')
env_path.chmod(0o600)
state_path.write_text(json.dumps(state, indent=2) + '\n')
state_path.chmod(0o600)

tls = private / 'tls'
ca_changed = not (tls / 'ca.crt').exists()
if not ca_changed:
    ca_text = subprocess.run(['openssl', 'x509', '-in', str(tls / 'ca.crt'), '-noout', '-text'], check=True, capture_output=True, text=True).stdout
    ca_changed = 'X509v3 Name Constraints: critical' not in ca_text
if ca_changed:
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-days', '3650',
        '-keyout', str(tls / 'ca.key'), '-out', str(tls / 'ca.crt'), '-subj', '/CN=ForgeDock Local SSO CA',
        '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign',
        '-addext', 'nameConstraints=critical,permitted;DNS:localhost,permitted;IP:127.0.0.0/255.0.0.0,permitted;IP:::1/ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff'], check=True, capture_output=True)
if ca_changed or not (tls / 'localhost.crt').exists():
    subprocess.run(['openssl', 'req', '-newkey', 'rsa:3072', '-nodes', '-keyout', str(tls / 'localhost.key'),
        '-out', str(tls / 'localhost.csr'), '-subj', '/CN=localhost'], check=True, capture_output=True)
    extensions = tls / 'localhost.ext'
    extensions.write_text('subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n')
    subprocess.run(['openssl', 'x509', '-req', '-in', str(tls / 'localhost.csr'), '-CA', str(tls / 'ca.crt'),
        '-CAkey', str(tls / 'ca.key'), '-CAcreateserial', '-days', '365', '-out', str(tls / 'localhost.crt'),
        '-extfile', str(extensions)], check=True, capture_output=True)
for path in tls.iterdir():
    path.chmod(0o600)

realm = {
    'realm': 'forgedock', 'enabled': True, 'displayName': 'ForgeDock', 'loginTheme': 'forgedock',
    'sslRequired': 'all', 'registrationAllowed': False, 'resetPasswordAllowed': False,
    'rememberMe': False, 'bruteForceProtected': True, 'permanentLockout': False,
    'failureFactor': 5, 'waitIncrementSeconds': 60, 'maxFailureWaitSeconds': 900,
    'ssoSessionIdleTimeout': 1800, 'ssoSessionMaxLifespan': 28800,
    'passwordPolicy': 'length(14) and notUsername and notEmail',
    'webAuthnPolicyRpEntityName': 'ForgeDock', 'webAuthnPolicyRpId': 'localhost',
    'webAuthnPolicyUserVerificationRequirement': 'required',
    'webAuthnPolicySignatureAlgorithms': ['ES256', 'RS256'],
    'webAuthnPolicyAvoidSameAuthenticatorRegister': True,
    'browserFlow': browser_flow,
    'authenticationFlows': [
        {'alias': browser_flow, 'description': 'Password plus required ' + second_factor + '; SSO cookie reuse.',
         'providerId': 'basic-flow', 'topLevel': True, 'builtIn': False,
         'authenticationExecutions': [
             {'authenticator': 'auth-cookie', 'requirement': 'ALTERNATIVE', 'priority': 10, 'authenticatorFlow': False},
             {'flowAlias': forms_flow, 'requirement': 'ALTERNATIVE', 'priority': 20, 'authenticatorFlow': True},
         ]},
        {'alias': forms_flow, 'providerId': 'basic-flow', 'topLevel': False, 'builtIn': False,
         'authenticationExecutions': [
             {'authenticator': 'auth-username-password-form', 'requirement': 'REQUIRED', 'priority': 10, 'authenticatorFlow': False},
             {'authenticator': authenticator, 'requirement': 'REQUIRED', 'priority': 20, 'authenticatorFlow': False},
         ]},
    ],
    'otpPolicyType': 'totp', 'otpPolicyAlgorithm': 'HmacSHA1',
    'otpPolicyDigits': 6, 'otpPolicyPeriod': 30, 'otpPolicyLookAheadWindow': 1,
    'requiredActions': [
        {'alias': 'CONFIGURE_TOTP', 'name': 'Configure OTP', 'providerId': 'CONFIGURE_TOTP', 'enabled': True, 'defaultAction': False, 'priority': 40},
        {'alias': 'UPDATE_PASSWORD', 'name': 'Update Password', 'providerId': 'UPDATE_PASSWORD', 'enabled': True, 'defaultAction': False, 'priority': 30},
        {'alias': 'webauthn-register', 'name': 'Webauthn Register', 'providerId': 'webauthn-register', 'enabled': True, 'defaultAction': False, 'priority': 50},
    ],
    'clients': [{
        'clientId': 'forgedock', 'name': 'ForgeDock dashboard', 'enabled': True,
        'protocol': 'openid-connect', 'publicClient': False, 'clientAuthenticatorType': 'client-secret',
        'secret': client_secret, 'standardFlowEnabled': True, 'implicitFlowEnabled': False,
        'directAccessGrantsEnabled': False, 'serviceAccountsEnabled': False,
        'redirectUris': [dashboard_origin + '/api/auth/callback'], 'webOrigins': [dashboard_origin],
        'defaultClientScopes': ['profile'],
        'attributes': {'pkce.code.challenge.method': 'S256'},
    }],
    'users': [{
        'id': operator_subject, 'username': operator_name, 'enabled': True,
        'firstName': 'ForgeDock', 'lastName': 'Operator',
        'requiredActions': ['UPDATE_PASSWORD', enrollment_action],
        'credentials': [{'type': 'password', 'value': operator_password, 'temporary': True}],
    }],
}
realm_path = private / 'import' / 'forgedock-realm.json'
realm_path.write_text(json.dumps(realm, indent=2) + '\n')
realm_path.chmod(0o600)
(private / 'postgres.env').write_text(f'POSTGRES_DB=keycloak\nPOSTGRES_USER=keycloak\nPOSTGRES_PASSWORD={database_password}\n')
(private / 'keycloak.env').write_text('\n'.join([
    'KC_DB=postgres', 'KC_DB_URL=jdbc:postgresql://forgedock-keycloak-db:5432/keycloak',
    'KC_DB_USERNAME=keycloak', f'KC_DB_PASSWORD={database_password}',
    f'KC_BOOTSTRAP_ADMIN_USERNAME={admin_name}', f'KC_BOOTSTRAP_ADMIN_PASSWORD={admin_password}',
    f'KC_HOSTNAME={identity_origin}', 'KC_HTTPS_CERTIFICATE_FILE=/etc/keycloak/tls/localhost.crt',
    'KC_HTTPS_CERTIFICATE_KEY_FILE=/etc/keycloak/tls/localhost.key',
    'KC_HTTP_ENABLED=false', 'KC_HEALTH_ENABLED=true',
]) + '\n')
for filename in ('postgres.env', 'keycloak.env'):
    (private / filename).chmod(0o600)
api_url = os.environ.get('ASPNETCORE_URLS', 'http://127.0.0.1:5080')
if api_url != 'http://127.0.0.1:5080':
    raise SystemExit('Local SSO proxy expects ASPNETCORE_URLS=http://127.0.0.1:5080.')
(private / 'proxy' / 'Caddyfile').write_text(f'''{{
    admin off
    auto_https off
}}
https://localhost:{dashboard_port} {{
    bind 127.0.0.1 ::1
    tls /etc/forgedock-tls/localhost.crt /etc/forgedock-tls/localhost.key
    handle /api/* {{
        reverse_proxy 127.0.0.1:5080 {{
            header_up Host {{http.request.host}}
        }}
    }}
    handle {{
        reverse_proxy 127.0.0.1:5173
    }}
}}
''')
# Preserve access for the workspace owner when this script is run by root.
owner = env_path.stat()
if os.geteuid() == 0:
    for path in [private, *private.rglob('*')]:
        if path.is_file() or path.is_dir():
            # Postgres will manage its own data-directory ownership after launch.
            if path != private / 'postgres' and private / 'postgres' not in path.parents:
                os.chown(path, owner.st_uid, owner.st_gid)
print(f'Local Keycloak prepared at {identity_origin}; dashboard: {dashboard_origin}.')
print('Initial credentials are stored privately in .env; no passwords are printed.')
