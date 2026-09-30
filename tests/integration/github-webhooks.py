#!/usr/bin/env python3
"""Exercise webhook endpoints with an isolated PostgreSQL database and API (no worker).
Run: bash scripts/with-env.sh python3 tests/integration/github-webhooks.py
Requires local forgedock-postgres, Docker, and the built API.
"""
import concurrent.futures
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
DB = 'forgedock_webhooks_test_' + uuid.uuid4().hex
TOKEN = os.environ['ForgeDock__ApiToken']
connection = re.sub(r'(?i)Database=[^;]*', 'Database=' + DB, os.environ['ConnectionStrings__ForgeDock'])
assert DB in connection
with socket.socket() as port_socket:
    port_socket.bind(('127.0.0.1', 0))
    port = port_socket.getsockname()[1]
BASE = f'http://127.0.0.1:{port}/api'
env = {**os.environ, 'ConnectionStrings__ForgeDock': connection, 'ASPNETCORE_URLS': f'http://127.0.0.1:{port}', 'ForgeDock__WebhookBaseUrl': 'https://hooks.example.com'}
process = None

def sql(statement):
    subprocess.run(['docker', 'exec', 'forgedock-postgres', 'psql', '-U', 'forgedock', '-d', 'postgres', '-c', statement], check=True, stdout=subprocess.DEVNULL)

def call(path, body=None, method=None, auth=True, headers=None):
    actual_headers = {'Content-Type': 'application/json', **(headers or {})}
    if auth:
        actual_headers['Authorization'] = 'Bearer ' + TOKEN
    data = body if isinstance(body, bytes) else json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, headers=actual_headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, json.loads(response.read() or '{}')
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or '{}')

def start_api():
    server = subprocess.Popen(['dotnet', 'src/ForgeDock.Api/bin/Debug/net10.0/ForgeDock.Api.dll'], cwd=ROOT, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(100):
        try:
            call('/session'); return server
        except urllib.error.URLError:
            time.sleep(.1)
    server.terminate(); server.wait(timeout=5)
    raise AssertionError('Test API failed to start')

try:
    sql(f'CREATE DATABASE "{DB}"')
    subprocess.run(['dotnet', 'ef', 'database', 'update', '--no-build', '--project', 'src/ForgeDock.Infrastructure', '--startup-project', 'src/ForgeDock.Api'], cwd=ROOT, env=env, check=True, stdout=subprocess.DEVNULL)
    process = start_api()
    code, project = call('/projects', {'name': 'Webhook test', 'repositoryUrl': 'https://github.com/example/private.git', 'branch': 'main', 'deploymentMode': 'Auto'})
    assert code == 201
    project_id = project['id']
    settings_path = '/projects/' + project_id + '/webhook'
    hook_path = '/webhooks/github/' + project_id
    assert call(settings_path, {'enabled': True}, 'PUT', auth=False)[0] == 401
    assert call(settings_path)[1]['enabled'] is False
    code, config = call(settings_path, {'enabled': True}, 'PUT')
    assert code == 200 and config['enabled'] and len(config['secret']) == 64
    secret = config['secret']
    assert config['publicUrl'] == 'https://hooks.example.com/api' + hook_path
    assert call(settings_path)[1]['secret'] is None
    assert call(settings_path, {'enabled': True}, 'PUT')[1]['secret'] is None
    assert call('/projects/' + project_id + '/environment/TEST_VALUE', {'value': 'snapshot-value'}, 'PUT')[0] == 204

    def deliver(payload, event='push', delivery=None, signed_with=None, wrong_signature=False, content_type='application/json'):
        body = payload if isinstance(payload, bytes) else json.dumps(payload, ensure_ascii=False).encode()
        signature = hmac.new((signed_with or secret).encode(), body, hashlib.sha256).hexdigest()
        return call(hook_path, body, auth=False, headers={'X-GitHub-Event': event, 'X-GitHub-Delivery': delivery or str(uuid.uuid4()),
            'X-Hub-Signature-256': 'sha256=' + ('0' * 64 if wrong_signature else signature), 'Content-Type': content_type})

    push = {'ref': 'refs/heads/main', 'after': 'b' * 40, 'deleted': False, 'repository': {'html_url': 'https://github.com/example/private'}, 'head_commit': {'message': 'Ship 🚀'}}
    assert deliver(push, wrong_signature=True)[0] == 401
    assert deliver(push, content_type='application/x-www-form-urlencoded')[0] == 415
    assert deliver(b'{malformed')[0] == 400
    assert deliver(push, delivery='invalid-delivery-id')[0] == 400
    assert deliver({**push, 'after': 'bad-sha'})[0] == 400
    assert deliver(b'x' * (2 * 1024 * 1024 + 1))[0] == 413
    assert deliver({'zen': 'test'}, event='ping')[1]['status'] == 'Ping'
    assert deliver(push, event='issues')[1]['status'] == 'IgnoredEvent'
    assert deliver({**push, 'ref': 'refs/heads/other'})[1]['status'] == 'IgnoredBranch'
    assert deliver({**push, 'ref': 'refs/tags/main'})[1]['status'] == 'IgnoredBranch'
    assert deliver({**push, 'deleted': True})[1]['status'] == 'IgnoredDeletedRef'
    assert deliver({**push, 'repository': {'html_url': 'https://github.com/other/private'}})[1]['status'] == 'IgnoredRepository'
    assert len(call('/projects/' + project_id + '/deployments')[1]) == 0
    delivery = str(uuid.uuid4())
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        results = list(executor.map(lambda _: deliver(push, delivery=delivery), range(8)))
    assert all(code == 200 for code, _ in results), results
    assert sum(result['status'] == 'Queued' for _, result in results) == 1
    assert sum(result['status'] == 'Duplicate' for _, result in results) == 7
    deployments = call('/projects/' + project_id + '/deployments')[1]
    assert len(deployments) == 1 and deployments[0]['trigger'] == 'GitHubPush'
    # The request snapshot and exact commit are verified directly in the isolated database.
    inspection = subprocess.check_output(['docker', 'exec', 'forgedock-postgres', 'psql', '-U', 'forgedock', '-d', DB, '-tA', '-c',
        'SELECT "RequestedCommit", "ConfigurationJson"::jsonb->>\'Branch\', (SELECT count(*) FROM jsonb_object_keys("ConfigurationJson"::jsonb->\'ProtectedEnvironment\')) FROM "Deployments"'], text=True).strip()
    assert inspection == 'b' * 40 + '|main|1', inspection
    # Durable receipts remain idempotent after restarting the API.
    process.terminate(); process.wait(timeout=5)
    process = start_api()
    assert deliver(push, delivery=delivery)[1]['status'] == 'Duplicate'
    # Disabling and rotation do not expose persisted secrets or enqueue ignored pushes.
    assert call(settings_path, {'enabled': False}, 'PUT')[0] == 200
    assert deliver(push)[1]['status'] == 'IgnoredDisabled'
    _, rotated = call(settings_path + '/rotate-secret', {}, 'POST')
    old_secret = secret; secret = rotated['secret']
    assert secret != old_secret
    assert deliver(push, signed_with=old_secret)[0] == 401
    assert call(settings_path)[1]['secret'] is None
    assert call(settings_path, {'enabled': True}, 'PUT')[0] == 200
    assert deliver(push, delivery=delivery)[1]['status'] == 'Duplicate'
    assert call(settings_path)[1]['lastDelivery']['status'] == 'IgnoredDisabled'
    # A pending stop blocks queueing, and its rejected delivery remains retryable.
    assert call('/deployments/' + deployments[0]['id'] + '/cancel', {})[0] == 200
    assert call('/projects/' + project_id + '/operations', {'kind': 'Stop'})[0] == 202
    retry_delivery = str(uuid.uuid4())
    assert deliver(push, delivery=retry_delivery)[0] == 409
    assert len(call('/projects/' + project_id + '/deployments')[1]) == 1
    subprocess.run(['docker', 'exec', 'forgedock-postgres', 'psql', '-U', 'forgedock', '-d', DB, '-c',
        'UPDATE "Operations" SET "State" = \'Completed\''], check=True, stdout=subprocess.DEVNULL)
    assert deliver(push, delivery=retry_delivery)[1]['status'] == 'Queued'
    # Notification settings never expose stored webhooks and reject arbitrary destinations.
    notification_path = f'/projects/{project_id}/notifications'
    assert call(notification_path, {'onSuccess': True, 'onFailure': True, 'slackUrl': 'https://localhost/hook'}, 'PUT')[0] == 400
    assert call(notification_path, {'onSuccess': True, 'onFailure': True, 'slackUrl': 'https://hooks.slack.com/services/T1/B2/secret', 'discordUrl': 'https://discord.com/api/webhooks/123/secret', 'email': 'operator@example.com'}, 'PUT')[0] == 204
    config = call(notification_path)[1]
    assert config['slackConfigured'] and config['discordConfigured'] and 'secret' not in json.dumps(config)
    assert call(notification_path, {'onSuccess': False, 'onFailure': True}, 'PUT')[0] == 204
    assert call(notification_path)[1]['slackConfigured']
    # Managed services are unique and atomically add a protected connection variable.
    database_path = f'/projects/{project_id}/databases'
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        results = list(executor.map(lambda _: call(database_path, {'kind': 'PostgreSql'}), range(4)))
    assert sum(code == 202 for code, _ in results) == 1 and sum(code == 409 for code, _ in results) == 3, results
    database = call(database_path)[1][0]
    assert 'protectedPassword' not in database
    assert 'DATABASE_URL' in call(f'/projects/{project_id}/environment')[1]
    assert call(f"{database_path}/{database['id']}/schedule", {'intervalHours': 24}, 'PUT')[0] == 204
    assert call(f"{database_path}/{database['id']}/schedule", {'intervalHours': 169}, 'PUT')[0] == 400
    assert call(database_path)[1][0]['nextBackupAt'] is not None
    # Independent preview DB and environment; exact SHA; concurrent receipt dedup; stale close handling.
    assert call(f'/projects/{project_id}/previews', {'enabled': True}, 'PUT')[0] == 204
    pr = {'action': 'opened', 'number': 17, 'repository': {'html_url': 'https://github.com/example/private'},
          'pull_request': {'updated_at': '2026-10-01T00:00:00Z', 'draft': False, 'base': {'ref': 'main'},
                           'head': {'ref': 'feature/test', 'sha': 'c' * 40, 'repo': {'html_url': 'https://github.com/example/private'}}}}
    preview_delivery = str(uuid.uuid4())
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        results = list(executor.map(lambda _: deliver(pr, event='pull_request', delivery=preview_delivery), range(4)))
    assert all(code == 200 for code, _ in results), results
    assert sum(result['status'] == 'PreviewQueued' for _, result in results) == 1
    preview = call(f'/projects/{project_id}/previews')[1]['previews'][0]
    assert preview['branch'] == 'feature/test'
    variables = call(f"/projects/{preview['id']}/environment")[1]
    assert variables == ['DATABASE_URL'], variables
    assert call(f"/projects/{preview['id']}/databases")[1][0]['id'] != database['id']
    preview_deployments = call(f"/projects/{preview['id']}/deployments")[1]
    assert len(preview_deployments) == 1 and preview_deployments[0]['trigger'] == 'GitHubPullRequest'
    fork = json.loads(json.dumps(pr)); fork['pull_request']['head']['repo']['html_url'] = 'https://github.com/fork/private'
    assert deliver(fork, event='pull_request')[1]['status'] == 'IgnoredFork'
    closed = {**pr, 'action': 'closed', 'pull_request': {**pr['pull_request'], 'updated_at': '2026-10-01T00:01:00Z'}}
    assert deliver(closed, event='pull_request')[1]['status'] == 'PreviewClosed'
    assert call(f"/projects/{preview['id']}/deployments")[1][0]['state'] == 'Cancelled'
    assert deliver(pr, event='pull_request')[1]['status'] == 'IgnoredStalePreview'
    # Backup jobs are project-scoped, require running DB, and require restore confirmation.
    for deployment in call(f'/projects/{project_id}/deployments')[1]:
        if deployment['state'] == 'Queued': assert call('/deployments/' + deployment['id'] + '/cancel', {})[0] == 200
    backup_path = f"{database_path}/{database['id']}/backups"
    assert call(backup_path, {})[0] == 409
    subprocess.run(['docker', 'exec', 'forgedock-postgres', 'psql', '-U', 'forgedock', '-d', DB, '-c',
        'UPDATE "DatabaseServices" SET "State" = \'Running\' WHERE "Id" = \'%s\'' % database['id']], check=True, stdout=subprocess.DEVNULL)
    code, backup = call(backup_path, {})
    assert code == 202 and backup['kind'] == 'Backup'
    assert call(backup_path, {})[0] == 409
    subprocess.run(['docker', 'exec', 'forgedock-postgres', 'psql', '-U', 'forgedock', '-d', DB, '-c',
        'UPDATE "DatabaseBackups" SET "State" = \'Completed\' WHERE "Id" = \'%s\'' % backup['id']], check=True, stdout=subprocess.DEVNULL)
    assert call(f"/projects/{project_id}/backups/{backup['id']}/restore", {'confirm': False})[0] == 400
    assert call(f"/projects/{preview['id']}/backups/{backup['id']}/restore", {'confirm': True})[0] == 404
    assert call(f"/projects/{project_id}/backups/{backup['id']}/restore", {'confirm': True})[0] == 202
    print('Notification, database, backup and PR preview API integration checks passed.')
    print('GitHub webhook integration checks passed: signature, filters, ping, concurrent deduplication, pinned commit, snapshot, disable, rotation, and operation conflict.')
finally:
    if process:
        process.terminate()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
    sql(f'DROP DATABASE IF EXISTS "{DB}" WITH (FORCE)')
