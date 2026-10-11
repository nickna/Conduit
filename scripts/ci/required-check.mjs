import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { comparisonFromEvent, validatePlan } from './change-impact.mjs';

export function validateJobs(required, needs, plan, event, comparison) {
  if (!Array.isArray(required) || required.length === 0) throw new Error('Required job inventory is empty');
  if (!needs || typeof needs !== 'object' || Array.isArray(needs)) throw new Error('Missing validation results');
  // Callers without a plan retain the original success-only contract. The CI
  // entrypoint always requires a validated plan and successful selector.
  if (plan !== undefined) validatePlan(required, plan, event, comparison);
  const failures = required.filter(id => {
    const result = needs[id]?.result;
    return plan?.jobs[id] === false ? !['success', 'skipped'].includes(result) : result !== 'success';
  })
    .map(id => `${id}: ${needs[id]?.result ?? 'missing'}`);
  if (failures.length) throw new Error(`Required validation did not succeed:\n${failures.join('\n')}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const plan = JSON.parse(process.env.CI_PLAN);
  const event = process.env.GITHUB_EVENT_NAME;
  if (!event) throw new Error('Missing current CI event');
  const comparison = event === 'pull_request' && plan.mode !== 'full'
    ? comparisonFromEvent(JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))) : undefined;
  validateJobs(JSON.parse(readFileSync(new URL('./required-jobs.json', import.meta.url))),
    JSON.parse(process.env.CI_NEEDS), plan, event, comparison);
  console.log(`All selected validation jobs succeeded (${plan.mode}); excluded jobs were explicitly verified.`);
}
