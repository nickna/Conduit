import {
  buildMessageContent,
  GatewayClient,
  ModelCapability,
  ConflictError,
  NotFoundError,
  RateLimitError,
  type ChatAttachment,
} from "..";
import { RetryStrategyType } from "@/lib/conduit-common";
import { TextDecoder } from "node:util";

Object.assign(globalThis, { TextDecoder });

describe("GatewayClient", () => {
  beforeEach(() => jest.clearAllMocks());
  afterEach(() => jest.restoreAllMocks());

  function jsonResponse(body: unknown, status = 200): Response {
    return {
      ok: status >= 200 && status < 300,
      status,
      statusText: status === 200 ? "OK" : "Error",
      headers: new Headers({ "content-type": "application/json" }),
      text: async () => JSON.stringify(body),
    } as Response;
  }

  it("sends the virtual key as an opaque Bearer token", async () => {
    const fetchMock = jest.spyOn(global, "fetch").mockResolvedValue(
      jsonResponse({ data: [], count: 0 }),
    );
    const client = new GatewayClient({
      apiKey: "vk_test",
      baseURL: "https://gateway.test/",
    });

    await client.discovery.getModelsByCapability(ModelCapability.Chat);

    const [request] = fetchMock.mock.calls[0];
    expect((request as Request).url).toBe(
      "https://gateway.test/v1/conduit/discovery/models?capability=chat",
    );
    expect((request as Request).headers.get("Authorization")).toBe("Bearer vk_test");
  });

  it("uses the supplied virtual key when minting an ephemeral key", async () => {
    const fetchMock = jest.spyOn(global, "fetch").mockResolvedValue(
      jsonResponse({
        ephemeral_key: "ephemeral",
        expires_at: "2026-07-20T00:00:00Z",
        expires_in_seconds: 60,
      }),
    );
    const client = new GatewayClient({
      apiKey: "server-key",
      baseURL: "https://gateway.test",
    });

    await client.auth.generateEphemeralKey("webadmin-key", {
      metadata: { purpose: "test" },
    });

    const [request] = fetchMock.mock.calls[0];
    expect((request as Request).url).toBe("https://gateway.test/v1/conduit/auth/ephemeral-key");
    expect((request as Request).method).toBe("POST");
    expect((request as Request).headers.get("Authorization")).toBe("Bearer webadmin-key");
  });

  it("uses the Conduit chat route and preserves enhanced streaming events", async () => {
    const payload = Buffer.from(
      'event: reasoning\ndata: {"content":"thinking"}\n\n' +
      'event: metrics-final\ndata: {"total_tokens":2}\n\n' +
      'data: [DONE]\n\n',
    );
    let read = false;
    const fetchMock = jest.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      body: {
        getReader: () => ({
          read: async () => {
            if (read) return { done: true, value: undefined };
            read = true;
            return { done: false, value: payload };
          },
        }),
      },
    } as unknown as Response);
    const client = new GatewayClient({ apiKey: "vk_test", baseURL: "https://gateway.test" });

    const stream = await client.chat.create({ model: "chat-model", messages: [], stream: true });
    const events = [];
    for await (const event of stream) events.push(event);

    const [request, init] = fetchMock.mock.calls[0];
    expect(request).toBe("https://gateway.test/v1/conduit/chat/completions");
    expect(new Headers(init?.headers).get("Authorization")).toBe("Bearer vk_test");
    expect(events).toEqual([{ content: "thinking" }, { total_tokens: 2 }]);
  });

  it("uses canonical function fields and the idempotency header", async () => {
    const fetchMock = jest.spyOn(global, "fetch").mockResolvedValue(
      jsonResponse({
        id: "85f88aa3-a3e3-43d3-9e12-f33c4f6dcb36",
        function_id: 7,
        status: "completed",
        input: {},
        output: { answer: "ok" },
        created_at: "2026-07-23T20:00:00Z",
        duration_ms: 12,
        cost: { estimated: "0.001", actual: "0.001", currency: "USD" },
      }),
    );
    const client = new GatewayClient({
      apiKey: "vk_test",
      baseURL: "https://gateway.test",
    });

    const response = await client.functions.execute(
      { function_configuration_id: 7, parameters: {} },
      "idem-123",
    );

    const [request] = fetchMock.mock.calls[0] as [Request];
    expect(request.url).toBe("https://gateway.test/v1/conduit/functions/execute");
    expect(request.headers.get("Idempotency-Key")).toBe("idem-123");
    expect(typeof request.body).toBe("string");
    expect(JSON.parse(request.body as unknown as string)).toEqual({
      function_configuration_id: 7,
      parameters: {},
    });
    expect(response.id).toBe("85f88aa3-a3e3-43d3-9e12-f33c4f6dcb36");
    expect(response.functionId).toBe(7);
    expect(response.status).toBe("completed");
    expect(response.durationMs).toBe(12);
    expect(response.cost.actual).toBe(0.001);
    expect(response.cost.currency).toBe("USD");
  });

  it("validates media limits without issuing a request", () => {
    const client = new GatewayClient({
      apiKey: "key",
      baseURL: "https://gateway.test",
      retries: 0,
    });
    const oversized = { size: 101 * 1024 * 1024, type: "image/png" } as Blob;
    expect(client.media.validateFileSize(oversized, "Image")).toEqual(
      expect.objectContaining({ valid: false }),
    );
  });

  it("normalizes Gateway error responses", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 429,
      statusText: "Too Many Requests",
      headers: new Headers([["retry-after", "7"]]),
      text: async () => JSON.stringify({ error: { message: "Slow down" } }),
    } as Response);
    const client = new GatewayClient({
      apiKey: "key",
      baseURL: "https://gateway.test",
      retries: 0,
    });

    await expect(client.discovery.getModels()).rejects.toEqual(
      expect.objectContaining({
        message: "Slow down",
        retryAfter: 7,
      }),
    );
    await expect(client.discovery.getModels()).rejects.toBeInstanceOf(
      RateLimitError,
    );
  });

  it("retries retryable HTTP responses through the shared contract pipeline", async () => {
    const fetchMock = jest
      .spyOn(global, "fetch")
      .mockResolvedValueOnce(jsonResponse({ error: "Unavailable" }, 503))
      .mockResolvedValueOnce(jsonResponse({ data: [], count: 0 }));
    const client = new GatewayClient({
      apiKey: "key",
      baseURL: "https://gateway.test",
      retryStrategy: {
        type: RetryStrategyType.FIXED_DELAY,
        maxRetries: 1,
        delayMs: 0,
      },
    });

    await expect(client.discovery.getModels()).resolves.toEqual({
      data: [],
      count: 0,
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it.each([
    [404, NotFoundError],
    [409, ConflictError],
  ])("maps Gateway status %i through the common hierarchy", async (status, ErrorType) => {
    jest.spyOn(global, "fetch").mockResolvedValue(
      jsonResponse({ error: { message: "Mapped error" } }, status),
    );
    const client = new GatewayClient({
      apiKey: "key",
      baseURL: "https://gateway.test",
      retries: 0,
    });

    await expect(client.discovery.getModels()).rejects.toBeInstanceOf(ErrorType);
  });

  it("preserves rate-limit scope from Gateway responses", async () => {
    jest.spyOn(global, "fetch").mockResolvedValue({
      ...jsonResponse({ error: { message: "Busy model" } }, 429),
      headers: new Headers([
        ["retry-after", "3"],
        ["x-ratelimit-scope", "model:sora-2:rpm"],
      ]),
    } as Response);
    const client = new GatewayClient({
      apiKey: "key",
      baseURL: "https://gateway.test",
      retries: 0,
    });

    await expect(client.discovery.getModels()).rejects.toEqual(
      expect.objectContaining({
        retryAfter: 3,
        scope: "model:sora-2:rpm",
      }),
    );
  });

  it("builds ordered mixed-modality content without rewriting remote URLs", () => {
    const attachments: ChatAttachment[] = [
      {
        kind: "image",
        url: "https://cdn.example/image.png?signature=unchanged",
        mimeType: "image/png",
        size: 1,
        name: "image.png",
        detail: "high",
      },
      {
        kind: "pdf",
        url: "https://cdn.example/report.pdf",
        mimeType: "application/pdf",
        size: 2,
        name: "report.pdf",
      },
      {
        kind: "audio",
        url: "blob:audio-preview",
        base64: "UklGRg==",
        mimeType: "audio/mpeg",
        size: 3,
        name: "recording.mp3",
      },
      {
        kind: "video",
        url: "https://cdn.example/demo.mp4",
        mimeType: "video/mp4",
        size: 4,
        name: "demo.mp4",
      },
    ];

    expect(buildMessageContent("Describe everything.", attachments)).toEqual([
      { type: "text", text: "Describe everything." },
      {
        type: "image_url",
        image_url: {
          url: "https://cdn.example/image.png?signature=unchanged",
          detail: "high",
        },
      },
      {
        type: "file",
        file: {
          filename: "report.pdf",
          file_data: "https://cdn.example/report.pdf",
        },
      },
      {
        type: "input_audio",
        input_audio: { data: "UklGRg==", format: "mp3" },
      },
      {
        type: "video_url",
        video_url: { url: "https://cdn.example/demo.mp4" },
      },
    ]);
  });

  it("rejects an audio attachment without inline base64 data", () => {
    const attachment: ChatAttachment = {
      kind: "audio",
      url: "https://cdn.example/recording.mp3",
      mimeType: "audio/mpeg",
      size: 3,
      name: "recording.mp3",
    };

    expect(() => buildMessageContent("", [attachment])).toThrow(
      "Audio attachments must contain base64 data.",
    );
  });
});
