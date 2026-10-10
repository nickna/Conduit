import { readdirSync, readFileSync } from 'node:fs';
import { parseWorkflow, validateWorkflows } from './workflow-policy.mjs';
const workflows = Object.fromEntries(readdirSync('.github/workflows').filter(f => /\.ya?ml$/.test(f))
  .map(file => [file, parseWorkflow(readFileSync(`.github/workflows/${file}`, 'utf8'))]));
console.log(validateWorkflows(workflows, JSON.parse(readFileSync('scripts/ci/required-jobs.json', 'utf8'))));
