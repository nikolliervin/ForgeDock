#!/usr/bin/env python3
"""Opt-in local ACME smoke test. Requires Docker, OpenSSL and Python 3.8+.

Pebble bypasses public DNS validation ONLY inside this isolated test network.
Production ownership validation and certificate settings are not modified.
"""
import subprocess, pathlib, tempfile, time, ssl, json, shutil, uuid
name='forgedock-https-check-'+uuid.uuid4().hex[:8]
root=pathlib.Path(tempfile.mkdtemp(prefix=name))
root.chmod(0o755)
def run(*args): return subprocess.check_output(args,text=True,stderr=subprocess.STDOUT).strip()
containers=[]
try:
 run('docker','network','create',name)
 shutil.copy(pathlib.Path(__file__).resolve().parent.parent/'tests/fixtures/pebble-root.pem',root/'root.pem')
 (root/'Caddyfile').write_text('''{
 admin localhost:2019
 email ops@example.com
 acme_ca https://pebble:14000/dir
 acme_ca_root /etc/caddy/root.pem
}
:80 {
 respond "Not found" 404
}
https://app.example.com {
 reverse_proxy forgedock-proxy:80
}
''')
 (root/'nginx.conf').write_text('''events {}\nhttp { server { listen 80; return 200 "$http_x_forwarded_proto|$host|$request_uri"; } }''')
 for suffix,image,args in [
 ('ca','ghcr.io/letsencrypt/pebble:2.10.1',['--network-alias','pebble','-e','PEBBLE_VA_ALWAYS_VALID=1','-e','PEBBLE_VA_NOSLEEP=1']),
 ('proxy','nginx:alpine',['--network-alias','forgedock-proxy','-v',f'{root}/nginx.conf:/etc/nginx/nginx.conf:ro,z']),
 ('edge','caddy:2.11.4-alpine',['-p','127.0.0.1::443','-p','127.0.0.1::80','-v',f'{root}:/etc/caddy:ro,z'])]:
  container=name+'-'+suffix
  run('docker','run','-d','--name',container,'--network',name,*args,image)
  containers.append(container)
 time.sleep(1)
 if run('docker','inspect','--format','{{.State.Running}}',name+'-edge') != 'true':
  raise RuntimeError(run('docker','logs',name+'-edge'))
 info=json.loads(run('docker','inspect',name+'-edge'))[0]
 port=info['NetworkSettings']['Ports']['443/tcp'][0]['HostPort']
 httpport=info['NetworkSettings']['Ports']['80/tcp'][0]['HostPort']
 context=ssl._create_unverified_context()
 import socket
 for attempt in range(60):
  try:
   with socket.create_connection(('127.0.0.1',int(port)),timeout=2) as sock:
    with context.wrap_socket(sock,server_hostname='app.example.com') as tls:
     certificate=tls.getpeercert(binary_form=True)
     assert certificate
     checked=subprocess.run(['openssl','x509','-noout','-checkhost','app.example.com'],
         input=ssl.DER_cert_to_PEM_cert(certificate),text=True,capture_output=True)
     assert checked.returncode == 0 and 'does match certificate' in checked.stdout, checked.stdout
     validity=subprocess.run(['openssl','x509','-noout','-checkend','3600'],
         input=ssl.DER_cert_to_PEM_cert(certificate),text=True,capture_output=True)
     assert validity.returncode == 0, validity.stdout
     tls.sendall(b'GET /check?value=1 HTTP/1.1\r\nHost: app.example.com\r\nConnection: close\r\n\r\n')
     response=b''
     while chunk:=tls.recv(4096): response+=chunk
     assert b'https|app.example.com|/check?value=1' in response,response
     print('PASS: local ACME issuance, matching SNI, HTTPS request and forwarded headers')
     break
  except (OSError,ssl.SSLError): time.sleep(1)
 else: raise RuntimeError(run('docker','logs',name+'-edge'))
 with socket.create_connection(('127.0.0.1',int(httpport)),timeout=2) as sock:
  sock.sendall(b'GET /check HTTP/1.1\r\nHost: app.example.com\r\nConnection: close\r\n\r\n'); response=sock.recv(4096)
  assert b'308' in response and b'https://app.example.com/check' in response,response
 print('PASS: automatic HTTP to HTTPS redirect')
 with socket.create_connection(('127.0.0.1',int(httpport)),timeout=2) as sock:
  sock.sendall(b'GET / HTTP/1.1\r\nHost: unknown.example.com\r\nConnection: close\r\n\r\n')
  assert b'404' in sock.recv(4096)
 print('PASS: unknown host returns 404')
finally:
 for container in reversed(containers):
  subprocess.run(['docker','rm','-f',container],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 subprocess.run(['docker','network','rm',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 shutil.rmtree(root)
