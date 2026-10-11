import { execFileSync } from 'node:child_process';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

// Every aggregate job needs an explicit owner. Adding an unclassified job must
// fail instead of silently excluding it from a selective run.
const owners = {
  select: 'always', 'source-ignore-guard': 'always', dependencies: 'always',
  webadmin: 'frontend', docker: 'frontend', codeql: 'frontend',
  'media-dispatch-integration': 'backend', 'billing-fault-injection': 'backend',
  'billing-invariants': 'backend', 'signalr-integration': 'backend',
  'aot-analyzers': 'backend', validate: 'backend', 'wolverine-two-host': 'backend',
  migrations: 'backend', 'distributed-lock': 'backend', 's3-storage': 'backend', contracts: 'backend',
};
const rootDocs = new Set(['AGENTS.md', 'README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'SECURITY.md', 'LICENSE', 'LICENSE.md']);
const shaPattern = /^[a-f0-9]{40}(?:[a-f0-9]{24})?$/;
const isSha = value => typeof value === 'string' && shaPattern.test(value);

export function validateInventory(inventory) {
  if (!Array.isArray(inventory) || !inventory.length ||
      inventory.some(id => typeof id !== 'string') || new Set(inventory).size !== inventory.length) {
    throw new Error('Invalid or empty validation job inventory');
  }
  if (JSON.stringify([...inventory].sort()) !== JSON.stringify(Object.keys(owners).sort())) {
    throw new Error('Validation job inventory differs from explicit change-impact ownership');
  }
}

function validPath(path) {
  return typeof path === 'string' && path.length > 0 && !path.includes('\0') && !path.includes('\\') &&
    !path.startsWith('/') && !/^[a-z]:/i.test(path) && !path.split('/').some(part => !part || part === '.' || part === '..');
}

export function classifyPath(path) {
  if (!validPath(path)) return 'unknown';
  if (rootDocs.has(path) || (path.startsWith('docs/') && path.endsWith('.md'))) return 'docs';
  // Generated clients must still prove agreement with the authoritative backend
  // exports. Editing these files alone cannot bypass contract drift validation.
  if (path.startsWith('WebAdmin/') && !path.startsWith('WebAdmin/src/generated/')) return 'frontend';
  return 'unknown';
}

function jobsFor(inventory, mode) {
  return Object.fromEntries(inventory.map(id => [id,
    mode === 'full' || owners[id] === 'always' || (mode === 'frontend' && owners[id] === 'frontend')]));
}

export function createPlan({ event, files = [], base = null, head = null, fallback = null }, inventory) {
  validateInventory(inventory);
  if (typeof event !== 'string' || !event) throw new Error('Missing CI event');
  if (!Array.isArray(files) || files.some(file => typeof file !== 'string')) throw new Error('Invalid changed file inventory');
  files = [...new Set(files)].sort();
  let mode = 'full';
  let reasons;
  if (event !== 'pull_request') reasons = [`${event} requires complete validation`];
  else if (fallback) reasons = [`Complete validation: ${fallback}`];
  else if (!isSha(base) || !isSha(head)) reasons = ['Complete validation: PR revision comparison is unavailable'];
  else if (!files.length) reasons = ['Complete validation: the PR diff is empty'];
  else {
    const unknown = files.filter(file => classifyPath(file) === 'unknown');
    if (unknown.length) reasons = unknown.map(file => `Complete validation: backend, tooling, generated contract, or unclassified input ${JSON.stringify(file)}`);
    else if (files.some(file => classifyPath(file) === 'frontend')) {
      mode = 'frontend';
      reasons = ['Only WebAdmin and optional documentation inputs changed; retain frontend coverage and packaged cross-service execution'];
    } else {
      mode = 'docs';
      reasons = ['Only allowlisted non-executable documentation inputs changed'];
    }
  }
  return { version: 1, event, mode, jobs: jobsFor(inventory, mode),
    codeqlLanguages: mode === 'full' ? ['csharp', 'javascript'] : mode === 'frontend' ? ['javascript'] : [],
    reasons, base, head, files };
}

export function validatePlan(inventory, plan, expectedEvent, comparison) {
  validateInventory(inventory);
  if (!plan || typeof plan !== 'object' || Array.isArray(plan) || plan.version !== 1 ||
      typeof plan.event !== 'string' || !plan.event || (expectedEvent && plan.event !== expectedEvent) ||
      !['full', 'frontend', 'docs'].includes(plan.mode) ||
      !Array.isArray(plan.files) || plan.files.some(file => typeof file !== 'string') ||
      new Set(plan.files).size !== plan.files.length ||
      !Array.isArray(plan.reasons) || !plan.reasons.length || plan.reasons.some(reason => typeof reason !== 'string' || !reason) ||
      !(plan.base === null || isSha(plan.base)) || !(plan.head === null || isSha(plan.head))) {
    throw new Error('Invalid CI selection plan or event');
  }
  if (plan.mode !== 'full') {
    const derived = createPlan(plan, inventory);
    if (derived.mode !== plan.mode) throw new Error('Selective plan does not match its changed inputs');
    if (comparison && (plan.base !== comparison.base || plan.head !== comparison.head)) {
      throw new Error('Selective plan does not match the current PR revisions');
    }
  }
  const expectedJobs = jobsFor(inventory, plan.mode);
  if (!plan.jobs || typeof plan.jobs !== 'object' || Array.isArray(plan.jobs) ||
      JSON.stringify(Object.keys(plan.jobs).sort()) !== JSON.stringify([...inventory].sort()) ||
      inventory.some(id => plan.jobs[id] !== expectedJobs[id])) throw new Error('Invalid CI selection job inventory or decision');
  const expectedLanguages = plan.mode === 'full' ? ['csharp', 'javascript'] : plan.mode === 'frontend' ? ['javascript'] : [];
  if (JSON.stringify(plan.codeqlLanguages) !== JSON.stringify(expectedLanguages)) throw new Error('Invalid planned CodeQL languages');
  return plan;
}

// The plan appears both directly and JSON-escaped inside CI_NEEDS. Bound that
// nested representation well below Linux's per-environment-string limit and
// Actions' job-output limit. Detailed changed paths remain in the artifact.
export function preparePlanOutput(plan, inventory) {
  validatePlan(inventory, plan);
  const nested = JSON.stringify({ plan: JSON.stringify(plan) });
  if (Math.max(Buffer.byteLength(nested, 'utf8'), nested.length * 2) <= 32 * 1024) {
    return { plan, evidence: plan };
  }
  const evidence = createPlan({ ...plan, fallback: 'PR change inventory exceeds the safe job-output size' }, inventory);
  const compact = { ...evidence, files: [],
    reasons: [`Complete validation: selection evidence exceeds the safe job-output size; ${evidence.files.length} changed paths retained in ci-selection/plan.json`] };
  validatePlan(inventory, compact);
  return { plan: compact, evidence };
}

// -z preserves spaces, Unicode and newlines. Rename/copy records include both
// paths so moving a backend file into WebAdmin still requires backend validation.
export function parseChangedFiles(output) {
  if (typeof output !== 'string' || (output && !output.endsWith('\0'))) throw new Error('Incomplete Git diff');
  const fields = output ? output.slice(0, -1).split('\0') : [];
  const files = [];
  for (let i = 0; i < fields.length;) {
    const status = fields[i++];
    if (!/^(?:[ADMT]|[RC](?:100|[0-9]{1,2}))$/.test(status)) throw new Error('Unknown Git change status');
    const paths = /^[RC]/.test(status) ? 2 : 1;
    for (let j = 0; j < paths; j++) {
      const file = fields[i++];
      if (!validPath(file)) throw new Error('Invalid or incomplete Git changed path');
      files.push(file);
    }
  }
  return [...new Set(files)].sort();
}

export function readChangedFiles(cwd, base, head) {
  if (!isSha(base) || !isSha(head)) throw new Error('Invalid PR revision');
  return parseChangedFiles(execFileSync('git', ['diff', '--no-ext-diff', '--no-textconv', '--name-status', '-z', '--find-renames', `${base}...${head}`, '--'],
    { cwd, encoding: 'utf8', timeout: 30_000, maxBuffer: 32 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] }));
}

export function comparisonFromEvent(payload) {
  const base = payload?.pull_request?.base?.sha;
  const head = payload?.pull_request?.head?.sha;
  if (!isSha(base) || !isSha(head)) throw new Error('PR base/head revisions are missing or invalid');
  return { base, head };
}

export function planFromEvent(event, payload, inventory, cwd = process.cwd()) {
  if (event !== 'pull_request') return createPlan({ event }, inventory);
  let comparison = { base: null, head: null };
  try {
    comparison = comparisonFromEvent(payload);
    return createPlan({ event, ...comparison, files: readChangedFiles(cwd, comparison.base, comparison.head) }, inventory);
  } catch {
    return createPlan({ event, ...comparison, fallback: 'PR history or complete diff could not be read' }, inventory);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const inventory = JSON.parse(readFileSync(new URL('./required-jobs.json', import.meta.url), 'utf8'));
  const event = process.env.GITHUB_EVENT_NAME;
  let payload;
  try { payload = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')); } catch { /* full PR fallback */ }
  const { plan, evidence } = preparePlanOutput(validatePlan(inventory, planFromEvent(event, payload, inventory), event), inventory);
  mkdirSync('artifacts/ci-selection', { recursive: true });
  writeFileSync('artifacts/ci-selection/plan.json', JSON.stringify(evidence, null, 2) + '\n');
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `plan=${JSON.stringify(plan)}\n`);
  if (process.env.GITHUB_STEP_SUMMARY) {
    const selected = inventory.filter(id => plan.jobs[id]);
    const excluded = inventory.filter(id => !plan.jobs[id]);
    appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `\n### CI change selection\n\nMode: **${plan.mode}**; event: **${plan.event}**; changed paths: ${evidence.files.length}.\n\n` +
      `Selected: ${selected.join(', ')}.\n\nExcluded: ${excluded.join(', ') || 'none'}.\n\n` +
      'The retained ci-selection plan records PR revisions, all changed paths and selection reasons.\n');
  }
  console.log(`CI selection: ${plan.mode}; ${inventory.filter(id => plan.jobs[id]).length}/${inventory.length} jobs selected`);
}
