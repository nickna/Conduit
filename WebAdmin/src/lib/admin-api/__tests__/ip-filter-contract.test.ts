import { ConduitAdminClient } from '..';
import type { CacheProvider } from '../client/types';

const mockFetch: jest.MockedFunction<typeof fetch> = jest.fn();
global.fetch = mockFetch;

function response(body?: unknown, status = 200): Response {
  const hasBody = status !== 204;
  let statusText = 'OK';
  if (status === 204) statusText = 'No Content';
  else if (status === 201) statusText = 'Created';
  return {
    ok: status >= 200 && status < 300, status,
    statusText,
    url: 'https://admin.test',
    headers: new Headers(hasBody ? { 'content-type': 'application/json' } : undefined),
    text: async () => hasBody ? JSON.stringify(body) : '',
  } as Response;
}

function body(request: Request): unknown {
  return JSON.parse(Reflect.get(request, 'body') as unknown as string) as unknown;
}

function client(cache?: CacheProvider) {
  return new ConduitAdminClient({ baseUrl: 'https://admin.test', masterKey: 'master-key', retries: 0, cache });
}

const filter = {
  id: 7, name: 'Office', ipAddressOrCidr: '2001:db8::/32', filterType: 'whitelist' as const,
  isEnabled: true, description: null, createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-02-01T00:00:00Z', createdBy: null, updatedBy: null, virtualKeyId: null,
};

const filterPage = {
  data: [filter],
  pagination: { page: 1, pageSize: 100, totalItems: 1, totalPages: 1 },
};

beforeEach(() => mockFetch.mockReset());

describe('IP filter generated operations', () => {
  it.each([
    ['list', '/v1/admin/ip-filters?page=1&pageSize=100', (c: ConduitAdminClient) => c.ipFilters.list()],
    ['enabled list', '/v1/admin/ip-filters/enabled', (c: ConduitAdminClient) => c.ipFilters.getEnabled()],
    ['virtual-key list', '/v1/admin/ip-filters/by-virtual-key/42', (c: ConduitAdminClient) => c.ipFilters.listByVirtualKey(42)],
    ['get', '/v1/admin/ip-filters/7', (c: ConduitAdminClient) => c.ipFilters.getById(7)],
  ])('performs the %s read with master-key authentication', async (name, path, invoke) => {
    expect(name).toBeTruthy();
    mockFetch.mockResolvedValueOnce(response(path.endsWith('/7') ? filter : filterPage));
    await invoke(client());
    const request = mockFetch.mock.calls[0]?.[0] as Request;
    expect(request.method).toBe('GET');
    expect(request.url).toBe(`https://admin.test${path}`);
    expect(request.headers.get('X-Master-Key')).toBe('master-key');
  });

  it('creates with the exact request body and accepts 201', async () => {
    const create = { name: 'Office', ipAddressOrCidr: '2001:db8::/32', filterType: 'whitelist' as const, isEnabled: true };
    mockFetch.mockResolvedValueOnce(response(filter, 201));
    await expect(client().ipFilters.create(create)).resolves.toEqual({
      ...filter,
      description: undefined,
      createdBy: undefined,
      updatedBy: undefined,
    });
    const request = mockFetch.mock.calls[0]?.[0] as Request;
    expect(request.method).toBe('POST');
    expect(body(request)).toEqual(create);
  });

  it('preserves partial update and settings bodies and accepts 204', async () => {
    mockFetch.mockResolvedValue(response(undefined, 204));
    const api = client();
    const update = { isEnabled: false };
    await api.ipFilters.update(7, update);
    await api.ipFilters.updateSettings({ defaultAllow: false });
    const first = mockFetch.mock.calls[0]?.[0] as Request;
    const second = mockFetch.mock.calls[1]?.[0] as Request;
    expect(first.url).toBe('https://admin.test/v1/admin/ip-filters/7');
    expect(first.method).toBe('PATCH');
    expect(body(first)).toEqual({ isEnabled: false });
    expect(second.url).toBe('https://admin.test/v1/admin/ip-filters/settings');
    expect(body(second)).toEqual({ defaultAllow: false });
  });

  it('deletes a rule and accepts 204', async () => {
    mockFetch.mockResolvedValueOnce(response(undefined, 204));
    await client().ipFilters.deleteById(7);
    const request = mockFetch.mock.calls[0]?.[0] as Request;
    expect(request.method).toBe('DELETE');
    expect(request.url).toBe('https://admin.test/v1/admin/ip-filters/7');
  });

  it('reads settings and URL-encodes IPv4 and IPv6 checks', async () => {
    const settings = { isEnabled: true, defaultAllow: false, bypassForAdminUi: false, excludedEndpoints: [], filterMode: 'restrictive', whitelistFilters: [], blacklistFilters: [] };
    mockFetch.mockResolvedValueOnce(response(settings)).mockResolvedValue(response({ isAllowed: true }));
    const api = client();
    await expect(api.ipFilters.getSettings()).resolves.toEqual(settings);
    await api.ipFilters.checkIp('192.168.1.1');
    await api.ipFilters.checkIp('2001:db8::1');
    expect((mockFetch.mock.calls[1]?.[0] as Request).url).toBe('https://admin.test/v1/admin/ip-filters/check/192.168.1.1');
    expect((mockFetch.mock.calls[2]?.[0] as Request).url).toBe('https://admin.test/v1/admin/ip-filters/check/2001%3Adb8%3A%3A1');
  });

  it('caches reads and clears the cache after active enable/disable mutations', async () => {
    const values = new Map<string, unknown>();
    const clearMock = jest.fn(async () => { values.clear(); });
    const cache: CacheProvider = {
      async get<T>(key: string): Promise<T | null> { return (values.get(key) as T | undefined) ?? null; },
      async set<T>(key: string, value: T): Promise<void> { values.set(key, value); },
      async delete(key: string): Promise<void> { values.delete(key); },
      clear: clearMock,
    };
    mockFetch.mockResolvedValueOnce(response(filterPage));
    const api = client(cache);
    await api.ipFilters.list();
    await api.ipFilters.list();
    expect(mockFetch).toHaveBeenCalledTimes(1);

    mockFetch.mockResolvedValue(response(undefined, 204));
    await api.ipFilters.enableFilter(7);
    await api.ipFilters.disableFilter(7);
    expect(clearMock).toHaveBeenCalledTimes(2);
    expect(body(mockFetch.mock.calls[1]?.[0] as Request)).toEqual({ isEnabled: true });
    expect(body(mockFetch.mock.calls[2]?.[0] as Request)).toEqual({ isEnabled: false });
  });
});
