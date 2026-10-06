import type { FetchBaseApiClient } from '../client/FetchBaseApiClient';
import { FetchSystemService } from './FetchSystemService';

const mockGetGlobalSetting = jest.fn();
const mockUpdateGlobalSetting = jest.fn();
const mockValidateVirtualKey = jest.fn();
const mockCreateVirtualKey = jest.fn();
const mockDeleteVirtualKey = jest.fn();
const mockListGroups = jest.fn();
const mockCreateGroup = jest.fn();
const mockDeleteGroup = jest.fn();

jest.mock('./FetchSettingsService', () => ({
  FetchSettingsService: jest.fn(() => ({
    getGlobalSetting: mockGetGlobalSetting,
    updateGlobalSetting: mockUpdateGlobalSetting,
  })),
}));

jest.mock('./FetchVirtualKeyService', () => ({
  FetchVirtualKeyService: jest.fn(() => ({
    validate: mockValidateVirtualKey,
    create: mockCreateVirtualKey,
    delete: mockDeleteVirtualKey,
  })),
}));

jest.mock('./FetchVirtualKeyGroupService', () => ({
  FetchVirtualKeyGroupService: jest.fn(() => ({
    list: mockListGroups,
    create: mockCreateGroup,
    delete: mockDeleteGroup,
  })),
}));

describe('FetchSystemService.getWebAdminVirtualKey', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockGetGlobalSetting.mockRejectedValue(new Error('not found'));
    mockListGroups.mockResolvedValue({
      data: [],
      pagination: { page: 1, pageSize: 100, totalItems: 0, totalPages: 0 },
    });
    mockCreateGroup.mockResolvedValue({ id: 7, externalGroupId: 'webadmin-internal' });
    mockCreateVirtualKey.mockResolvedValue({
      virtualKey: 'vk_webadmin',
      keyInfo: { id: 11 },
    });
    mockUpdateGlobalSetting.mockResolvedValue(undefined);
  });

  it('shares one cold-start bootstrap across concurrent service instances', async () => {
    const client = {} as FetchBaseApiClient;
    const firstService = new FetchSystemService(client);
    const secondService = new FetchSystemService(client);

    const [firstKey, secondKey] = await Promise.all([
      firstService.getWebAdminVirtualKey(),
      secondService.getWebAdminVirtualKey(),
    ]);

    expect(firstKey).toBe('vk_webadmin');
    expect(secondKey).toBe('vk_webadmin');
    expect(mockCreateGroup).toHaveBeenCalledTimes(1);
    expect(mockCreateVirtualKey).toHaveBeenCalledTimes(1);
    expect(mockUpdateGlobalSetting).toHaveBeenCalledTimes(1);
  });

  it('reuses an existing internal group instead of funding another one', async () => {
    mockListGroups.mockResolvedValue({
      data: [{ id: 42, externalGroupId: 'webadmin-internal' }],
      pagination: { page: 1, pageSize: 100, totalItems: 1, totalPages: 1 },
    });
    const service = new FetchSystemService({} as FetchBaseApiClient);

    await service.getWebAdminVirtualKey();

    expect(mockCreateGroup).not.toHaveBeenCalled();
    expect(mockCreateVirtualKey).toHaveBeenCalledWith(
      expect.objectContaining({ virtualKeyGroupId: 42 }),
      undefined
    );
  });

  it('loads the winning key and cleans up resources after a settings race', async () => {
    mockGetGlobalSetting
      .mockRejectedValueOnce(new Error('not found'))
      .mockResolvedValueOnce({ value: 'vk_winner' });
    mockUpdateGlobalSetting.mockRejectedValueOnce(new Error('setting already exists'));
    mockValidateVirtualKey.mockResolvedValueOnce({ isValid: true });
    const service = new FetchSystemService({} as FetchBaseApiClient);

    const key = await service.getWebAdminVirtualKey();

    expect(key).toBe('vk_winner');
    expect(mockDeleteVirtualKey).toHaveBeenCalledWith('11', undefined);
    expect(mockDeleteGroup).toHaveBeenCalledWith(7, undefined);
  });
});

describe('FetchSystemService diagnostics contracts', () => {
  it('reads expanded service health from the health-status endpoint', async () => {
    const health = {
      overallStatus: 'degraded',
      timestamp: '2026-07-23T18:00:00Z',
      summary: { healthy: 5, degraded: 1, unhealthy: 0, unknown: 0, total: 6 },
      services: [{
        id: 'core-api',
        name: 'Gateway API',
        status: 'degraded',
        instances: [{
          instanceId: 'gateway-a',
          status: 'healthy',
          version: '3.0.0',
          commitSha: 'abc123',
          buildTimestamp: '2026-07-23T17:00:00Z',
        }],
      }],
    };
    const executeContractRead = jest.fn().mockResolvedValue(health);
    const service = new FetchSystemService({
      executeContractRead,
    } as unknown as FetchBaseApiClient);

    await expect(service.getServiceHealth()).resolves.toEqual(health);
    expect(executeContractRead).toHaveBeenCalledWith(
      '/v1/admin/health-status/services',
      expect.any(Function),
      undefined,
    );
  });

  it('invalidates function discovery through the system metadata endpoint', async () => {
    const result = {
      message: 'accepted',
      timestamp: '2026-07-23T18:00:00Z',
    };
    const executeContractOperation = jest.fn().mockResolvedValue(result);
    const service = new FetchSystemService({
      executeContractOperation,
    } as unknown as FetchBaseApiClient);

    await expect(service.invalidateFunctionDiscoveryCache()).resolves.toEqual(result);
    expect(executeContractOperation).toHaveBeenCalledWith(
      '/v1/admin/system-metadata/cache/invalidate-function-discovery',
      'POST',
      expect.any(Function),
      undefined,
    );
  });
});
