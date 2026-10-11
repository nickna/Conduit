import assert from 'node:assert/strict';
import OpenAI, { AuthenticationError, BadRequestError, NotFoundError } from 'openai';

const baseURL = process.env.CONDUIT_SMOKE_BASE_URL;
if (!baseURL) throw new Error('CONDUIT_SMOKE_BASE_URL is required.');

const options = { baseURL, maxRetries: 0, timeout: 10_000 };
const client = new OpenAI({
  ...options,
  apiKey: process.env.CONDUIT_SMOKE_API_KEY ?? 'smoke-key',
});
const chatModel = process.env.CONDUIT_SMOKE_CHAT_MODEL ?? 'chat-model';
const embeddingModel = process.env.CONDUIT_SMOKE_EMBEDDING_MODEL ?? 'embedding-model';
const imageModel = process.env.CONDUIT_SMOKE_IMAGE_MODEL ?? 'image-model';
const speechModel = process.env.CONDUIT_SMOKE_SPEECH_MODEL ?? 'speech-model';

function assertModel(model) {
  assert.equal(model.object, 'model');
  assert.ok(typeof model.id === 'string' && model.id.length > 0);
  assert.ok(Number.isInteger(model.created) && model.created >= 0, 'Model created must be a Unix timestamp.');
  assert.ok(typeof model.owned_by === 'string' && model.owned_by.length > 0, 'Model owner was missing.');
}

function assertChatUsage(usage) {
  assert.equal(usage?.prompt_tokens, 1);
  assert.equal(usage?.completion_tokens, 1);
  assert.equal(usage?.total_tokens, 2);
}

function assertResponse(response) {
  assert.equal(response.object, 'response');
  assert.ok(response.id.startsWith('resp_'));
  assert.equal(response.status, 'completed');
  assert.equal(response.model, chatModel);
  assert.equal(response.output_text, 'ok');
  assert.equal(response.output.length, 1);
  assert.equal(response.output[0].type, 'message');
  assert.equal(response.output[0].role, 'assistant');
  assert.equal(response.output[0].status, 'completed');
  assert.equal(response.output[0].content[0].type, 'output_text');
  assert.equal(response.output[0].content[0].text, 'ok');
  assert.equal(response.usage?.input_tokens, 1);
  assert.equal(response.usage?.output_tokens, 1);
  assert.equal(response.usage?.total_tokens, 2);
}

const models = await client.models.list();
assert.equal(models.object, 'list');
assert.ok(models.data.length > 0, 'Models list was empty.');
models.data.forEach(assertModel);
const model = await client.models.retrieve(chatModel);
assertModel(model);
assert.equal(model.id, chatModel);
const listedModel = models.data.find((item) => item.id === chatModel);
assert.ok(listedModel, 'Retrieved model was absent from the list.');
for (const field of ['id', 'object', 'created', 'owned_by']) assert.equal(model[field], listedModel[field]);

await assert.rejects(client.models.retrieve('sdk-smoke-missing-model'), (error) => {
  assert.ok(error instanceof NotFoundError);
  assert.equal(error.status, 404);
  assert.equal(error.code, 'model_not_found');
  assert.equal(error.type, 'invalid_request_error');
  assert.equal(error.param, 'model');
  return true;
});
const invalidClient = new OpenAI({ ...options, apiKey: 'invalid-smoke-key' });
await assert.rejects(invalidClient.models.retrieve(chatModel), (error) => {
  assert.ok(error instanceof AuthenticationError);
  assert.equal(error.status, 401);
  return true;
});

const chatRequest = {
  model: chatModel,
  messages: [{ role: 'user', content: [{ type: 'text', text: 'SDK smoke test' }] }],
  max_completion_tokens: 16,
  reasoning_effort: 'low',
  parallel_tool_calls: false,
  modalities: ['text'],
  prediction: { type: 'content', content: 'ok' },
  logprobs: true,
  top_logprobs: 1,
  service_tier: 'default',
  tool_choice: 'none',
};
const chat = await client.chat.completions.create(chatRequest);
assert.equal(chat.object, 'chat.completion');
assert.equal(chat.model, chatModel);
assert.equal(chat.choices[0]?.message?.role, 'assistant');
assert.equal(chat.choices[0]?.message?.content, 'ok');
assert.equal(chat.choices[0]?.finish_reason, 'stop');
assertChatUsage(chat.usage);

await assert.rejects(client.chat.completions.create({ ...chatRequest, max_completion_tokens: '16' }), (error) => {
  assert.ok(error instanceof BadRequestError);
  assert.equal(error.status, 400);
  return true;
});

const chatStream = await client.chat.completions.create({
  ...chatRequest,
  stream: true,
  stream_options: { include_usage: true },
});
let streamedChatText = '';
let chatFinishReason;
let chatUsage;
let chatChunkCount = 0;
for await (const chunk of chatStream) {
  chatChunkCount++;
  assert.equal(chunk.object, 'chat.completion.chunk');
  assert.equal(chunk.model, chatModel);
  assert.ok(typeof chunk.id === 'string' && chunk.id.length > 0);
  assert.ok(Number.isInteger(chunk.created));
  for (const choice of chunk.choices) {
    assert.equal(choice.index, 0);
    streamedChatText += choice.delta.content ?? '';
    chatFinishReason = choice.finish_reason ?? chatFinishReason;
  }
  if (chunk.usage) assert.equal(chunk.choices.length, 0, 'Usage must be emitted in the final empty-choice chunk.');
  chatUsage = chunk.usage ?? chatUsage;
}
assert.ok(chatChunkCount >= 2, 'Chat stream ended before its completion chunk.');
assert.equal(streamedChatText, 'ok');
assert.equal(chatFinishReason, 'stop');
assertChatUsage(chatUsage);

assertResponse(await client.responses.create({ model: chatModel, input: 'SDK smoke test', store: false }));
const responseStream = client.responses.stream({ model: chatModel, input: 'SDK smoke test', store: false });
let streamedResponseText = '';
let responseCompletedCount = 0;
let sequence = -1;
for await (const event of responseStream) {
  assert.ok(event.sequence_number > sequence, 'Responses stream sequence numbers did not increase.');
  sequence = event.sequence_number;
  if (event.type === 'response.output_text.delta') streamedResponseText += event.delta;
  if (event.type === 'response.completed') responseCompletedCount++;
}
assert.equal(streamedResponseText, 'ok');
assert.equal(responseCompletedCount, 1, 'Responses stream was missing its completed event.');
assertResponse(await responseStream.finalResponse());

const embedding = await client.embeddings.create({
  model: embeddingModel,
  input: 'SDK smoke test',
  encoding_format: 'float',
});
assert.equal(embedding.object, 'list');
assert.equal(embedding.model, embeddingModel);
assert.equal(embedding.data.length, 1);
assert.equal(embedding.data[0].object, 'embedding');
assert.equal(embedding.data[0].index, 0);
assert.equal(embedding.data[0].embedding.length, 1);
assert.ok(Math.abs(embedding.data[0].embedding[0] - 0.1) < 1e-6, 'Embedding vector was incorrect.');
assert.equal(embedding.usage.prompt_tokens, 1);
assert.equal(embedding.usage.total_tokens, 1);

const image = await client.images.generate({
  model: imageModel,
  prompt: 'A small blue square',
  response_format: 'b64_json',
});
assert.equal(image.created, 1);
assert.equal(image.data?.length, 1);
assert.deepEqual(Buffer.from(image.data[0].b64_json, 'base64').subarray(0, 8), Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
assert.ok(!image.data[0].url, 'Base64 image response also exposed a URL.');

const speech = await client.audio.speech.create({
  model: speechModel,
  input: 'SDK smoke test',
  voice: 'alloy',
  response_format: 'mp3',
});
assert.equal(speech.headers.get('content-type'), 'audio/mpeg');
assert.deepEqual(Buffer.from(await speech.arrayBuffer()), Buffer.from([0x49, 0x44, 0x33, 0x04, 0x00, 0x00]));

console.log('Official Node SDK smoke test passed.');
