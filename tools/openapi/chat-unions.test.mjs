import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compareChatUnions } from './chat-unions.mjs';

const document = JSON.parse(readFileSync(new URL('../../Services/ConduitLLM.Gateway/openapi-gateway.json', import.meta.url)));
const baseline = JSON.parse(readFileSync(new URL('./openai-conformance-baseline.json', import.meta.url))).chatUnions;
const parts = (doc) => doc.components.schemas.Message.properties.content.oneOf.find((branch) => branch.type === 'array').items;
const part = (doc, type) => parts(doc).oneOf.map((branch) =>
  branch.$ref ? doc.components.schemas[branch.$ref.split('/').at(-1)] : branch)
  .find((branch) => branch.properties.type.enum.includes(type));
const choice = (doc) => doc.components.schemas.ToolChoice.oneOf;

test('committed supported chat unions match the pinned recursive shapes', () => {
  assert.deepEqual(compareChatUnions(document, baseline), []);
});

for (const [name, mutate] of [
  ['unconstrained array items', (doc) => { parts(doc).oneOf = [{ type: 'object' }]; }],
  ['missing content discriminator', (doc) => { delete parts(doc).discriminator; }],
  ['wrong discriminator mapping', (doc) => { parts(doc).discriminator.mapping.text = parts(doc).discriminator.mapping.file; }],
  ['missing type restriction', (doc) => { delete part(doc, 'text').properties.type.enum; }],
  ['missing required text', (doc) => { part(doc, 'text').required = ['type']; }],
  ['wrong nested image URL type', (doc) => { part(doc, 'image_url').properties.image_url.properties.url.type = 'integer'; }],
  ['missing nested audio data', (doc) => { part(doc, 'input_audio').properties.input_audio.required = ['format']; }],
  ['widened audio formats', (doc) => { part(doc, 'input_audio').properties.input_audio.properties.format.enum.push('flac'); }],
  ['widened image detail', (doc) => { delete part(doc, 'image_url').properties.image_url.properties.detail.enum; }],
  ['provider fallback in standard union', (doc) => { parts(doc).oneOf.push({ type: 'object', additionalProperties: true }); }],
  ['object fallback in scalar content', (doc) => { doc.components.schemas.Message.properties.content.oneOf[0].type.push('object'); }],
  ['widened tool modes', (doc) => { choice(doc)[0].enum.push('anything'); }],
  ['wrong tool discriminator', (doc) => { choice(doc)[1].properties.type.enum = ['wrong']; }],
  ['missing nested function name', (doc) => { delete choice(doc)[1].properties.function.required; }],
  ['arbitrary tool object', (doc) => { choice(doc)[1] = { type: 'object' }; }],
  ['arbitrary nullable wrapper', (doc) => { doc.components.schemas.ChatCompletionRequest.properties.tool_choice.oneOf.push({ type: 'object' }); }],
]) {
  test(`detects ${name}`, () => {
    const changed = structuredClone(document);
    mutate(changed);
    assert(compareChatUnions(changed, baseline).length > 0);
  });
}
