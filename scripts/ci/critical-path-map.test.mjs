import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

test('all 30 legacy critical-path facts have explicit execution ownership', () => {
  const directory = 'Tests/ConduitLLM.IntegrationTests/Tests/CriticalPath';
  const methods = readdirSync(directory).filter(file => file.endsWith('.cs')).flatMap(file =>
    [...readFileSync(`${directory}/${file}`, 'utf8').matchAll(/public async Task (\w+)\(/g)].map(match => match[1]));
  const map = JSON.parse(readFileSync('scripts/ci/critical-path-map.json'));
  assert.equal(methods.length, 30);
  assert.deepEqual(Object.keys(map).sort(), methods.sort());
  for (const [method, entry] of Object.entries(map)) {
    assert.ok(entry.owner, method);
    assert.ok(['required', 'manual'].includes(entry.lane), method);
    if (entry.lane === 'manual') assert.ok(entry.reason?.length > 40, `${method}: missing manual justification`);
  }
});
