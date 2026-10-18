import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repository = fileURLToPath(new URL('../../', import.meta.url));

async function productionSources(directory) {
  const files = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(files
    .filter(file => !['bin', 'obj', 'node_modules'].includes(file.name))
    .map(async file => {
      const absolute = path.join(directory, file.name);
      if (file.isDirectory()) return productionSources(absolute);
      if (!file.name.endsWith('.cs')) return [];
      return [{ file: path.relative(repository, absolute), text: await readFile(absolute, 'utf8') }];
    }));
  return nested.flat();
}

test('production JSON contracts cannot reintroduce reflection resolvers or open enum converters', async () => {
  const sources = (await Promise.all(['Services', 'Shared']
    .map(directory => productionSources(path.join(repository, directory))))).flat();
  assert.ok(sources.length > 100, 'Production source inventory must be populated');
  const violations = sources.flatMap(source => {
    const errors = [];
    if (/\bDefaultJsonTypeInfoResolver\b/.test(source.text)) errors.push('reflection JSON resolver');
    if (/\bJsonStringEnumConverter\s*(?:\(|\))/.test(source.text)) errors.push('non-generic enum converter');
    return errors.map(error => `${source.file}: ${error}`);
  });
  assert.deepEqual(violations, [], 'Use boundary-owned generated metadata and closed generic enum converters');
});

test('ordinary service builds and the contract test executable disable JSON reflection', async () => {
  const projects = [
    'Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj',
    'Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj',
    'Tests/ConduitLLM.SerializationTests/ConduitLLM.SerializationTests.csproj',
  ];
  for (const project of projects) {
    const text = await readFile(path.join(repository, project), 'utf8');
    assert.match(text,
      /<JsonSerializerIsReflectionEnabledByDefault>\s*false\s*<\/JsonSerializerIsReflectionEnabledByDefault>/,
      `${project} must disable reflection outside the opt-in native profile`);
  }
});
