import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
const pins = JSON.parse(readFileSync(new URL('./action-versions.json', import.meta.url)));
test('every external action is pinned to a reviewed immutable revision', () => {
  for (const file of readdirSync('.github/workflows').filter(path => path.endsWith('.yml'))) {
    const content = readFileSync(`.github/workflows/${file}`, 'utf8');
    for (const [, repository, , revision] of content.matchAll(/uses:\s*([\w.-]+\/[\w.-]+)(\/[\w/-]+)?@([^\s#]+)/g)) {
      assert.match(revision, /^[a-f0-9]{40}$/, `${file}: ${repository}`);
      assert.equal(revision, pins[repository]?.revision, `${file}: unreviewed ${repository}`);
    }
    assert.match(content, /permissions:\s*\n\s+contents: read/, `${file}: missing read-only default`);
  }
});
