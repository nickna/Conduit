export type FilterType = 'whitelist' | 'blacklist';
export type FilterMode = 'permissive' | 'restrictive';

export interface IpFilterDto {
  id: number;
  name: string;
  ipAddressOrCidr: string;
  filterType: FilterType;
  isEnabled: boolean;
  description?: string;
  createdAt: string;
  updatedAt: string;
  createdBy?: string;
  updatedBy?: string;
  virtualKeyId?: number | null;
}

export interface CreateIpFilterDto {
  name: string;
  ipAddressOrCidr: string;
  filterType: FilterType;
  isEnabled?: boolean;
  description?: string;
  virtualKeyId?: number | null;
}

export interface UpdateIpFilterDto {
  name?: string;
  ipAddressOrCidr?: string;
  filterType?: FilterType;
  isEnabled?: boolean;
  description?: string;
}

export interface IpFilterSettingsDto {
  isEnabled: boolean;
  defaultAllow: boolean;
  bypassForAdminUi: boolean;
  excludedEndpoints: string[];
  filterMode: FilterMode;
  whitelistFilters: IpFilterDto[];
  blacklistFilters: IpFilterDto[];
}

export interface UpdateIpFilterSettingsDto {
  isEnabled?: boolean;
  defaultAllow?: boolean;
  bypassForAdminUi?: boolean;
  excludedEndpoints?: string[];
  filterMode?: FilterMode;
  ipv6Enabled?: boolean;
}

export interface IpCheckResult {
  isAllowed: boolean;
  deniedReason?: string | null;
}
