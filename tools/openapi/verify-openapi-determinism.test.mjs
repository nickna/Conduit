import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { compareGeneratedArtifacts } from './verify-openapi-determinism.mjs';

for (const scenario of [
  { name: 'matches committed artifacts', committed: 'current', first: 'current', second: 'current', unstable: [], stale: [] },
  { name: 'rejects deterministic drift', committed: 'old', first: 'new', second: 'new', unstable: [], stale: ['contract.json'] },
  { name: 'rejects nondeterminism', committed: 'current', first: 'current', second: 'different', unstable: ['contract.json'], stale: [] },
  { name: 'rejects missing committed artifacts', first: 'current', second: 'current', unstable: [], stale: ['contract.json'] },
]) {
  test(scenario.name, () => {
    const directory = mkdtempSync(path.join(tmpdir(), 'conduit-openapi-compare-'));
    try {
      const destination = path.join(directory, 'contract.json');
      const firstFile = path.join(directory, 'first.json');
      const secondFile = path.join(directory, 'second.json');
      if (scenario.committed !== undefined) writeFileSync(destination, scenario.committed);
      writeFileSync(firstFile, scenario.first);
      writeFileSync(secondFile, scenario.second);

      assert.deepEqual(compareGeneratedArtifacts(directory, ['contract.json'],
        new Map([[destination, firstFile]]), new Map([[destination, secondFile]])),
      { unstable: scenario.unstable, stale: scenario.stale });
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });
}
