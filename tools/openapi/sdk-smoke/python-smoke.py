import base64
import math
import os
import struct

from openai import AuthenticationError, BadRequestError, NotFoundError, OpenAI


base_url = os.environ.get("CONDUIT_SMOKE_BASE_URL")
if not base_url:
    raise RuntimeError("CONDUIT_SMOKE_BASE_URL is required.")

options = {"base_url": base_url, "max_retries": 0, "timeout": 10.0}
client = OpenAI(api_key=os.environ.get("CONDUIT_SMOKE_API_KEY", "smoke-key"), **options)
chat_model = os.environ.get("CONDUIT_SMOKE_CHAT_MODEL", "chat-model")
embedding_model = os.environ.get("CONDUIT_SMOKE_EMBEDDING_MODEL", "embedding-model")
image_model = os.environ.get("CONDUIT_SMOKE_IMAGE_MODEL", "image-model")
speech_model = os.environ.get("CONDUIT_SMOKE_SPEECH_MODEL", "speech-model")


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def check_model(model):
    check(model.object == "model", "Model object discriminator was incorrect.")
    check(isinstance(model.id, str) and model.id, "Model ID was missing.")
    check(isinstance(model.created, int) and model.created >= 0, "Model created must be a Unix timestamp.")
    check(isinstance(model.owned_by, str) and model.owned_by, "Model owner was missing.")


def check_chat_usage(usage):
    check(usage is not None, "Chat usage was missing.")
    check((usage.prompt_tokens, usage.completion_tokens, usage.total_tokens) == (1, 1, 2), "Chat usage was incorrect.")


def check_response(response):
    check(response.object == "response" and response.id.startswith("resp_"), "Responses object was incorrect.")
    check(response.status == "completed" and response.model == chat_model, "Responses completion status/model was incorrect.")
    check(response.output_text == "ok", "Responses output text was incorrect.")
    check(len(response.output) == 1, "Responses output item count was incorrect.")
    message = response.output[0]
    check((message.type, message.role, message.status) == ("message", "assistant", "completed"), "Responses message was incorrect.")
    check((message.content[0].type, message.content[0].text) == ("output_text", "ok"), "Responses content part was incorrect.")
    check(response.usage is not None, "Responses usage was missing.")
    check((response.usage.input_tokens, response.usage.output_tokens, response.usage.total_tokens) == (1, 1, 2), "Responses usage was incorrect.")


models = client.models.list()
check(models.object == "list" and models.data, "Models list was incorrect or empty.")
for model in models.data:
    check_model(model)
model = client.models.retrieve(chat_model)
check_model(model)
check(model.id == chat_model, "Retrieved model ID was incorrect.")
listed_model = next((item for item in models.data if item.id == chat_model), None)
check(listed_model is not None, "Retrieved model was absent from the list.")
for field in ("id", "object", "created", "owned_by"):
    check(getattr(model, field) == getattr(listed_model, field), "Model list/retrieve objects differed.")

try:
    client.models.retrieve("sdk-smoke-missing-model")
except NotFoundError as error:
    check(error.status_code == 404, "Missing model HTTP status was incorrect.")
    check((error.code, error.type, error.param) == ("model_not_found", "invalid_request_error", "model"), "Missing model OpenAI error was incorrect.")
else:
    raise RuntimeError("Missing model did not raise the SDK NotFoundError.")
with OpenAI(api_key="invalid-smoke-key", **options) as invalid_client:
    try:
        invalid_client.models.retrieve(chat_model)
    except AuthenticationError as error:
        check(error.status_code == 401, "Invalid key HTTP status was incorrect.")
    else:
        raise RuntimeError("Invalid key did not raise the SDK AuthenticationError.")

chat_request = {
    "model": chat_model,
    "messages": [{"role": "user", "content": [{"type": "text", "text": "SDK smoke test"}]}],
    "max_completion_tokens": 16,
    "reasoning_effort": "low",
    "parallel_tool_calls": False,
    "modalities": ["text"],
    "prediction": {"type": "content", "content": "ok"},
    "logprobs": True,
    "top_logprobs": 1,
    "service_tier": "default",
    "tool_choice": "none",
}
chat = client.chat.completions.create(**chat_request)
check((chat.object, chat.model) == ("chat.completion", chat_model), "Chat object/model was incorrect.")
check(chat.choices and (chat.choices[0].message.role, chat.choices[0].message.content, chat.choices[0].finish_reason) == ("assistant", "ok", "stop"), "Chat completion was incorrect.")
check_chat_usage(chat.usage)

try:
    client.chat.completions.create(**{**chat_request, "max_completion_tokens": "16"})
except BadRequestError as error:
    check(error.status_code == 400, "Quoted numeric field HTTP status was incorrect.")
else:
    raise RuntimeError("Quoted numeric field did not raise the SDK BadRequestError.")

streamed_chat_text = ""
chat_finish_reason = None
chat_usage = None
chat_chunk_count = 0
with client.chat.completions.create(**chat_request, stream=True, stream_options={"include_usage": True}) as chat_stream:
    for chunk in chat_stream:
        chat_chunk_count += 1
        check((chunk.object, chunk.model) == ("chat.completion.chunk", chat_model), "Chat stream object/model was incorrect.")
        check(isinstance(chunk.id, str) and chunk.id and isinstance(chunk.created, int), "Chat stream ID/timestamp was missing.")
        for choice in chunk.choices:
            check(choice.index == 0, "Chat stream choice index was incorrect.")
            streamed_chat_text += choice.delta.content or ""
            chat_finish_reason = choice.finish_reason or chat_finish_reason
        if chunk.usage:
            check(not chunk.choices, "Usage must be emitted in the final empty-choice chunk.")
        chat_usage = chunk.usage or chat_usage
check(chat_chunk_count >= 2, "Chat stream ended before its completion chunk.")
check((streamed_chat_text, chat_finish_reason) == ("ok", "stop"), "Chat stream completion was incorrect.")
check_chat_usage(chat_usage)

check_response(client.responses.create(model=chat_model, input="SDK smoke test", store=False))
streamed_response_text = ""
response_completed_count = 0
sequence = -1
with client.responses.stream(model=chat_model, input="SDK smoke test", store=False) as response_stream:
    for event in response_stream:
        check(event.sequence_number > sequence, "Responses stream sequence numbers did not increase.")
        sequence = event.sequence_number
        if event.type == "response.output_text.delta":
            streamed_response_text += event.delta
        if event.type == "response.completed":
            response_completed_count += 1
    check_response(response_stream.get_final_response())
check(streamed_response_text == "ok", "Responses stream output text was incorrect.")
check(response_completed_count == 1, "Responses stream was missing its completed event.")

for encoding in (None, "float", "base64"):
    # Omission exercises the SDK's default base64 request and automatic float decoding.
    # Distinct inputs keep each format's provider invocation observable even with caching.
    label = encoding or "default"
    embedding_options = {} if encoding is None else {"encoding_format": encoding}
    embedding = client.embeddings.create(
        model=embedding_model,
        input=f"Python SDK embedding {label}",
        **embedding_options,
    )
    check((embedding.object, embedding.model) == ("list", embedding_model), f"{label} embedding object/model was incorrect.")
    check(len(embedding.data) == 1, f"{label} embedding item count was incorrect.")
    vector = embedding.data[0]
    check((vector.object, vector.index) == ("embedding", 0), f"{label} embedding item was incorrect.")
    check((embedding.usage.prompt_tokens, embedding.usage.total_tokens) == (1, 1), f"{label} embedding usage was incorrect.")
    if encoding == "base64":
        check(isinstance(vector.embedding, str), "Explicit base64 embedding did not remain a string.")
        check(vector.embedding == "zczMPQ==", "Explicit base64 embedding was changed.")
        raw_embedding = base64.b64decode(vector.embedding, validate=True)
        check(len(raw_embedding) == 4, "Base64 embedding must contain exactly one float32.")
        check(math.isclose(struct.unpack("<f", raw_embedding)[0], 0.1, abs_tol=1e-6), "Base64 float32 embedding value was incorrect.")
    else:
        check(isinstance(vector.embedding, list), f"{label} embedding was not decoded to a numeric array.")
        check(len(vector.embedding) == 1 and math.isclose(vector.embedding[0], 0.1, abs_tol=1e-6), f"{label} embedding vector was incorrect.")

image = client.images.generate(model=image_model, prompt="A small blue square", response_format="b64_json")
check(image.created == 1 and image.data and len(image.data) == 1, "Image response was incorrect.")
check(base64.b64decode(image.data[0].b64_json, validate=True).startswith(b"\x89PNG\r\n\x1a\n"), "Image data was not a PNG.")
check(not image.data[0].url, "Base64 image response also exposed a URL.")

speech = client.audio.speech.create(model=speech_model, input="SDK smoke test", voice="alloy", response_format="mp3")
check(speech.response.headers.get("content-type") == "audio/mpeg", "Speech content type was incorrect.")
check(speech.read() == b"ID3\x04\x00\x00", "Speech audio bytes were incorrect.")
client.close()

print("Official Python SDK smoke test passed.")
