import { createServer } from 'node:http';
import { appendFileSync, mkdirSync, readFileSync } from 'node:fs';

const host = '127.0.0.1';
const port = Number(process.env.SEB_LAB_PORT || 8765);
const origin = `http://${host}:${port}`;
const logs = new URL('../../artifacts/', import.meta.url);
const logFile = new URL(port === 8765 ? 'seb-quiz-lab.ndjson' : `seb-quiz-lab-${port}.ndjson`, logs);
mkdirSync(logs, { recursive: true });

const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/lab.css', ['lab.css', 'text/css; charset=utf-8']],
  ['/lab.js', ['lab.js', 'text/javascript; charset=utf-8']]
]);
const types = new Set(['started', 'viewed', 'answered', 'stopped', 'resumed', 'submitted', 'shortcut']);
const signals = new Set(['blur', 'focus', 'visibilitychange', 'pagehide', 'pageshow']);
const shortcuts = new Set(['Ctrl + Shift + J', 'Ctrl + Shift + K', 'Ctrl + Shift + H']);

function send(response, status, contentType, body = '') {
  response.writeHead(status, {
    'Content-Type': contentType,
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'Content-Security-Policy': "default-src 'self'; frame-ancestors 'none'; base-uri 'none'"
  });
  response.end(body);
}

function accept(entry) {
  if (!entry || typeof entry.attempt !== 'string' || !/^[a-f0-9-]{36}$/i.test(entry.attempt) ||
      !types.has(entry.type) || typeof entry.time !== 'string' || !Number.isFinite(Date.parse(entry.time))) {
    return null;
  }
  const { attempt, type, time } = entry;
  const event = { attempt, type, time: new Date(time).toISOString(), received: new Date().toISOString() };
  if (type === 'viewed' || type === 'answered') {
    if (![1, 2, 3].includes(entry.question)) return null;
    event.question = entry.question;
  }
  if (type === 'stopped' || type === 'resumed') {
    if (!signals.has(entry.signal)) return null;
    event.signal = entry.signal;
  }
  if (type === 'shortcut') {
    if (!shortcuts.has(entry.shortcut)) return null;
    event.shortcut = entry.shortcut;
  }
  return event;
}

createServer(async (request, response) => {
  if (request.headers.host !== `${host}:${port}`) return send(response, 403, 'text/plain');
  try {
    if (request.method === 'POST' && request.url === '/events') {
      if (request.headers.origin !== origin || request.headers['content-type'] !== 'application/json') {
        return send(response, 403, 'text/plain');
      }
      let body = '';
      for await (const chunk of request) {
        body += chunk;
        if (body.length > 2048) return send(response, 413, 'text/plain');
      }
      let event;
      try { event = accept(JSON.parse(body)); } catch { /* Malformed input. */ }
      if (!event) return send(response, 400, 'text/plain');
      appendFileSync(logFile, JSON.stringify(event) + '\n', 'utf8');
      return send(response, 204, 'text/plain');
    }
    if (request.method === 'GET' && request.url === '/events') {
      let data = '';
      try { data = readFileSync(logFile, 'utf8'); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      return send(response, 200, 'text/plain; charset=utf-8', data);
    }
    if (request.method === 'GET' && files.has(request.url)) {
      const [file, contentType] = files.get(request.url);
      return send(response, 200, contentType, readFileSync(new URL(`dist/${file}`, import.meta.url)));
    }
    return send(response, 404, 'text/plain', 'Not found');
  } catch {
    return send(response, 500, 'text/plain', 'Local lab unavailable');
  }
}).listen(port, host, () => console.log(`SEB Lab: ${origin}/\nLog: ${origin}/events`));
