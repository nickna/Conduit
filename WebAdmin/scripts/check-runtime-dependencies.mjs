import { readFileSync, readdirSync, realpathSync, statSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

// #1467 approves braces only in development tooling. Check the files that
// actually ship, independently of npm's dev flag and the advisory database.
export function checkRuntimeDependencies(directory) {
  const root = resolve(directory);
  if (!statSync(join(root, 'server.js')).isFile()) throw new Error('Expected a Next standalone server');
  const pending = [root], visited = new Set();
  let packages = 0;
  while (pending.length) {
    const current = realpathSync(pending.pop());
    if (visited.has(current)) continue;
    visited.add(current);
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = join(current, entry.name);
      if (entry.isDirectory() || (entry.isSymbolicLink() && statSync(path).isDirectory())) pending.push(path);
      else if (basename(path) === 'package.json') {
        const manifest = JSON.parse(readFileSync(path, 'utf8'));
        if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest) ||
            (Object.hasOwn(manifest, 'name') && typeof manifest.name !== 'string'))
          throw new Error(`Invalid package manifest: ${path}`);
        packages++;
        if (manifest.name === 'braces') throw new Error(`Development-only braces must not ship: ${path}`);
      }
    }
  }
  if (!packages) throw new Error('Standalone dependency manifests are missing');
  return { packages, braces: 0 };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  if (!process.argv[2]) throw new Error('Pass the Next standalone directory');
  console.log(checkRuntimeDependencies(process.argv[2]));
}
