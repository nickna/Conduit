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
    for (const concurrency of [workflow.concurrency, ...Object.values(workflow.jobs).map(job => job.concurrency)]) {
      if (typeof concurrency !== 'object' || concurrency?.queue === undefined) continue;
      require(['max', 'single'].includes(concurrency.queue) && !(concurrency.queue === 'max' && concurrency['cancel-in-progress'] !== false),
        `${name}: unsupported queue or cancellation combination`);
    }
    for (const [id, job] of Object.entries(workflow.jobs)) {
      if (!job.uses) require(Number.isInteger(job['timeout-minutes']) && job['timeout-minutes'] > 0 && job['timeout-minutes'] <= 90,
        `${name}/${id}: missing bounded timeout`);
    }
  }
  const ci = workflows['ci.yml'];
  const packagedCheckout = array(ci.jobs.docker.steps).filter(step => step.uses?.startsWith('actions/checkout@'));
  require(packagedCheckout.length === 1 && packagedCheckout[0].with?.['persist-credentials'] === false,
    'Packaged builds must not retain checkout credentials for dependency/build steps');
  for (const event of ['push', 'pull_request']) {
    const trigger = ci.on[event];
    require(['master', 'dev'].every(b => trigger?.branches?.includes(b)) && !trigger.paths && !trigger['paths-ignore'],
      `CI must validate all ${event} changes on master/dev`);
  }
  const gate = ci.jobs.required;
  const alwaysJobs = ['select', 'source-ignore-guard', 'dependencies'];
  require(alwaysJobs.every(id => required.includes(id)) && new Set(required).size === required.length,
    'Required inventory must include the selector and unconditional policy guards exactly once');
  require(JSON.stringify(Object.keys(ci.jobs).sort()) === JSON.stringify([...required, 'required'].sort()),
    'Every CI job must belong to the protected validation inventory');
  for (const id of alwaysJobs) {
    require(ci.jobs[id]?.if === undefined && array(ci.jobs[id]?.needs).length === 0,
      `${id}: selection and policy guards must run independently on every change`);
  }
  const selector = ci.jobs.select;
  const selectorCheckout = array(selector.steps).filter(step => step.uses?.startsWith('actions/checkout@'));
  require(selectorCheckout.length === 1 && selectorCheckout[0].with?.['fetch-depth'] === 0 &&
    selector.outputs?.plan === '${{ steps.plan.outputs.plan }}' &&
    array(selector.steps).some(step => step.id === 'plan' && step.run === 'node scripts/ci/change-impact.mjs'),
    'Selector must classify complete Git history and publish its validated plan');
  require(array(selector.steps).some(step => step.uses?.startsWith('actions/upload-artifact@') &&
    step.if === 'always()' && step.with?.path === 'artifacts/ci-selection/plan.json' && step.with?.['if-no-files-found'] === 'error'),
    'Selector must retain its complete plan and selection reasons');
  for (const id of required.filter(id => !alwaysJobs.includes(id))) {
    require(JSON.stringify(array(ci.jobs[id]?.needs)) === JSON.stringify(['select']) &&
      ci.jobs[id]?.if === `\${{ needs.select.result == 'success' && fromJSON(needs.select.outputs.plan).jobs['${id}'] }}`,
      `${id}: validation must use its own selector decision after successful classification`);
  }
  const timingInput = ci.on.workflow_dispatch?.inputs?.measure_previous_scheduling;
  require(timingInput?.type === 'boolean' && timingInput.default === false &&
    ci.jobs['distributed-lock'].with?.repeat_policy_tests === "${{ github.event_name == 'workflow_dispatch' && inputs.measure_previous_scheduling }}",
    'Scheduling comparison must be opt-in and manual without changing ordinary validation');
  require(gate.name === 'CI required' && gate.if === '${{ always() }}', 'Aggregate must always report with its protected name');
  require(JSON.stringify([...array(gate.needs)].sort()) === JSON.stringify([...required].sort()) && required.every(id => ci.jobs[id]),
    'Aggregate dependency graph differs from required inventory');
  require(array(gate.steps).some(step => step.run === 'node scripts/ci/required-check.mjs' &&
    step.env?.CI_NEEDS === '${{ toJSON(needs) }}' && step.env?.CI_PLAN === '${{ needs.select.outputs.plan }}'),
    'Aggregate must verify every result against the successful selector plan');
  const codeql = workflows['codeql-analysis.yml'];
  require(ci.jobs.codeql.with?.languages === '${{ toJSON(fromJSON(needs.select.outputs.plan).codeqlLanguages) }}' &&
    codeql.on.workflow_call?.inputs?.languages?.type === 'string' &&
    codeql.on.workflow_call.inputs.languages.default === '["csharp","javascript"]',
    'CodeQL must receive the complete planned language inventory with a full standalone default');
  require(codeql.on.schedule?.length > 0 && codeql.on.workflow_dispatch?.inputs?.languages === undefined &&
    codeql.jobs.analyze.strategy?.matrix?.language === '\${{ fromJSON(inputs.languages || \'["csharp","javascript"]\') }}' &&
    Object.keys(codeql.jobs.analyze.strategy.matrix).length === 1,
    'CodeQL language matrix must select exactly the plan and keep scheduled/manual scans full');
  require(array(codeql.jobs.analyze.steps).some(step => step.uses?.startsWith('github/codeql-action/init@') &&
    step.with?.['build-mode'] === "${{ matrix.language == 'csharp' && 'manual' || 'none' }}"),
    'CodeQL C# analysis must retain its instrumented manual build');
  const release = workflows['release.yml'];
  const production = release.jobs.production, candidates = release.jobs.candidates;
  require(production?.concurrency?.group === 'conduit-production-release' && production.concurrency['cancel-in-progress'] === false && production.concurrency.queue === 'max',
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
