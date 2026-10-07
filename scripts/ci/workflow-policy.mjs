import { createRequire } from 'node:module';
const { parseDocument } = createRequire(new URL('../../tools/ci/package.json', import.meta.url))('yaml');
export function parseWorkflow(source) {
  const document = parseDocument(source, { uniqueKeys: true });
  if (document.errors.length) throw new Error(document.errors.map(e => e.message).join('\n'));
  return document.toJS();
}
const array = value => Array.isArray(value) ? value : value ? [value] : [];
function require(condition, message) { if (!condition) throw new Error(message); }
export function validateWorkflows(workflows, required) {
  for (const [name, workflow] of Object.entries(workflows)) {
    require(workflow.name && workflow.on && workflow.jobs, `${name}: missing workflow structure`);
    require(workflow.permissions?.contents === 'read', `${name}: default token must read contents`);
    for (const [id, job] of Object.entries(workflow.jobs)) {
      if (!job.uses) require(Number.isInteger(job['timeout-minutes']) && job['timeout-minutes'] > 0 && job['timeout-minutes'] <= 90,
        `${name}/${id}: missing bounded timeout`);
    }
  }
  const ci = workflows['ci.yml'];
  for (const event of ['push', 'pull_request']) {
    const trigger = ci.on[event];
    require(['master', 'dev'].every(b => trigger?.branches?.includes(b)) && !trigger.paths && !trigger['paths-ignore'],
      `CI must validate all ${event} changes on master/dev`);
  }
  const gate = ci.jobs.required;
  require(gate.name === 'CI required' && gate.if === '${{ always() }}', 'Aggregate must always report with its protected name');
  require(JSON.stringify([...array(gate.needs)].sort()) === JSON.stringify([...required].sort()) && required.every(id => ci.jobs[id]),
    'Aggregate dependency graph differs from required inventory');
  const release = workflows['release.yml'];
  const production = release.jobs.production, candidates = release.jobs.candidates;
  require(production?.concurrency?.group === 'conduit-production-release' && production.concurrency['cancel-in-progress'] === false,
    'Production must serialize across tags without cancelling a running release');
  require(production.environment === 'production' && !production.if && array(production.needs).includes('candidates') &&
    array(production.needs).includes('metadata'), 'Production must require successful validated candidates and exact-SHA metadata');
  require(!candidates.if && ['metadata', 'docker'].every(id => array(candidates.needs).includes(id)), 'Candidate proof must require complete builds');
  const commands = production.steps.map(s => s.run ?? '').join('\n');
  require(commands.includes('release-images.mjs preflight') && commands.includes('release-images.mjs migrated') &&
    commands.includes('release-images.mjs promote') && commands.includes('i.image+"@"+i.digest'), 'Migration/promotion must use the recorded digest set');
  const steps = production.steps;
  const index = term => steps.findIndex(s => s.run?.includes(term));
  require(index(' preflight ') < index('/app/migrator/') && index('/app/migrator/') < index(' promote '), 'Preflight, migration, promotion ordering is unsafe');
  require(!steps.some(s => /preflight|promote|migrator/.test(s.run ?? '') && s['continue-on-error']), 'Production gates cannot ignore errors');
  const scans = candidates.steps.filter(s => s.uses?.startsWith('aquasecurity/trivy-action@'));
  require(scans.length === 3 && scans.every(s => /^\$\{\{ steps\.images\.outputs\.(admin|http|webadmin) \}\}$/.test(s.with['image-ref'])),
    'Scan all three immutable candidate identities');
  require(candidates.steps.some(s => s.run?.includes('security-check.mjs image')), 'Candidate security policy must block production');
  for (const file of ['migration-validation.yml', 'distributed-lock.yml', 'codeql-analysis.yml']) {
    const workflow = workflows[file];
    require(workflow.on.workflow_call !== undefined, `${file}: required reusable entrypoint missing`);
    require(workflow.concurrency?.group && !workflow.concurrency.group.includes('github.workflow'), `${file}: reusable concurrency collides with its caller`);
  }
  return { workflows: Object.keys(workflows).length, requiredJobs: required.length };
}
