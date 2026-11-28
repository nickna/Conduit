import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { checkStaticAssets } from './check-static-assets.mjs';

const script = '/_next/static/chunks/page.js';
const stylesheet = '/_next/static/css/page.css';
const wrapper = source => `globalThis.__RSC_MANIFEST = globalThis.__RSC_MANIFEST || {};\nglobalThis.__RSC_MANIFEST["/fixture/page"] = ${source};\n`;
const manifest = chunks => wrapper(JSON.stringify({ clientModules: { fixture: { chunks } } }));

function fixture(t) {
  const owned = mkdtempSync(join(tmpdir(), 'conduit-static-assets-'));
  const runtime = join(owned, 'runtime');
  const server = join(runtime, '.next/server/app');
  const assets = join(runtime, '.next/static');
  mkdirSync(server, { recursive: true });
  mkdirSync(assets, { recursive: true });
  t.after(() => {
    assert.equal(dirname(resolve(owned)), resolve(tmpdir()));
    assert.ok(owned.includes('conduit-static-assets-'));
    rmSync(owned, { recursive: true, force: true });
  });
  return {
    owned, runtime, server, assets,
    manifest(text, name = 'fixture/page_client-reference-manifest.js') {
      const target = join(server, name);
      mkdirSync(dirname(target), { recursive: true });
      writeFileSync(target, text);
    },
    asset(url) {
      const name = url.split(/[?#]/, 1)[0].replace(/^\/_next\/static\//, '');
      assert.ok(!name.includes('..') && !name.startsWith('/'));
      const target = join(assets, name);
      mkdirSync(dirname(target), { recursive: true });
      writeFileSync(target, '/* fixture asset */');
    },
  };
}

function healthy(t) {
  const value = fixture(t);
  value.manifest(manifest([script, stylesheet]));
  value.asset(script);
  value.asset(stylesheet);
  return value;
}

test('checks JS and CSS shared across nested route manifests', t => {
  const value = healthy(t);
  value.manifest(manifest([script, stylesheet]), '(group)/other/page_client-reference-manifest.js');
  assert.deepEqual(checkStaticAssets(value.runtime), { manifests: 2, references: 4, uniqueAssets: 2 });
});

test('distinguishes browser chunks from valid SSR server-chunk mappings', t => {
  const value = healthy(t);
  value.manifest(wrapper(JSON.stringify({
    clientModules: { fixture: { chunks: [script, stylesheet] } },
    ssrModuleMapping: { 123: { '*': { chunks: ['server/chunks/ssr/[root-of-the-server]__fixture._.js'] } } },
  })));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 2);
});

test('scopes metadata strings to JS/CSS without rejecting a CDN prefix or static media', t => {
  const value = healthy(t);
  value.manifest(wrapper(JSON.stringify({
    moduleLoading: { prefix: 'https://cdn.example.invalid' },
    clientModules: { fixture: { chunks: [script, stylesheet] } },
    staticMedia: '/_next/static/media/font.woff2',
    staticPrefix: '/_next/static/',
  })));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 2);
});

test('accepts the pinned webpack producer initializer with balanced parentheses', t => {
  const value = healthy(t);
  value.manifest(manifest([script]).replace('= globalThis.__RSC_MANIFEST || {};', '= (globalThis.__RSC_MANIFEST || {});'));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 1);
  for (const initializer of ['= (globalThis.__RSC_MANIFEST || {};', '= globalThis.__RSC_MANIFEST || {});']) {
    value.manifest(manifest([script]).replace('= globalThis.__RSC_MANIFEST || {};', initializer));
    assert.throws(() => checkStaticAssets(value.runtime), /Invalid client-reference manifest header/);
  }
});

test('decodes quoted JSON strings and allows a URL query without changing the file', t => {
  const value = healthy(t);
  value.manifest(manifest([`${script}?deployment=fixture#asset`, stylesheet]).replaceAll('/_next/', '\\u002f_next\\u002f'));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 2);
});

test('accepts Next relative static URLs and numeric Webpack chunk identifiers', t => {
  const value = healthy(t);
  value.manifest(manifest(['123', 'static/chunks/page.js']));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 1);
});

// These 19 URLs come from the exact failed amd64 image's Virtual Keys manifest.
// Image: d5f1e533d38e5146cc1f9e2c01391e8a81c2e224a5d1319d5dfc4c1f7f6cbe4e.
// Manifest SHA256: 23069030ab5816b8aae354ac8d6fd50e90251b5b81d884f7f0cef27a98f000d7.
const retainedReferences = [
  "/_next/static/chunks/0btgj7d_zki8v.js",
  "/_next/static/chunks/0c62f3m2d3sww.js",
  "/_next/static/chunks/0c_-uuhjhjspa.js",
  "/_next/static/chunks/0cki0ht-wt5z_.js",
  "/_next/static/chunks/0sn0_ndts7dh2.js",
  "/_next/static/chunks/12_boi6bra97f.js",
  "/_next/static/chunks/19i5u5bkuoli8.js",
  "/_next/static/chunks/1an2y8jiurzpj.js",
  "/_next/static/chunks/1lduk2j9sm9nq.js",
  "/_next/static/chunks/1onsz2_enonl8.js",
  "/_next/static/chunks/2bxpll2ybc0ma.js",
  "/_next/static/chunks/33uvrka0t2gah.js",
  "/_next/static/chunks/3_xu83pkduwwe.js",
  "/_next/static/chunks/3ao9kwzkjitl5.js",
  "/_next/static/chunks/3e9c1bq9yfw1m.js",
  "/_next/static/chunks/3m1tr9ctc_nnt.js",
  "/_next/static/chunks/3uddi9s_9limi.js",
  "/_next/static/chunks/3zh3lwray5pwc.js",
  "/_next/static/chunks/450ts21u5ohv6.js",
];

test('rejects the retained 450ts manifest/static mismatch while the other 18 assets exist', t => {
  const value = fixture(t);
  assert.equal(retainedReferences.length, 19);
  value.manifest(manifest(retainedReferences));
  for (const url of retainedReferences) {
    if (!url.endsWith('/450ts21u5ohv6.js')) value.asset(url);
  }
  assert.throws(() => checkStaticAssets(value.runtime), /Missing static asset \/_next\/static\/chunks\/450ts21u5ohv6\.js/);
  value.asset('/_next/static/chunks/450ts21u5ohv6.js');
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 19);
});

test('rejects an absent asset and an asset replaced by a directory', t => {
  const value = fixture(t);
  value.manifest(manifest([script]));
  assert.throws(() => checkStaticAssets(value.runtime), /Missing static asset/);
  mkdirSync(join(value.assets, 'chunks/page.js'), { recursive: true });
  assert.throws(() => checkStaticAssets(value.runtime), /Missing static asset/);
});

test('fails closed without expected manifests or local references', t => {
  const value = fixture(t);
  assert.throws(() => checkStaticAssets(value.runtime), /No expected client-reference manifests/);
  value.manifest(manifest([]));
  assert.throws(() => checkStaticAssets(value.runtime), /No expected local JS\/CSS references/);
});

test('requires references in each manifest, including when another route is healthy', t => {
  const value = healthy(t);
  value.manifest(manifest([]), 'empty/page_client-reference-manifest.js');
  assert.throws(() => checkStaticAssets(value.runtime), /No expected local JS\/CSS references/);
});

test('permits generated empty API route manifests beside healthy pages', t => {
  const value = healthy(t);
  value.manifest(wrapper('{"clientModules":{}}'), 'api/health/route_client-reference-manifest.js');
  assert.deepEqual(checkStaticAssets(value.runtime), { manifests: 2, references: 2, uniqueAssets: 2 });
});

test('does not accept an empty-route-only runtime or a route with broken client coverage', t => {
  const value = fixture(t);
  value.manifest(wrapper('{"clientModules":{}}'), 'api/health/route_client-reference-manifest.js');
  assert.throws(() => checkStaticAssets(value.runtime), /No expected local JS\/CSS references/);
  value.manifest(manifest([]), 'api/health/route_client-reference-manifest.js');
  assert.throws(() => checkStaticAssets(value.runtime), /No expected local JS\/CSS references/);
});

test('rejects absent output directories', t => {
  const value = fixture(t);
  assert.throws(() => checkStaticAssets(join(value.owned, 'absent')));
});

test('rejects invalid JSON and invalid generated wrappers', t => {
  const value = healthy(t);
  value.manifest(wrapper('{"clientModules":'));
  assert.throws(() => checkStaticAssets(value.runtime), /Invalid client-reference manifest JSON/);
  value.manifest('{"clientModules":{}}');
  assert.throws(() => checkStaticAssets(value.runtime), /Invalid client-reference manifest header/);
});

test('never executes generated JavaScript or accepts trailing statements', t => {
  const value = healthy(t);
  globalThis.staticAssetGuardWasExecuted = false;
  value.manifest(`${manifest([script])}globalThis.staticAssetGuardWasExecuted = true;`);
  assert.throws(() => checkStaticAssets(value.runtime), /Invalid client-reference manifest JSON/);
  assert.equal(globalThis.staticAssetGuardWasExecuted, false);
  delete globalThis.staticAssetGuardWasExecuted;
});

test('rejects invalid UTF-8 instead of replacing bytes', t => {
  const value = healthy(t);
  value.manifest(Buffer.from([0xc3, 0x28]));
  assert.throws(() => checkStaticAssets(value.runtime), /encoded data|encoding/i);
});

test('requires the client module and chunk shapes', t => {
  const value = healthy(t);
  for (const object of [{ clientModules: [] }, { clientModules: { fixture: { chunks: script } } }, { clientModules: { fixture: { chunks: [1] } } }]) {
    value.manifest(wrapper(JSON.stringify(object)));
    assert.throws(() => checkStaticAssets(value.runtime), /clientModules|chunks must|reference must/);
  }
});

test('does not lose a missing asset hidden by duplicate object keys', t => {
  const value = healthy(t);
  value.manifest(wrapper(`{"clientModules":{"fixture":{"chunks":["/_next/static/chunks/missing.js"]},"fixture":{"chunks":["${script}"]}}}`));
  assert.throws(() => checkStaticAssets(value.runtime), /Missing static asset.*missing\.js/);
});

test('ignores remote assets but does not accept remote-only coverage', t => {
  const value = healthy(t);
  value.manifest(manifest([script, 'https://cdn.example.invalid/chunk.js', '//cdn.example.invalid/style.css']));
  assert.equal(checkStaticAssets(value.runtime).uniqueAssets, 1);
  value.manifest(manifest(['https://cdn.example.invalid/chunk.js']));
  assert.throws(() => checkStaticAssets(value.runtime), /No expected local JS\/CSS references/);
});

for (const unsafe of [
  '/_next/static/../secret.js',
  '/_next/static/chunks/%2e%2e/secret.js',
  '/_next/static/chunks/%252e%252e/secret.js',
  '/_next/static/chunks/%2fsecret.js',
  '/_next/static/chunks/..\\secret.js',
  '/_next/static/chunks/%00secret.js',
  '/_next/static/chunks/%ZZ.js',
  '/_next/static/chunks/C:secret.js',
  '/_next/static/chunks/not-javascript.txt',
  '../secret.js',
  'file:///tmp/secret.js',
]) {
  test(`rejects unsafe or unsupported asset URL ${JSON.stringify(unsafe)}`, t => {
    const value = healthy(t);
    value.manifest(manifest([script, unsafe]));
    assert.throws(() => checkStaticAssets(value.runtime), /Unsafe|Invalid|invalid/);
  });
}

test('rejects an asset directory symlink that escapes static output', t => {
  const value = healthy(t);
  const outside = join(value.owned, 'outside');
  mkdirSync(outside);
  writeFileSync(join(outside, 'escape.js'), '/* outside */');
  symlinkSync(outside, join(value.assets, 'escape'), process.platform === 'win32' ? 'junction' : 'dir');
  value.manifest(manifest(['/_next/static/escape/escape.js']));
  assert.throws(() => checkStaticAssets(value.runtime), /escapes the runtime tree/);
});
