// Serves the production build (dist/opportunity-web/browser) the way deploy/docker/web/nginx.conf does: SPA fallback
// to index.html, the CSP nonce written into index.html, and the security headers from security-headers.conf, so the
// browser tests see what ships (including CSP violations). /api and /bff are not served: the tests mock them.
// Usage: node e2e/support/serve.mjs [port]
import { randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));
const root = resolve(here, '../../dist/opportunity-web/browser');
const headersConf = resolve(here, '../../../../deploy/docker/web/security-headers.conf');
const port = Number(process.argv[2] ?? process.env.E2E_PORT ?? 4300);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.json': 'application/json',
};

/** `add_header Name "value" always;` lines; `$request_id` is the per-response nonce. */
const securityHeaders = [
  ...(await readFile(headersConf, 'utf8')).matchAll(/^add_header\s+(\S+)\s+"([^"]*)"/gm),
].map(([, name, value]) => [name, value]);
const indexHtml = await readFile(join(root, 'index.html'), 'utf8');

createServer(async (req, res) => {
  const nonce = randomBytes(16).toString('hex');
  for (const [name, value] of securityHeaders) {
    // HSTS on http://127.0.0.1 is ignored by browsers; keep it anyway to mirror nginx.
    res.setHeader(name, value.replaceAll('$request_id', nonce));
  }
  const path = decodeURIComponent(new URL(req.url ?? '/', 'http://localhost').pathname);
  if (path.startsWith('/api/') || path.startsWith('/bff/')) {
    res
      .writeHead(502, { 'content-type': 'text/plain' })
      .end('API is not served; mock it in the test');
    return;
  }
  const file = normalize(join(root, path));
  if (file.startsWith(root + sep) && extname(file)) {
    try {
      const body = await readFile(file);
      res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
      res.end(body);
      return;
    } catch {
      res.writeHead(404, { 'content-type': 'text/plain' }).end('Not found');
      return;
    }
  }
  res.writeHead(200, { 'content-type': types['.html'], 'cache-control': 'no-cache' });
  res.end(indexHtml.replaceAll('__CSP_NONCE__', nonce));
}).listen(port, '127.0.0.1', () => console.log(`Serving ${root} on http://127.0.0.1:${port}`));
