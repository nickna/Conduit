import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

function resolveLockedDependency(packages, consumer, name) {
  let ancestor = consumer;
  while (ancestor) {
    const path = `${ancestor}/node_modules/${name}`;
    if (Object.hasOwn(packages, path)) return { path, ...packages[path] };
    const boundary = ancestor.lastIndexOf('/node_modules/');
    ancestor = boundary < 0 ? '' : ancestor.slice(0, boundary);
  }
  const path = `node_modules/${name}`;
  if (Object.hasOwn(packages, path)) return { path, ...packages[path] };
  throw new Error(`Missing locked ${name} for ${consumer}`);
}

// npm can report a successful lockfile-only install without applying this
// scoped override (#1486). Check the consumer's resolution, not just a hoisted copy.
export function checkDependencyOverrides(manifest, lock) {
  const expected = manifest.overrides?.['@istanbuljs/load-nyc-config']?.['js-yaml'];
  if (typeof expected !== 'string' || !/^\d+\.\d+\.\d+$/.test(expected))
    throw new Error('Require an exact coverage parser override version');
  if (lock.lockfileVersion !== 3 || !lock.packages || typeof lock.packages !== 'object')
    throw new Error('Require a complete v3 package lock');
  const consumers = Object.keys(lock.packages).filter(path =>
    path === 'node_modules/@istanbuljs/load-nyc-config' || path.endsWith('/node_modules/@istanbuljs/load-nyc-config'));
  if (!consumers.length) throw new Error('Coverage configuration loader is missing from the lockfile');
  for (const consumer of consumers) {
    const parser = resolveLockedDependency(lock.packages, consumer, 'js-yaml');
    if (parser.version !== expected)
      throw new Error(`Stale scoped override: ${parser.path} is ${parser.version}, expected ${expected}. See docs/operations/npm-lockfile-overrides.md`);
  }
  for (const path of Object.keys(lock.packages))
    if (path === 'node_modules/sprintf-js' || path.endsWith('/node_modules/sprintf-js'))
      throw new Error(`Obsolete vulnerable coverage dependency remains: ${path}`);
  return { consumers: consumers.length, parser: expected };
}

export function checkInstalledCoverageParser(directory, expected) {
  const require = createRequire(join(resolve(directory), 'package.json'));
  const loaderRequire = createRequire(require.resolve('@istanbuljs/load-nyc-config'));
  const actual = loaderRequire('js-yaml/package.json').version;
  if (actual !== expected) throw new Error(`Installed coverage parser is ${actual}, expected ${expected}`);
  return actual;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = process.argv.slice(2);
  const installed = args.includes('--installed');
  const directories = args.filter(arg => arg !== '--installed');
  if (directories.length > 1 || directories.some(arg => arg.startsWith('--'))) throw new Error('Usage: check-dependency-overrides.mjs [directory] [--installed]');
  const directory = resolve(directories[0] ?? fileURLToPath(new URL('..', import.meta.url)));
  const read = name => JSON.parse(readFileSync(join(directory, name), 'utf8').replace(/^\uFEFF/, ''));
  const result = checkDependencyOverrides(read('package.json'), read('package-lock.json'));
  if (installed) result.installedParser = checkInstalledCoverageParser(directory, result.parser);
  console.log(result);
}
