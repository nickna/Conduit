import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { enforceCss } from './css-warning-policy.mjs';
const root = process.cwd();
const require = createRequire(resolve('WebAdmin/package.json'));
const { default: stylelint } = await import(pathToFileURL(require.resolve('stylelint')).href);
const output = resolve('artifacts/webadmin-hygiene'); mkdirSync(output, { recursive: true });
process.chdir('WebAdmin');
const css = await stylelint.lint({ files: 'src/**/*.css' });
writeFileSync(`${output}/css.json`, JSON.stringify(css.results.map(({ source, warnings, parseErrors, invalidOptionWarnings }) =>
  ({ source, warnings, parseErrors, invalidOptionWarnings })), null, 2));
if (css.results.some(r => r.parseErrors.length || r.invalidOptionWarnings.length || r.warnings.some(w => w.text.startsWith('Unknown rule'))))
  throw new Error('CSS lint configuration or syntax is invalid');
let dead;
try { dead = execFileSync(process.execPath, ['node_modules/knip/bin/knip.js', '--include', 'files,dependencies', '--no-progress', '--reporter', 'json'],
  { encoding: 'utf8', timeout: 120_000 }); }
catch (error) { dead = error.stdout; if (!dead) throw error; }
writeFileSync(`${output}/dead-code.json`, dead);
const unused = JSON.parse(dead);
if (!Array.isArray(unused.issues) || unused.issues.length || unused.files?.length) throw new Error('Unused code/dependencies detected');
const counts = {};
for (const result of css.results) for (const warning of result.warnings) {
  const key = `${relative(process.cwd(), result.source).replaceAll('\\', '/')}:${warning.rule}`;
  counts[key] = (counts[key] ?? 0) + 1;
}
const baselinePath = resolve(root, 'scripts/ci/css-warning-budget.json');
if (process.argv.includes('--write-baseline')) writeFileSync(baselinePath, JSON.stringify(counts, null, 2) + '\n');
const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
enforceCss(counts, baseline);
console.log({ cssFindings: Object.values(counts).reduce((a, b) => a + b, 0), cssRuleBudgets: Object.keys(baseline).length, deadCodeFindings: 0 });
