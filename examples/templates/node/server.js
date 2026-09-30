const http = require('node:http');
http.createServer((request, response) => {
  response.setHeader('Content-Type', 'application/json');
  response.end(JSON.stringify(request.url === '/health' ? { status: 'ok' } : { message: 'Hello from ForgeDock' }));
}).listen(Number(process.env.PORT || 8080), '0.0.0.0');
