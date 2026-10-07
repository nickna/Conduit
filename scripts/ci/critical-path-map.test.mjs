import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

test('all 30 legacy critical-path facts have explicit execution ownership', () => {
  const directory = 'Tests/ConduitLLM.IntegrationTests/Tests/CriticalPath';
  const sources = readdirSync(directory).filter(file => file.endsWith('.cs')).map(file =>
    readFileSync(`${directory}/${file}`, 'utf8'));
  const methods = sources.flatMap(source =>
    [...source.matchAll(/public void (\w+)\(/g)].map(match => match[1]));
  const map = JSON.parse(readFileSync('scripts/ci/critical-path-map.json'));
  assert.equal(methods.length, 30);
  assert.deepEqual(Object.keys(map).sort(), methods.sort());
  const facts = sources.flatMap(source => [...source.matchAll(/\[Fact\(([^\r\n]*)\)\]/g)]);
  assert.equal(facts.length, methods.length);
  for (const fact of facts) {
    assert.match(fact[1], /Skip = "Retired external harness \(#1464\)\./,
      'retired external facts must be reported skipped, never passed');
  }
  for (const source of sources) {
    assert.doesNotMatch(source, /IClassFixture|ICollectionFixture|\[Collection\(|CriticalPathTestBase/,
      'retirement records must not initialize provider or Docker fixtures');
    assert.equal([...source.matchAll(/throw new InvalidOperationException\("Retired harness:/g)].length,
      [...source.matchAll(/\[Fact\(/g)].length, 'retired records must fail if accidentally re-enabled');
  }
  for (const [method, entry] of Object.entries(map)) {
    assert.ok(entry.owner, method);
    assert.ok(['required', 'manual'].includes(entry.lane), method);
    if (entry.lane === 'manual') assert.ok(entry.reason?.length > 40, `${method}: missing manual justification`);
  }
});
