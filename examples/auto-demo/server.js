const http = require('node:http');
const port = Number(process.env.PORT || 8080);
http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify({ app: 'ForgeDock Auto demo', status: 'healthy' }));
}).listen(port, '0.0.0.0');
