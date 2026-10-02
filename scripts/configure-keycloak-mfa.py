#!/usr/bin/env python3
"""Apply the selected required second factor to the owned local Keycloak realm."""
import json
import os
import urllib.error
import urllib.parse
import urllib.request

factor = os.environ.get('ForgeDock__Keycloak__SecondFactor', 'totp')
if factor not in ('totp', 'webauthn'):
    raise SystemExit('Keycloak__SecondFactor must be totp or webauthn.')
base = 'https://localhost:' + os.environ['ForgeDock__Keycloak__HttpsPort']
login = urllib.parse.urlencode({
    'grant_type': 'password', 'client_id': 'admin-cli',
    'username': os.environ['ForgeDock__Keycloak__AdminUsername'],
    'password': os.environ['ForgeDock__Keycloak__AdminPassword'],
}).encode()
try:
    token = json.load(urllib.request.urlopen(urllib.request.Request(
        base + '/realms/master/protocol/openid-connect/token', data=login)))['access_token']
except urllib.error.HTTPError as error:
    raise SystemExit('Keycloak admin authentication failed. Update AdminUsername/AdminPassword in .env.') from error

def request(path, method='GET', body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + '/admin/realms/forgedock' + path, data=data, method=method,
        headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

alias = 'forgedock-browser-' + factor
forms = 'forgedock-forms-' + factor
authenticator = 'auth-otp-form' if factor == 'totp' else 'webauthn-authenticator'
action = 'CONFIGURE_TOTP' if factor == 'totp' else 'webauthn-register'
flows = {flow['alias'] for flow in request('/authentication/flows')}
if alias not in flows:
    request('/authentication/flows', 'POST', {
        'alias': alias, 'description': 'ForgeDock password plus required ' + factor,
        'providerId': 'basic-flow', 'topLevel': True, 'builtIn': False,
    })
    request('/authentication/flows/' + alias + '/executions/execution', 'POST', {'provider': 'auth-cookie'})
    request('/authentication/flows/' + alias + '/executions/flow', 'POST', {
        'alias': forms, 'type': 'basic-flow', 'provider': 'basic-flow',
        'description': 'Required password and second factor',
    })
    for provider in ('auth-username-password-form', authenticator):
        request('/authentication/flows/' + forms + '/executions/execution', 'POST', {'provider': provider})
# Apply only the selected managed flow, preserving every other flow and credential.
for execution in request('/authentication/flows/' + alias + '/executions'):
    requirement = 'ALTERNATIVE' if execution.get('providerId') == 'auth-cookie' or execution.get('authenticationFlow') else 'REQUIRED'
    request('/authentication/flows/' + alias + '/executions', 'PUT', {'id': execution['id'], 'requirement': requirement})
actions = {item['alias'] for item in request('/authentication/required-actions')}
representation = {'alias': action, 'name': 'Configure OTP' if factor == 'totp' else 'Webauthn Register',
    'providerId': action, 'enabled': True, 'defaultAction': False, 'priority': 40}
if action not in actions:
    request('/authentication/register-required-action', 'POST', representation)
else:
    request('/authentication/required-actions/' + action, 'PUT', representation)
realm = request('')
realm.update(loginTheme='forgedock', browserFlow=alias, otpPolicyType='totp', otpPolicyAlgorithm='HmacSHA1',
    otpPolicyDigits=6, otpPolicyPeriod=30, otpPolicyLookAheadWindow=1)
request('', 'PUT', realm)
# Migrate the configured operator's pending enrollment without changing their password,
# deleting authenticators, or altering unrelated required actions.
name = os.environ['ForgeDock__Keycloak__OperatorUsername']
users = request('/users?username=' + urllib.parse.quote(name) + '&exact=true')
if len(users) != 1:
    raise SystemExit('Expected exactly one configured local operator.')
user = users[0]
credentials = request('/users/' + user['id'] + '/credentials')
credential_type = 'otp' if factor == 'totp' else 'webauthn'
required = [item for item in user.get('requiredActions', []) if item not in ('CONFIGURE_TOTP', 'webauthn-register')]
if not any(item['type'] == credential_type for item in credentials):
    required.append(action)
user['requiredActions'] = required
request('/users/' + user['id'], 'PUT', user)
print('Keycloak second factor configured: ' + factor + '. Existing passwords and credentials preserved.')
