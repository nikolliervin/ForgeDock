const http = require('node:http');
const net = require('node:net');
const fs = require('node:fs');
const version = fs.readFileSync('/app/version.txt', 'utf8').trim();
function redis(command) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: 'store', port: 6379 });
    socket.setTimeout(2000, () => socket.destroy(new Error('Redis timeout')));
    socket.on('error', reject);
    socket.on('connect', () => socket.write(`*${command.length}\r\n` + command.map(value => `$${Buffer.byteLength(value)}\r\n${value}\r\n`).join('')));
    let output = '';
    socket.on('data', data => {
      output += data.toString();
      if (!output.includes('\r\n')) return;
      socket.end();
      if (output.startsWith('-')) reject(new Error(output));
      else resolve(output.slice(1).split('\r\n')[0]);
    });
  });
}
http.createServer(async (request, response) => {
  try {
    if (request.url === '/health') { if (!fs.existsSync('/run/secrets/fixture-secret')) throw new Error('Repository secret mount is missing'); await redis(['PING']); response.end('healthy'); return; }
    const count = await redis(['INCR', 'requests']);
    const label = String(process.env.DEMO_LABEL || 'Compose demo').replace(/[&<>"']/g, '');
    response.setHeader('Content-Type', 'text/html');
    response.end(`<!doctype html><html lang="en"><title>ForgeDock Compose demo</title><h1>${label}: ${version}</h1><p>Requests stored in Redis: ${count}</p></html>`);
  } catch (error) { response.statusCode = 503; response.end('Store is unavailable'); console.error(error.message); }
}).listen(3000, '0.0.0.0', () => console.log(`Web service started: ${version}`));
