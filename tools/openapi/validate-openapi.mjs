#!/usr/bin/env node
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptsDir, '..', '..');
const defaultFiles = [
  path.join(repoRoot, 'Services', 'ConduitLLM.Admin', 'openapi-admin.json'),
  path.join(repoRoot, 'Services', 'ConduitLLM.Gateway', 'openapi-gateway.json'),
];
const defaultAllowlist = path.join(scriptsDir, 'openapi-ratchet-allowlist.json');
const defaultOpenAiBaseline = path.join(scriptsDir, 'openai-conformance-baseline.json');
const openAiRoutes = [
  ['GET', '/v1/models', '/models'],
  ['GET', '/v1/models/{model}', '/models/{model}'],
  ['POST', '/v1/embeddings', '/embeddings'],
  ['POST', '/v1/chat/completions', '/chat/completions'],
  ['POST', '/v1/responses', '/responses'],
  ['POST', '/v1/audio/transcriptions', '/audio/transcriptions'],
  ['POST', '/v1/audio/speech', '/audio/speech'],
  ['POST', '/v1/images/generations', '/images/generations'],
];
// Bounded schema checks complement the existing top-level field comparison. They do
// not claim full schema equivalence: enums, constraints, nullability, and unlisted
// nested schemas are not compared. JSON types exclude null on both sides.
const openAiSchemaChecks = {
  'GET /v1/models': { response: ['', '/properties/data', '/properties/data/items',
    ...['id', 'object', 'created', 'owned_by'].map((name) => `/properties/data/items/properties/${name}`)] },
  'GET /v1/models/{model}': { response: ['',
    ...['id', 'object', 'created', 'owned_by'].map((name) => `/properties/${name}`)] },
  'POST /v1/chat/completions': { request: [
    'temperature', 'top_p', 'max_tokens', 'max_completion_tokens', 'n', 'seed',
    'frequency_penalty', 'presence_penalty', 'reasoning_effort', 'parallel_tool_calls',
    'modalities', 'audio', 'prediction', 'logprobs', 'top_logprobs', 'service_tier',
  ].map((name) => `/properties/${name}`), response: ['/properties/created'] },
  'POST /v1/embeddings': { request: ['/properties/dimensions'] },
  'POST /v1/images/generations': { request: [
    'n', 'output_compression', 'partial_images',
  ].map((name) => `/properties/${name}`), response: ['/properties/created'] },
  'POST /v1/audio/speech': { request: ['/properties/speed'] },
};
const methods = new Set(['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace']);
const ratchetRules = [
  'generic-success-schema',
  'unsupported-media-type',
  'missing-4xx-response',
  'property-casing',
  'admin-route-style',
  'operation-summary',
];
const allowedMediaTypes = new Set([
  'application/json',
  'application/merge-patch+json',
  'application/problem+json',
  'application/octet-stream',
  'text/event-stream',
]);

function schemaType(schema) {
  if (!schema || typeof schema !== 'object') return undefined;
  return Array.isArray(schema.type) ? schema.type.find((type) => type !== 'null') : schema.type;
}

function containsBinarySchema(schema, document, seen = new Set()) {
  if (!schema || typeof schema !== 'object' || seen.has(schema)) return false;
  seen.add(schema);
  if (schema.$ref?.startsWith('#/')) {
    const target = schema.$ref.slice(2).split('/').reduce((value, segment) =>
      value?.[segment.replaceAll('~1', '/').replaceAll('~0', '~')], document);
    return containsBinarySchema(target, document, seen);
  }
  if (schemaType(schema) === 'string' && schema.format === 'binary') return true;
  if (schema.items && containsBinarySchema(schema.items, document, seen)) return true;
  for (const child of [...(schema.allOf ?? []), ...(schema.oneOf ?? []), ...(schema.anyOf ?? [])]) {
    if (containsBinarySchema(child, document, seen)) return true;
  }
  return Object.values(schema.properties ?? {}).some((property) => containsBinarySchema(property, document, seen));
}

function isAllowedMediaType(contentType, schema, document, operation) {
  if (allowedMediaTypes.has(contentType)) return true;
  if (operation?.operationId === 'Metrics_GetAllMetrics' && schemaType(schema) === 'string' &&
      ['text/plain', 'application/openmetrics-text'].includes(contentType)) return true;
  return containsBinarySchema(schema, document);
}

function isGenericSuccessSchema(schema) {
  if (!schema || typeof schema !== 'object' || Object.keys(schema).length === 0) return true;
  if (schema.$ref) return false;
  if (schema.oneOf || schema.anyOf || schema.allOf) {
    const branches = schema.oneOf ?? schema.anyOf ?? schema.allOf;
    return branches.length === 0 || branches.every(isGenericSuccessSchema);
  }
  if (schemaType(schema) === 'array') return !schema.items || isGenericSuccessSchema(schema.items);
  if (schemaType(schema) !== 'object') return false;
  return Object.keys(schema.properties ?? {}).length === 0 &&
    !schema.additionalProperties &&
    !schema.patternProperties;
}

function contractName(file) {
  const normalized = file.replaceAll('\\', '/').toLowerCase();
  if (normalized.includes('conduitllm.admin') || normalized.includes('openapi-admin')) return 'admin';
  if (normalized.includes('conduitllm.gateway') || normalized.includes('openapi-gateway')) return 'gateway';
  return path.basename(file, path.extname(file)).toLowerCase();
}

function routeStyleProblem(route) {
  if (route === '/metrics') return null;
  if (!route.startsWith('/v1/admin/')) return 'must be under /v1/admin';
  const segments = route.split('/').slice(3);
  const literals = segments.filter((segment) => segment && !segment.startsWith('{'));
  const invalid = literals.find((segment) => !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(segment));
  if (invalid) return `literal segment '${invalid}' must be lowercase kebab-case`;
  const resource = segments[0];
  if (resource && !resource.startsWith('{') && !/(?:s|metadata)$/.test(resource)) {
    return `resource segment '${resource}' must be plural`;
  }
  return null;
}

function propertyNameIsValid(name, contract) {
  if (contract === 'gateway') return /^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$/.test(name);
  if (contract === 'admin') return /^[a-z][A-Za-z0-9]*$/.test(name);
  return true;
}

function visitSchemaProperties(schema, pointer, contract, addViolation, seen = new Set()) {
  if (!schema || typeof schema !== 'object' || seen.has(schema)) return;
  seen.add(schema);
  for (const [name, child] of Object.entries(schema.properties ?? {})) {
    const propertyPointer = `${pointer}/properties/${name}`;
    if (!propertyNameIsValid(name, contract)) {
      addViolation('property-casing', propertyPointer,
        `${contract === 'gateway' ? 'snake_case' : 'camelCase'} property naming is required`);
    }
    visitSchemaProperties(child, propertyPointer, contract, addViolation, seen);
  }
  if (schema.items) visitSchemaProperties(schema.items, `${pointer}/items`, contract, addViolation, seen);
  for (const keyword of ['allOf', 'oneOf', 'anyOf']) {
    for (const [index, child] of (schema[keyword] ?? []).entries()) {
      visitSchemaProperties(child, `${pointer}/${keyword}/${index}`, contract, addViolation, seen);
    }
  }
  if (schema.additionalProperties && typeof schema.additionalProperties === 'object') {
    visitSchemaProperties(schema.additionalProperties, `${pointer}/additionalProperties`, contract, addViolation, seen);
  }
}

function validateDocument(file, document) {
  const invariantErrors = [];
  const violations = [];
  const contract = contractName(file);
  const schemes = document.components?.securitySchemes ?? {};
  const operationIds = new Map();
  const violationKeys = new Set();

  const invariant = (location, message) => invariantErrors.push({ file, location, message });
  const violate = (rule, target, message) => {
    const key = `${contract}|${target}`;
    const unique = `${rule}|${key}`;
    if (!violationKeys.has(unique)) {
      violationKeys.add(unique);
      violations.push({ rule, key, file, target, message });
    }
  };

  if (document.openapi !== '3.1.1') {
    invariant('openapi', `expected 3.1.1, found ${document.openapi ?? 'missing'}`);
  }

  for (const [name, schema] of Object.entries(document.components?.schemas ?? {})) {
    visitSchemaProperties(schema, `#/components/schemas/${name}`, contract, violate);
  }

  for (const [route, pathItem] of Object.entries(document.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (!methods.has(method)) continue;
      const location = `${method.toUpperCase()} ${route}`;
      const operationId = operation.operationId?.trim();

      if (!operationId) {
        invariant(location, 'operationId is missing or empty');
      } else if (operationIds.has(operationId)) {
        invariant(location, `operationId '${operationId}' duplicates ${operationIds.get(operationId)}`);
      } else {
        operationIds.set(operationId, location);
      }
      if (!Array.isArray(operation.tags) || operation.tags.length === 0) {
        invariant(location, 'at least one tag is required');
      }
      if (!Array.isArray(operation.security)) {
        invariant(location, 'security must be explicit (an empty array means anonymous)');
      } else {
        for (const requirement of operation.security) {
          for (const schemeName of Object.keys(requirement)) {
            if (!(schemeName in schemes)) {
              invariant(location, `security requirement references unknown scheme '${schemeName}'`);
            }
          }
        }
      }
      if (!operation.summary?.trim()) {
        violate('operation-summary', location, 'summary is missing or empty');
      }
      if (contract === 'admin') {
        const problem = routeStyleProblem(route);
        if (problem) violate('admin-route-style', location, problem);
      }

      const security = operation.security ?? document.security ?? [];
      const authenticated = security.some((requirement) => Object.keys(requirement).length > 0);
      if (authenticated) {
        for (const status of ['401', '403', '429']) {
          if (!operation.responses?.[status] && !operation.responses?.default) {
            violate('missing-4xx-response', `${location} response ${status}`,
              `authenticated operation must document ${status}`);
          }
        }
      }

      for (const [contentType, media] of Object.entries(operation.requestBody?.content ?? {})) {
        const target = `${location} request ${contentType}`;
        if (!isAllowedMediaType(contentType, media.schema, document, operation)) {
          violate('unsupported-media-type', target, 'request uses a non-canonical media type');
        }
        visitSchemaProperties(media.schema, `#/paths/${route}/${method}/requestBody/content/${contentType}/schema`,
          contract, violate);
      }

      for (const [status, response] of Object.entries(operation.responses ?? {})) {
        const responseLocation = `${location} response ${status}`;
        if (!response.description?.trim()) {
          invariant(responseLocation, 'description is missing or empty');
        }
        const content = response.content ?? {};
        if ((status === '204' || status === '205' || method === 'head') && Object.keys(content).length > 0) {
          invariant(responseLocation, 'bodyless response must not declare content');
          continue;
        }
        for (const [contentType, media] of Object.entries(content)) {
          if (!isAllowedMediaType(contentType, media.schema, document, operation)) {
            violate('unsupported-media-type', `${responseLocation} ${contentType}`,
              'response uses a non-canonical media type');
          }
          visitSchemaProperties(media.schema,
            `#/paths/${route}/${method}/responses/${status}/content/${contentType}/schema`, contract, violate);
        }
        if (/^2\d\d$/.test(status) && status !== '204' && status !== '205' && method !== 'head') {
          if (Object.keys(content).length === 0) {
            invariant(responseLocation, 'body-bearing success response must declare content and a schema');
          }
          for (const [contentType, media] of Object.entries(content)) {
            if (!media.schema) {
              invariant(responseLocation, `${contentType} is missing a response schema`);
              continue;
            }
            if (contentType === 'application/json' && isGenericSuccessSchema(media.schema)) {
              violate('generic-success-schema', `${responseLocation} ${contentType}`,
                'success response must use a named, structured schema');
            }
            const binary = contentType === 'application/octet-stream' ||
              contentType.startsWith('image/') || contentType.startsWith('audio/') || contentType.startsWith('video/');
            if (binary && (schemaType(media.schema) !== 'string' || media.schema.format !== 'binary')) {
              invariant(responseLocation, `${contentType} must use a string/binary schema`);
            }
            if (contentType === 'text/event-stream' && schemaType(media.schema) !== 'string') {
              invariant(responseLocation, 'text/event-stream must use a string schema');
            }
          }
        }
      }
    }
  }

  if (contract === 'admin') {
    const scheme = schemes.MasterKey;
    if (scheme?.type !== 'apiKey' || scheme?.in !== 'header' || scheme?.name !== 'X-Master-Key') {
      invariant('components.securitySchemes.MasterKey', 'must be an X-Master-Key header apiKey scheme');
    }
  } else if (contract === 'gateway') {
    const scheme = schemes.VirtualKey;
    if (scheme?.type !== 'http' || scheme?.scheme !== 'bearer') {
      invariant('components.securitySchemes.VirtualKey', 'must be an HTTP Bearer scheme');
    }
  }

  return { invariantErrors, violations };
}

function resolveSchema(schema, document, seen = new Set()) {
  if (!schema?.$ref) return schema;
  if (!schema.$ref.startsWith('#/')) return schema;
  if (seen.has(schema.$ref)) return {};
  seen.add(schema.$ref);
  const target = schema.$ref.slice(2).split('/').reduce((value, segment) =>
    value?.[segment.replaceAll('~1', '/').replaceAll('~0', '~')], document);
  return resolveSchema(target, document, seen);
}

function schemaTypes(schema, document, seen = new Set()) {
  schema = resolveSchema(schema, document, seen);
  if (!schema || typeof schema !== 'object') return [];
  const types = new Set((Array.isArray(schema.type) ? schema.type : [schema.type])
    .filter((type) => type && type !== 'null'));
  if (schema.properties) types.add('object');
  for (const child of [...(schema.allOf ?? []), ...(schema.oneOf ?? []), ...(schema.anyOf ?? [])]) {
    schemaTypes(child, document, new Set(seen)).forEach((type) => types.add(type));
  }
  return [...types].sort();
}

function schemaAtPointer(schema, pointer, document) {
  const segments = pointer.split('/').slice(1)
    .map((segment) => segment.replaceAll('~1', '/').replaceAll('~0', '~'));
  function visit(current, remaining, seen = new Set()) {
    current = resolveSchema(current, document, seen);
    if (!remaining.length) return current;
    if (!current || typeof current !== 'object') return undefined;
    const [first, ...rest] = remaining;
    if (current[first] !== undefined) {
      const result = visit(current[first], rest, new Set(seen));
      if (result !== undefined) return result;
    }
    for (const child of current.allOf ?? []) {
      const result = visit(child, remaining, new Set(seen));
      if (result !== undefined) return result;
    }
    return undefined;
  }
  return visit(schema, segments);
}

function schemaFields(schema, document, seen = new Set()) {
  schema = resolveSchema(schema, document, seen);
  if (!schema || typeof schema !== 'object') return { properties: [], required: [] };
  const properties = new Set(Object.keys(schema.properties ?? {}));
  const required = new Set(schema.required ?? []);
  for (const child of [...(schema.allOf ?? []), ...(schema.oneOf ?? []), ...(schema.anyOf ?? [])]) {
    const fields = schemaFields(child, document, new Set(seen));
    fields.properties.forEach((name) => properties.add(name));
    fields.required.forEach((name) => required.add(name));
  }
  return { properties: [...properties].sort(), required: [...required].sort() };
}

function operationSchemas(operation) {
  const requestMedia = operation.requestBody?.content?.['application/json'] ??
    operation.requestBody?.content?.['multipart/form-data'];
  const success = Object.entries(operation.responses ?? {})
    .find(([status]) => /^2\d\d$/.test(status))?.[1];
  const responseMedia = success?.content?.['application/json'];
  return {
    request: requestMedia?.schema ?? null,
    response: responseMedia?.schema ?? null,
  };
}

function operationShape(operation, document, operationKey) {
  const schemas = operationSchemas(operation);
  return Object.fromEntries(['request', 'response'].map((side) => {
    if (!schemas[side]) return [side, null];
    const shape = schemaFields(schemas[side], document);
    const pointers = openAiSchemaChecks[operationKey]?.[side];
    if (pointers) {
      shape.schemas = Object.fromEntries(pointers.map((pointer) => {
        const schema = schemaAtPointer(schemas[side], pointer, document);
        const types = schemaTypes(schema, document);
        if (!types.length) throw new Error(`${operationKey} ${side} ${pointer || '/'}: pinned schema has no type`);
        const modelFields = operationKey.startsWith('GET /v1/models') &&
          ['', '/properties/data/items'].includes(pointer);
        return [pointer, { types, ...(modelFields ? schemaFields(schema, document) : {}) }];
      }));
    }
    return [side, shape];
  }));
}

function compareOpenAiConformance(gatewayDocument, baseline) {
  const warnings = [];
  for (const [operationKey, expected] of Object.entries(baseline.operations ?? {})) {
    const separator = operationKey.indexOf(' ');
    const method = operationKey.slice(0, separator).toLowerCase();
    const route = operationKey.slice(separator + 1);
    const operation = gatewayDocument.paths?.[route]?.[method];
    if (!operation) {
      warnings.push(`${operationKey}: shared OpenAI operation is missing`);
      continue;
    }
    const schemas = operationSchemas(operation);
    for (const side of ['request', 'response']) {
      if (!expected[side]) continue;
      const compareFields = (expectedShape, actualShape, location) => {
        for (const fieldKind of ['properties', 'required', 'types']) {
          if (!expectedShape[fieldKind]) continue;
          const expectedFields = expectedShape[fieldKind];
          const actualFields = actualShape[fieldKind] ?? [];
          const missing = expectedFields.filter((name) => !actualFields.includes(name));
          const extra = actualFields.filter((name) => !expectedFields.includes(name));
          if (missing.length || extra.length) {
            warnings.push(`${location} ${fieldKind}: ` +
              `missing [${missing.join(', ')}], extra [${extra.join(', ')}]`);
          }
        }
      };
      compareFields(expected[side], schemaFields(schemas[side], gatewayDocument), `${operationKey} ${side}`);
      for (const [pointer, expectedSchema] of Object.entries(expected[side].schemas ?? {})) {
        const schema = schemaAtPointer(schemas[side], pointer, gatewayDocument);
        compareFields(expectedSchema, {
          types: schemaTypes(schema, gatewayDocument),
          ...schemaFields(schema, gatewayDocument),
        }, `${operationKey} ${side} schema ${pointer || '/'}`);
      }
    }
  }
  return warnings;
}

function buildOpenAiBaseline(officialDocument, commit) {
  const operations = {};
  for (const [method, gatewayRoute, officialRoute] of openAiRoutes) {
    const operation = officialDocument.paths?.[officialRoute]?.[method.toLowerCase()];
    const key = `${method} ${gatewayRoute}`;
    if (!operation) throw new Error(`${key}: operation is missing from the pinned OpenAI document`);
    operations[key] = operationShape(operation, officialDocument, key);
  }
  return {
    version: 2,
    description: 'Enforces top-level fields on compatible operations, nested model objects, and selected field types. Does not compare enums, constraints, nullability, or unlisted nested schemas.',
    source: {
      repository: 'https://github.com/openai/openai-openapi',
      commit,
      document: 'openapi.json',
    },
    enforcement: 'enforced',
    operations,
  };
}

function validateOpenAiBaseline(baseline) {
  const invalid = (message) => { throw new Error(`Invalid OpenAI conformance baseline: ${message}`); };
  if (baseline?.version !== 2) invalid('version must be 2');
  if (baseline.enforcement !== 'enforced') invalid('enforcement must be enforced');
  if (baseline.source?.repository !== 'https://github.com/openai/openai-openapi' ||
      !/^[a-f0-9]{40}$/.test(baseline.source?.commit ?? '') ||
      baseline.source?.document !== 'openapi.json') invalid('pinned OpenAI source is missing or invalid');
  const validList = (values) => Array.isArray(values) && values.every((value) => typeof value === 'string') &&
    new Set(values).size === values.length;
  for (const [method, route] of openAiRoutes) {
    const key = `${method} ${route}`;
    const operation = baseline.operations?.[key];
    if (!operation || typeof operation !== 'object') invalid(`${key} is missing`);
    for (const side of ['request', 'response']) {
      const shape = operation[side];
      const bodylessSide = (side === 'request' && method === 'GET') ||
        (side === 'response' && key === 'POST /v1/audio/speech');
      if (bodylessSide) {
        if (shape !== null) invalid(`${key} ${side} must be null`);
        continue;
      }
      if (!shape || !validList(shape.properties) || !validList(shape.required) ||
          shape.required.some((field) => !shape.properties.includes(field))) invalid(`${key} ${side} fields are invalid`);
      for (const pointer of openAiSchemaChecks[key]?.[side] ?? []) {
        const schema = shape.schemas?.[pointer];
        const modelFields = key.startsWith('GET /v1/models') && ['', '/properties/data/items'].includes(pointer);
        if (!schema ||
            (modelFields && (!validList(schema.properties) || !validList(schema.required))) ||
            !validList(schema.properties ?? []) || !validList(schema.required ?? []) ||
            (schema.required ?? []).some((field) => !schema.properties?.includes(field)) ||
            !validList(schema.types) || !schema.types.length ||
            schema.types.some((type) => !['object', 'array', 'string', 'number', 'integer', 'boolean'].includes(type))) {
          invalid(`${key} ${side} schema ${pointer || '/'} is missing or invalid`);
        }
      }
    }
  }
  return baseline;
}

function readOpenAiBaseline(file) {
  if (!fs.existsSync(file)) throw new Error(`OpenAI conformance baseline is missing: ${file}`);
  return validateOpenAiBaseline(readJson(file));
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function conformanceSelfTest() {
  const model = {
    type: 'object',
    properties: {
      id: { type: 'string' }, object: { type: 'string' },
      created: { type: 'integer' }, owned_by: { type: 'string' },
    },
    required: ['id', 'object', 'created', 'owned_by'],
  };
  const response = (schema) => ({ responses: {
    200: { content: { 'application/json': { schema } } },
  } });
  const fixture = {
    components: { schemas: {
      Model: model,
      ModelAlias: { $ref: '#/components/schemas/Model' },
      ModelList: {
        type: 'object', properties: {
          object: { type: 'string' }, data: { type: 'array', items: { $ref: '#/components/schemas/ModelAlias' } },
        }, required: ['object', 'data'],
      },
    } },
    paths: {
      '/v1/models': { get: response({ $ref: '#/components/schemas/ModelList' }) },
      '/v1/models/{model}': { get: response({ $ref: '#/components/schemas/ModelAlias' }) },
      '/v1/chat/completions': { post: { requestBody: { content: { 'application/json': { schema: {
        type: 'object', properties: { max_completion_tokens: { type: ['integer', 'null'] } },
      } } } } } },
    },
  };
  const baseline = { operations: {
    'GET /v1/models': operationShape(fixture.paths['/v1/models'].get, fixture, 'GET /v1/models'),
    'GET /v1/models/{model}': operationShape(fixture.paths['/v1/models/{model}'].get, fixture, 'GET /v1/models/{model}'),
    'POST /v1/chat/completions': {
      request: { properties: ['max_completion_tokens'], required: [], schemas: {
        '/properties/max_completion_tokens': { types: ['integer'], properties: [], required: [] },
      } }, response: null,
    },
  } };
  assert.deepEqual(compareOpenAiConformance(fixture, baseline), []);
  const expectDifference = (mutate, pattern) => {
    const changed = structuredClone(fixture);
    mutate(changed);
    assert.ok(compareOpenAiConformance(changed, baseline).some((difference) => pattern.test(difference)));
  };
  expectDifference((changed) => { delete changed.paths['/v1/models/{model}']; }, /models\/\{model\}.*missing/);
  expectDifference((changed) => { delete changed.components.schemas.Model.properties.owned_by; }, /items properties.*owned_by/);
  expectDifference((changed) => { changed.components.schemas.Model.required.pop(); }, /items required.*owned_by/);
  expectDifference((changed) => { changed.components.schemas.Model.properties.created.type = 'string'; }, /created types.*integer.*string/);
  expectDifference((changed) => { changed.components.schemas.ModelList.properties.data.type = 'object'; }, /data types.*array.*object/);
  expectDifference((changed) => {
    changed.paths['/v1/chat/completions'].post.requestBody.content['application/json'].schema
      .properties.max_completion_tokens.type = ['integer', 'string', 'null'];
  }, /max_completion_tokens types.*extra \[string\]/);
  // allOf properties and escaped local references must resolve without weakening checks.
  assert.deepEqual(schemaTypes({ $ref: '#/components/schemas/a~1b' }, {
    components: { schemas: { 'a/b': { type: 'integer' } } },
  }), ['integer']);
  assert.deepEqual(schemaTypes(schemaAtPointer({ allOf: [
    { type: 'object', properties: { value: { type: 'integer' } } },
  ] }, '/properties/value', {}), {}), ['integer']);
  assert.deepEqual(schemaTypes(schemaAtPointer({
    type: 'object', properties: { sibling: { type: 'string' } },
    allOf: [{ type: 'object', properties: { inherited: { type: 'integer' } } }],
  }, '/properties/inherited', {}), {}), ['integer']);

  const committedBaseline = readOpenAiBaseline(defaultOpenAiBaseline);
  for (const mutate of [
    (changed) => { changed.enforcement = 'warn-only'; },
    (changed) => { changed.source.commit = ''; },
    (changed) => { delete changed.operations['GET /v1/models/{model}']; },
    (changed) => { changed.operations['POST /v1/embeddings'].response = null; },
    (changed) => { delete changed.operations['GET /v1/models'].response.schemas['/properties/data/items']; },
    (changed) => { changed.operations['GET /v1/models/{model}'].response.schemas[''].types = []; },
  ]) {
    const changed = structuredClone(committedBaseline);
    mutate(changed);
    assert.throws(() => validateOpenAiBaseline(changed), /Invalid OpenAI conformance baseline/);
  }
  assert.throws(() => readOpenAiBaseline(path.join(os.tmpdir(), `missing-openai-baseline-${process.pid}.json`)),
    /OpenAI conformance baseline is missing/);
}

function applyRatchet(violations, allowlist, activeContracts = null) {
  const allowed = new Map();
  for (const rule of ratchetRules) {
    allowed.set(rule, new Set(allowlist.rules?.[rule] ?? []));
  }
  const unallowlisted = violations.filter((violation) => !allowed.get(violation.rule)?.has(violation.key));
  const actual = new Map(ratchetRules.map((rule) => [rule, new Set(
    violations.filter((violation) => violation.rule === rule).map((violation) => violation.key),
  )]));
  const stale = [];
  for (const [rule, entries] of allowed) {
    for (const key of entries) {
      const contract = key.slice(0, key.indexOf('|'));
      if ((!activeContracts || activeContracts.has(contract)) && !actual.get(rule).has(key)) {
        stale.push({ rule, key });
      }
    }
  }
  return { unallowlisted, stale };
}

function makeAllowlist(violations) {
  const rules = Object.fromEntries(ratchetRules.map((rule) => [
    rule,
    violations.filter((violation) => violation.rule === rule).map((violation) => violation.key).sort(),
  ]));
  return {
    version: 1,
    description: 'Known API contract violations. Remove entries as endpoint families are fixed; new violations fail CI.',
    rules,
  };
}

function validateFiles(files, allowlist, openAiBaseline) {
  const invariantErrors = [];
  const violations = [];
  let gatewayDocument;
  for (const file of files) {
    const document = readJson(file);
    const result = validateDocument(file, document);
    invariantErrors.push(...result.invariantErrors);
    violations.push(...result.violations);
    if (contractName(file) === 'gateway') gatewayDocument = document;
  }
  const ratchet = applyRatchet(violations, allowlist, new Set(files.map(contractName)));
  const conformanceWarnings = gatewayDocument && openAiBaseline
    ? compareOpenAiConformance(gatewayDocument, openAiBaseline)
    : [];
  return { invariantErrors, violations, ...ratchet, conformanceWarnings };
}

function selfTest() {
  const base = {
    openapi: '3.1.1',
    info: { title: 'test', version: '1' },
    components: {
      securitySchemes: { MasterKey: { type: 'apiKey', in: 'header', name: 'X-Master-Key' } },
      schemas: {},
    },
    paths: {
      '/v1/admin/widgets': {
        get: {
          operationId: 'listWidgets',
          summary: 'List widgets',
          tags: ['Widgets'],
          security: [],
          responses: {
            200: {
              description: 'OK',
              content: { 'application/json': { schema: { $ref: '#/components/schemas/WidgetList' } } },
            },
          },
        },
      },
    },
  };
  base.components.schemas.WidgetList = {
    type: 'object',
    properties: { data: { type: 'array', items: { type: 'string' } } },
  };
  assert.equal(validateDocument('openapi-admin.json', structuredClone(base)).violations.length, 0);

  const upload = structuredClone(base);
  upload.components.schemas.File = { type: 'string', format: 'binary' };
  upload.paths['/v1/admin/widgets'].get.requestBody = {
    content: { 'multipart/form-data': { schema: { type: 'object',
      properties: { file: { $ref: '#/components/schemas/File' } } } } },
  };
  assert.equal(validateDocument('openapi-admin.json', upload).violations.length, 0,
    'multipart uploads may reference a reusable binary schema');
  upload.components.schemas.File = { $ref: '#/components/schemas/File' };
  assert(validateDocument('openapi-admin.json', upload).violations.some(v => v.rule === 'unsupported-media-type'),
    'cyclic or nonbinary references must not bypass media validation');

  const broken = structuredClone(base);
  broken.paths['/v1/admin/widget'] = broken.paths['/v1/admin/widgets'];
  delete broken.paths['/v1/admin/widgets'];
  const operation = broken.paths['/v1/admin/widget'].get;
  operation.summary = '';
  operation.security = [{ MasterKey: [] }];
  operation.responses[200].content = {
    'text/plain': { schema: { type: 'object', properties: { bad_name: { type: 'string' } } } },
    'application/json': { schema: { type: 'object' } },
  };
  const result = validateDocument('openapi-admin.json', broken);
  for (const rule of [
    'generic-success-schema',
    'unsupported-media-type',
    'missing-4xx-response',
    'property-casing',
    'admin-route-style',
    'operation-summary',
  ]) {
    assert(result.violations.some((violation) => violation.rule === rule), `${rule} was not detected`);
  }
  const emptyAllowlist = makeAllowlist([]);
  assert.equal(applyRatchet(result.violations, emptyAllowlist).unallowlisted.length, result.violations.length);
  const generatedAllowlist = makeAllowlist(result.violations);
  assert.deepEqual(applyRatchet(result.violations, generatedAllowlist), { unallowlisted: [], stale: [] });
  generatedAllowlist.rules['operation-summary'].push('admin|GET /v1/admin/stale');
  assert.equal(applyRatchet(result.violations, generatedAllowlist).stale.length, 1);

  conformanceSelfTest();
  console.log('OpenAPI ratchet and conformance self-tests passed.');
}

function parseArgs(argv) {
  const options = {
    files: [],
    allowlist: defaultAllowlist,
    baseline: defaultOpenAiBaseline,
    writeAllowlist: false,
    writeOpenAiBaseline: null,
    openAiCommit: null,
    selfTest: false,
  };
  for (let index = 0; index < argv.length; index++) {
    const value = argv[index];
    if (value === '--allowlist') options.allowlist = path.resolve(argv[++index]);
    else if (value === '--baseline') options.baseline = path.resolve(argv[++index]);
    else if (value === '--write-allowlist') options.writeAllowlist = true;
    else if (value === '--write-openai-baseline') options.writeOpenAiBaseline = path.resolve(argv[++index]);
    else if (value === '--openai-commit') options.openAiCommit = argv[++index];
    else if (value === '--self-test') options.selfTest = true;
    else options.files.push(path.resolve(value));
  }
  return options;
}

const options = parseArgs(process.argv.slice(2));

if (options.writeOpenAiBaseline) {
  if (!options.openAiCommit) throw new Error('--openai-commit is required with --write-openai-baseline');
  const official = readJson(options.writeOpenAiBaseline);
  const baseline = validateOpenAiBaseline(buildOpenAiBaseline(official, options.openAiCommit));
  fs.writeFileSync(options.baseline, `${JSON.stringify(baseline, null, 2)}${os.EOL}`);
  console.log(`Wrote pinned OpenAI conformance baseline to ${path.relative(repoRoot, options.baseline)}.`);
}
if (options.selfTest) selfTest();

if (!options.selfTest || options.files.length > 0 || options.writeAllowlist) {
  const files = options.files.length > 0 ? options.files : defaultFiles;
  const preliminary = [];
  for (const file of files) preliminary.push(...validateDocument(file, readJson(file)).violations);
  if (options.writeAllowlist) {
    fs.writeFileSync(options.allowlist, `${JSON.stringify(makeAllowlist(preliminary), null, 2)}${os.EOL}`);
    console.log(`Wrote ${preliminary.length} known violation(s) to ${path.relative(repoRoot, options.allowlist)}.`);
  }

  const allowlist = readJson(options.allowlist);
  const baseline = files.some((file) => contractName(file) === 'gateway')
    ? readOpenAiBaseline(options.baseline)
    : null;
  const result = validateFiles(files, allowlist, baseline);
  const allowlistCount = Object.values(allowlist.rules ?? {}).reduce((total, entries) => total + entries.length, 0);
  console.log(`OpenAPI ratchet allowlist: ${allowlistCount} known violation(s).`);
  for (const rule of ratchetRules) {
    console.log(`- ${rule}: ${(allowlist.rules?.[rule] ?? []).length}`);
  }
  if (result.conformanceWarnings.length > 0) {
    console.warn(`OpenAI conformance (${baseline?.enforcement ?? 'warn-only'}): ${result.conformanceWarnings.length} difference(s).`);
    for (const warning of result.conformanceWarnings) console.warn(`- ${warning}`);
  }

  const errors = [
    ...result.invariantErrors.map((error) =>
      `${path.relative(repoRoot, error.file)}: ${error.location}: ${error.message}`),
    ...result.unallowlisted.map((violation) =>
      `${path.relative(repoRoot, violation.file)}: ${violation.rule}: ${violation.key}: ${violation.message}`),
    ...result.stale.map((entry) =>
      `${path.relative(repoRoot, options.allowlist)}: stale ${entry.rule} entry '${entry.key}' must be removed`),
    ...(baseline?.enforcement === 'enforced'
      ? result.conformanceWarnings.map((warning) => `OpenAI conformance: ${warning}`)
      : []),
  ];
  if (errors.length > 0) {
    console.error(`OpenAPI invariant validation failed with ${errors.length} error(s):`);
    for (const error of errors) console.error(`- ${error}`);
    process.exitCode = 1;
  } else {
    console.log(`Validated ${files.length} OpenAPI contract(s).`);
  }
}
