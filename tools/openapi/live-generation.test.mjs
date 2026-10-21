import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptsDir, '../..');
const { scripts } = JSON.parse(readFileSync(path.join(scriptsDir, 'package.json'), 'utf8'));
const require = createRequire(import.meta.url);
const cli = path.join(path.dirname(require.resolve('openapi-typescript/package.json')), 'bin/cli.js');

test('the combined live alias generates types from both hosts', () => {
  assert.equal(scripts.generate, 'npm run generate:admin && npm run generate:gateway');
});

for (const [service, port, middleware] of [
  ['admin', 5002, 'Services/ConduitLLM.Admin/Program.cs'],
  ['gateway', 5000, 'Services/ConduitLLM.Gateway/Program.Middleware.cs'],
]) {
  test(`${service} live alias fetches the development route and generates only WebAdmin types`, async () => {
    const command = scripts[`generate:${service}`].match(/^openapi-typescript (\S+) -o (\S+)$/);
    assert(command, 'Expected a live openapi-typescript invocation');
    const source = new URL(command[1]);
    assert.equal(source.origin, `http://localhost:${port}`);
    assert.equal(path.resolve(scriptsDir, command[2]),
      path.join(repoRoot, 'WebAdmin/src/generated', `${service}-api.ts`));

    const route = readFileSync(path.join(repoRoot, middleware), 'utf8').match(/app\.MapOpenApi\("([^"]+)"\)/)?.[1];
    assert(route, 'Expected the service to map its development OpenAPI route');
    const requests = [];
    const server = createServer((request, response) => {
      requests.push(request.url);
      if (request.url !== route) {
        response.writeHead(404).end();
        return;
      }
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end(JSON.stringify({
        openapi: '3.1.1',
        info: { title: `${service} development contract`, version: '1' },
        paths: {
          [`/${service}-widgets`]: {
            get: { responses: { 200: { description: 'Widget list' } } },
          },
        },
      }));
    });
    const fixture = mkdtempSync(path.join(tmpdir(), 'conduit-live-openapi-'));
    try {
      server.listen(0, '127.0.0.1');
      await once(server, 'listening');
      // Give each fixture an available local port while retaining the npm command's route.
      source.hostname = '127.0.0.1';
      source.port = String(server.address().port);
      const output = path.join(fixture, `${service}-api.ts`);
      const child = spawn(process.execPath, [cli, source.href, '-o', output], {
        cwd: scriptsDir, stdio: ['ignore', 'pipe', 'pipe'], timeout: 20_000,
      });
      let diagnostics = '';
      child.stdout.on('data', chunk => { diagnostics += chunk; });
      child.stderr.on('data', chunk => { diagnostics += chunk; });
      const [code, signal] = await once(child, 'close');
      assert.equal(signal, null, diagnostics);
      assert.equal(code, 0, diagnostics);
      assert(requests.includes(route), 'The generator must request the service OpenAPI route');
      assert(readFileSync(output, 'utf8').includes(`"/${service}-widgets"`));
    } finally {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
      assert(fixture.startsWith(path.join(tmpdir(), 'conduit-live-openapi-')));
      rmSync(fixture, { recursive: true, force: true });
    }
  });
}
