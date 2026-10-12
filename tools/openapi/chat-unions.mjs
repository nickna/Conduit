// Recursive checks for the supported chat unions, independent of top-level field checks.
import assert from 'node:assert/strict';

const contentTypes = ['text', 'image_url', 'input_audio', 'file', 'refusal'];
const upstreamParts = ['Text', 'Image', 'Audio', 'File', 'Refusal'];

function resolve(schema, document) {
  const seen = new Set();
  while (schema?.$ref?.startsWith('#/')) {
    assert(!seen.has(schema.$ref), 'Cyclic chat union reference');
    seen.add(schema.$ref);
    schema = schema.$ref.slice(2).split('/').reduce((value, segment) =>
      value?.[segment.replaceAll('~1', '/').replaceAll('~0', '~')], document);
  }
  return schema;
}

// Ignore documentation and generator annotations; compare every nested wire field,
// required member, JSON type, enum, and URI format in these bounded unions.
function shape(schema, document) {
  schema = resolve(schema, document);
  if (!schema || typeof schema !== 'object') return null;
  const result = {};
  for (const field of ['type', 'enum', 'required', 'format', 'additionalProperties']) {
    if (schema[field] !== undefined) {
      result[field] = Array.isArray(schema[field]) ? [...schema[field]].sort() : schema[field];
    }
  }
  if (schema.properties) {
    result.properties = Object.fromEntries(Object.entries(schema.properties).sort(([a], [b]) => a.localeCompare(b))
      .map(([name, value]) => [name, shape(value, document)]));
  }
  for (const field of ['oneOf', 'anyOf', 'allOf']) {
    if (schema[field]) result[field] = schema[field].map((value) => shape(value, document))
      .sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
  }
  if (schema.items) result.items = shape(schema.items, document);
  return result;
}

export function buildChatUnionBaseline(official) {
  const parts = upstreamParts.map((name) =>
    official.components.schemas[`ChatCompletionRequestMessageContentPart${name}`]);
  const choice = official.components.schemas.ChatCompletionToolChoiceOption;
  const mode = choice.oneOf.find((branch) => branch.type === 'string');
  const namedFunction = official.components.schemas.ChatCompletionNamedToolChoice;
  return { contentParts: shape({ oneOf: parts }, official), toolChoice: shape({ oneOf: [mode, namedFunction] }, official) };
}

export function validateChatUnionBaseline(unions) {
  assert.equal(unions?.contentParts?.oneOf?.length, 5, 'Missing supported content-part baseline');
  assert.deepEqual(unions.contentParts.oneOf.map((part) => part.properties?.type?.enum?.[0]).sort(), [...contentTypes].sort());
  assert.equal(unions?.toolChoice?.oneOf?.length, 2, 'Missing supported tool-choice baseline');
  for (const part of unions.contentParts.oneOf) {
    assert.equal(part.type, 'object');
    assert(part.required?.includes('type'));
    assert(part.required?.includes(part.properties.type.enum[0]));
  }
}

export function compareChatUnions(document, expected) {
  const errors = [];
  const operation = document.paths?.['/v1/chat/completions']?.post;
  const request = resolve(operation?.requestBody?.content?.['application/json']?.schema, document);
  const messages = resolve(request?.properties?.messages, document);
  const message = resolve(messages?.items, document);
  const content = resolve(message?.properties?.content, document);
  const array = content?.oneOf?.find((branch) => branch.type === 'array');
  const parts = resolve(array?.items, document);
  let choice = resolve(request?.properties?.tool_choice, document);
  // Optional reference types are emitted as null | ToolChoice. Compare the supported
  // non-null union without allowing another arbitrary branch through this wrapper.
  if (choice?.oneOf?.length === 2 && choice.oneOf.some((branch) => branch.type === 'null'))
    choice = resolve(choice.oneOf.find((branch) => branch.type !== 'null'), document);
  if (parts?.discriminator?.propertyName !== 'type') errors.push('chat content: missing type discriminator');
  const mapping = parts?.discriminator?.mapping ?? {};
  if (Object.keys(mapping).length !== contentTypes.length || contentTypes.some((type) =>
    !parts?.oneOf?.some((branch) => branch.$ref === mapping[type] &&
      resolve(branch, document)?.properties?.type?.enum?.includes(type))))
    errors.push('chat content: missing or incorrect named discriminator mapping');
  if (content?.type !== undefined || content?.oneOf?.length !== 2 || array?.minItems !== 1 ||
      !content.oneOf.some((branch) => Array.isArray(branch.type) &&
        JSON.stringify([...branch.type].sort()) === '["null","string"]'))
    errors.push('chat content: expected nullable string or nonempty content-part array');
  const compare = (actual, baseline, name) => {
    try { assert.deepEqual(shape(actual, document), baseline); }
    catch { errors.push(`${name}: supported recursive union differs from pinned OpenAI baseline`); }
  };
  compare(parts, expected.contentParts, 'chat content parts');
  compare(choice, expected.toolChoice, 'chat tool_choice');
  return errors;
}
