import { readFileSync, readdirSync, realpathSync, statSync } from 'node:fs';
import { basename, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

const manifestSuffix = '_client-reference-manifest.js';
const jsonString = /"(?:[^"\\]|\\.)*"/g;

function within(parent, child) {
  const path = relative(parent, child);
  return path !== '..' && !path.startsWith(`..${sep}`) && !isAbsolute(path);
}

function manifestPayload(text, name) {
  // Next's generated wrapper contains JSON, not an executable configuration API.
  // Parse the assignment's payload and quoted strings; never import/eval this file.
  const header = /^\s*globalThis\.__RSC_MANIFEST\s*=\s*(\()?\s*globalThis\.__RSC_MANIFEST\s*\|\|\s*\{\s*\}\s*(\))?\s*;\s*/.exec(text);
  if (!header || Boolean(header[1]) !== Boolean(header[2])) throw new Error(`${name}: Invalid client-reference manifest header`);
  const rest = text.slice(header[0].length);
  const assignment = /^globalThis\.__RSC_MANIFEST\[\s*("(?:[^"\\]|\\.)*")\s*\]\s*=\s*/.exec(rest);
  if (!assignment) throw new Error(`${name}: Invalid client-reference manifest assignment`);
  JSON.parse(assignment[1]);
  const source = rest.slice(assignment[0].length).trim().replace(/;\s*$/, '');
  let payload;
  try { payload = JSON.parse(source); }
  catch (error) { throw new Error(`${name}: Invalid client-reference manifest JSON`, { cause: error }); }
  if (!payload || typeof payload !== 'object' || Array.isArray(payload) ||
      !payload.clientModules || typeof payload.clientModules !== 'object' || Array.isArray(payload.clientModules)) {
    throw new Error(`${name}: Missing clientModules object`);
  }
  return { payload, strings: [...source.matchAll(jsonString)].map(match => JSON.parse(match[0])) };
}

function assetPath(value, name, required = false) {
  if (typeof value !== 'string') throw new Error(`${name}: A chunk reference must be a string`);
  if (/^(?:https?:)?\/\//.test(value)) {
    if (!required) return null; // For example, moduleLoading.prefix is not a chunk URL.
    const url = new URL(value, 'https://external.invalid');
    if (!/\.(?:m?js|css)$/i.test(url.pathname)) throw new Error(`${name}: Invalid external chunk URL`);
    return null;
  }
  if (/^\d+$/.test(value) && required) return null; // Webpack chunk identifiers are not URLs.
  let url = value.startsWith('static/') ? `/_next/${value}` : value;
  if (!url.startsWith('/_next/static/')) {
    if (required) throw new Error(`${name}: Invalid local static URL ${value}`);
    return null;
  }
  url = url.split(/[?#]/, 1)[0];
  try { url = decodeURIComponent(url); }
  catch (error) { throw new Error(`${name}: Invalid encoded static URL ${value}`, { cause: error }); }
  const path = url.slice('/_next/static/'.length);
  const segments = path.split('/');
  // Other manifest metadata may contain fonts/images or a static URL prefix.
  // The lexical sweep checks JS/CSS URLs; required client chunks stay strict.
  if (!required && !/\.(?:m?js|css)$/i.test(path)) return null;
  if (!/\.(?:m?js|css)$/i.test(path) || /[\u0000-\u001f\u007f\\:]/.test(url) ||
      /%[0-9a-f]{2}/i.test(path) || segments.some(segment => !segment || segment === '.' || segment === '..')) {
    throw new Error(`${name}: Unsafe or invalid static URL ${value}`);
  }
  return segments;
}

function validateChunkArrays(value, name) {
  if (!value || typeof value !== 'object') return;
  if (Array.isArray(value)) {
    for (const item of value) validateChunkArrays(item, name);
    return;
  }
  for (const [key, item] of Object.entries(value)) {
    if (key === 'chunks') {
      if (!Array.isArray(item)) throw new Error(`${name}: chunks must be an array`);
      for (const chunk of item) assetPath(chunk, name, true);
    } else validateChunkArrays(item, name);
  }
}

function findManifests(directory) {
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Unexpected symlink in manifest tree: ${path}`);
    if (entry.isDirectory()) files.push(...findManifests(path));
    else if (entry.isFile() && entry.name.endsWith(manifestSuffix)) files.push(path);
  }
  return files.sort();
}

export function checkStaticAssets(runtimeDirectory) {
  const root = realpathSync(runtimeDirectory);
  const server = realpathSync(join(root, '.next/server/app'));
  const assets = realpathSync(join(root, '.next/static'));
  if (!within(root, server) || !within(root, assets)) throw new Error('Next output directories escape the runtime tree');
  const manifests = findManifests(server);
  if (!manifests.length) throw new Error('No expected client-reference manifests found');
  const uniqueAssets = new Set();
  let references = 0;
  for (const file of manifests) {
    const name = relative(root, file).split(sep).join('/');
    const text = new TextDecoder('utf-8', { fatal: true }).decode(readFileSync(file));
    const { payload, strings } = manifestPayload(text, name);
    // SSR module mappings also contain server/chunks paths; only clientModules'
    // chunk arrays are browser URLs. Local static strings are checked everywhere.
    validateChunkArrays(payload.clientModules, name);
    const manifestAssets = new Set();
    // Reading every quoted JSON string also retains asset references hidden by
    // duplicate object keys; JSON.parse alone would keep only their last values.
    for (const value of strings) {
      const segments = assetPath(value, name);
      if (!segments) continue;
      const path = resolve(assets, ...segments);
      let actual;
      try {
        actual = realpathSync(path);
        if (!statSync(actual).isFile()) throw new Error('Asset is not a regular file');
      } catch (error) {
        throw new Error(`${name}: Missing static asset ${value}`, { cause: error });
      }
      if (!within(assets, actual)) throw new Error(`${name}: Static asset escapes the runtime tree: ${value}`);
      manifestAssets.add(path);
      uniqueAssets.add(path);
    }
    // Next also emits empty route-handler manifests for API endpoints. Pages
    // must retain browser references; an empty-only runtime still fails below.
    const emptyRoute = basename(file) === 'route_client-reference-manifest.js' && Object.keys(payload.clientModules).length === 0;
    if (!manifestAssets.size && !emptyRoute) throw new Error(`${name}: No expected local JS/CSS references found`);
    references += manifestAssets.size;
  }
  if (!uniqueAssets.size) throw new Error('No expected local JS/CSS references found in runtime tree');
  return { manifests: manifests.length, references, uniqueAssets: uniqueAssets.size };
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  try {
    if (process.argv.length !== 3) throw new Error('Usage: node check-static-assets.mjs <runtime-directory>');
    console.log(JSON.stringify(checkStaticAssets(process.argv[2])));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
