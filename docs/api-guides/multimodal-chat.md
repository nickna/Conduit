# Multimodal chat inputs

The Gateway accepts ordered content parts in `messages[].content` and preserves their order
when forwarding the request. `/v1/chat/completions` supports the OpenAI `text`, `image_url`,
`input_audio`, `file`, and `refusal` parts. Image, audio, and file parts belong to user messages;
refusal parts belong to assistant messages. Malformed parts return an OpenAI-shaped 400 error.

Use `/v1/conduit/chat/completions` for video, additional audio formats, and other provider
content types. Its contract describes these extensions separately. Provider parts require a
nonempty `type`; known parts still require their documented fields. The Gateway does not
download caller-provided URLs.

OpenRouter is the reference implementation for the full contract. Other providers receive the
same ordered content when their OpenAI-compatible endpoint supports it; unsupported modalities
produce an error rather than being converted to text.

## Content parts

Send images and videos as public HTTPS URLs or base64 data URLs:

```json
{
  "type": "image_url",
  "image_url": {
    "url": "https://example.com/diagram.png",
    "detail": "auto"
  }
}
```

```json
{
  "type": "video_url",
  "video_url": {
    "url": "data:video/mp4;base64,AAAA..."
  }
}
```

Send PDFs using `file_data` with either an HTTPS URL or an
`application/pdf` base64 data URL. A provider-issued `file_id` may be used instead:

```json
{
  "type": "file",
  "file": {
    "filename": "report.pdf",
    "file_data": "https://example.com/report.pdf"
  }
}
```

```json
{
  "type": "file",
  "file": {
    "file_id": "file-provider-id"
  }
}
```

Audio uses raw base64, not a data URL or remote URL:

```json
{
  "type": "input_audio",
  "input_audio": {
    "data": "UklGRg...",
    "format": "wav"
  }
}
```

The standard route supports `wav` and `mp3`. The Conduit route also supports provider formats
`aiff`, `aac`, `ogg`, `flac`, `m4a`, `pcm16`, and `pcm24`. Inline images support PNG, JPEG, WebP,
and GIF. Inline video supports MP4, MPEG,
QuickTime/MOV, and WebM.

## Complete mixed request

The following request asks about media in a deliberate order:

```bash
curl https://your-conduit.example/v1/conduit/chat/completions \
  -H "Authorization: Bearer $CONDUIT_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "openrouter-model-alias",
    "messages": [{
      "role": "user",
      "content": [
        {"type": "text", "text": "Compare these inputs in order."},
        {"type": "image_url", "image_url": {"url": "https://example.com/chart.png"}},
        {"type": "file", "file": {"filename": "notes.pdf", "file_data": "https://example.com/notes.pdf"}},
        {"type": "input_audio", "input_audio": {"data": "UklGRg...", "format": "wav"}},
        {"type": "video_url", "video_url": {"url": "https://example.com/demo.mp4"}}
      ]
    }]
  }'
```

Unknown fields on messages, responses, deltas, and content parts are preserved for provider
extensions. Unknown content-part types are accepted only on the Conduit route; OpenRouter
validation rejects types it does not support with a 400 response.

Both chat routes support `tool_choice` modes `none`, `auto`, and `required`, or a named function
choice (`{"type":"function","function":{"name":"my_function"}}`). Other tool-choice
discriminators and malformed function choices return an OpenAI-shaped 400 error.

## PDF parsing and reuse

OpenRouter can parse PDFs for models without native file support. Select an engine with its
`file-parser` plugin:

```json
{
  "plugins": [
    {
      "id": "file-parser",
      "pdf": {
        "engine": "cloudflare-ai"
      }
    }
  ]
}
```

Valid engines are `native`, `cloudflare-ai`, and `mistral-ocr`. The Mistral OCR engine can add a
per-page provider charge. Successful responses retain OpenRouter `file` annotations, including
the parsed content. Reusing the returned annotation in a later request avoids parsing the same
file again:

```json
{
  "role": "assistant",
  "content": "The report describes...",
  "annotations": [
    {
      "type": "file",
      "file": {
        "hash": "provider-hash",
        "name": "notes.pdf",
        "content": [
          {"type": "text", "text": "Parsed document text..."}
        ]
      }
    }
  ]
}
```

When OpenRouter reports a file-processing failure, the Gateway retains
`error.metadata.file_annotations` while redacting media payloads from the public error message.

## Capabilities, limits, and security

Discovery reports `file_input` only for native file support. It reports `pdf_input` when PDF input
is effective, including OpenRouter's parser fallback. Use `pdf_input` when deciding whether to show
a PDF picker.

The Gateway accepts only HTTPS remote media URLs; it does not fetch them or follow redirects.
Inline media is limited to 20 MiB decoded per part and 50 MiB decoded across the request. Invalid
base64, MIME types, audio formats, and file-source combinations are rejected before the provider
call. Logs and public transport errors redact base64 data and signed URL query strings.
