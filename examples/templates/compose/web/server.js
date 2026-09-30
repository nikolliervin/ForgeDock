const http = require('node:http');
http.createServer((request, response) => response.end(request.url === '/health' ? 'ok' : 'Hello from ForgeDock Compose')).listen(Number(process.env.PORT || 8080), '0.0.0.0');
