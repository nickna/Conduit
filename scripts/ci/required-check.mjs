import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function validateJobs(required, needs) {
  if (!Array.isArray(required) || required.length === 0) throw new Error('Required job inventory is empty');
  const failures = required.filter(id => needs[id]?.result !== 'success')
    .map(id => `${id}: ${needs[id]?.result ?? 'missing'}`);
  if (failures.length) throw new Error(`Required validation did not succeed:\n${failures.join('\n')}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  validateJobs(JSON.parse(readFileSync(new URL('./required-jobs.json', import.meta.url))),
    JSON.parse(process.env.CI_NEEDS));
  console.log('All required validation jobs succeeded.');
}
