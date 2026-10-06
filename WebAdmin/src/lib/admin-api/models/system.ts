import type { components } from '@/generated/admin-api';

// Reconciled to the wire `SystemInfoDto` in issue #1038: the endpoint returns nested
// version/os/database/runtime/recordCounts objects, not the previous flat shape.
export interface VersionInfo {
  appVersion?: string;
  commitSha?: string;
  buildTimestamp?: string;
  buildDate?: string | null;
}

export interface OsInfo {
  description?: string;
  architecture?: string;
}

export interface DatabaseInfo {
  provider?: string;
  version?: string;
  connected?: boolean;
  connectionString?: string;
  location?: string;
  size?: string;
  tableCount?: number | null;
}

export interface RuntimeInfo {
  runtimeVersion?: string;
  startTime?: string;
  uptime?: string;
  /** Customer error visibility mode (CONDUIT_CUSTOMER_MODE): "Internal" | "External" */
  customerMode?: string;
}

export interface RecordCountsDto {
  virtualKeys?: number;
  requests?: number | null;
  settings?: number;
  providers?: number;
  modelMappings?: number;
}

export interface SystemInfoDto {
  version?: VersionInfo;
  operatingSystem?: OsInfo;
  database?: DatabaseInfo;
  runtime?: RuntimeInfo;
  recordCounts?: RecordCountsDto;
}

export type HealthStatusDto = components['schemas']['HealthStatusDto'];
