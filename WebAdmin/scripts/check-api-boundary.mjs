import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

import { usesGenericModelTransport } from './api-boundary-rules.mjs';

const root = process.cwd();
const forbidden = [
  '@knn_labs/conduit-admin-client',
  '@knn_labs/conduit-gateway-client',
  '@knn_labs/conduit-common',
  'SDKs/Node/Admin',
  'SDKs\\Node\\Admin',
  'SDKs/Node/Gateway',
  'SDKs\\Node\\Gateway',
  'SDKs/Node/Common',
  'SDKs\\Node\\Common',
];
const violations = [];
const directFetchViolations = [];
const genericModelTransportViolations = [];
const contractNativeModelFamilyServices = new Set([
  'src/lib/admin-api/services/FetchModelAuthorService.ts',
  'src/lib/admin-api/services/FetchModelSeriesService.ts',
  'src/lib/admin-api/services/FetchModelService.ts',
  'src/lib/admin-api/services/FetchModelMappingsService.ts',
  'src/lib/admin-api/services/FetchModelCostService.ts',
  'src/lib/admin-api/services/FetchProvidersService.ts',
  'src/lib/admin-api/services/FetchProvidersServiceKeys.ts',
  'src/lib/admin-api/services/FetchProviderSyncService.ts',
  'src/lib/admin-api/services/FetchProviderErrorsService.ts',
  'src/lib/admin-api/services/FetchVirtualKeyService.ts',
  'src/lib/admin-api/services/FetchVirtualKeyGroupService.ts',
  'src/lib/admin-api/services/FetchAnalyticsService.ts',
  'src/lib/admin-api/services/FetchSettingsService.ts',
  'src/lib/admin-api/services/FetchIpFilterService.ts',
  'src/lib/admin-api/services/FetchFunctionsService.ts',
  'src/lib/admin-api/services/ProviderToolsService.ts',
  'src/lib/admin-api/services/FetchConfigurationService.ts',
  'src/lib/admin-api/services/FetchMediaService.ts',
]);
const retiredLocalNames = [
  'sdk-config',
  'sdkChatStreamingAdapter',
  'errors/sdk-errors',
  'handleSDKError',
  'SDKChatStreamingAdapter',
  "this.client['executeContractRead']",
  "this.client['executeContractOperation']",
  'browserCoreClient',
  'getBrowserCoreClient',
  'getServerCoreClient',
];

async function scan(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await scan(fullPath);
      continue;
    }
    if (!/\.(?:ts|tsx|js|mjs|json)$/.test(entry.name)) continue;
    const contents = await readFile(fullPath, 'utf8');
    if (forbidden.some((value) => contents.includes(value))) {
      violations.push(path.relative(root, fullPath));
    }
    const relative = path.relative(root, fullPath).replaceAll('\\', '/');
    if (
      relative.startsWith('src/lib/admin-api/services/') &&
      /\bfetch\s*\(/.test(contents)
    ) {
      directFetchViolations.push(relative);
    }
    if (
      contractNativeModelFamilyServices.has(relative) &&
      usesGenericModelTransport(contents)
    ) {
      genericModelTransportViolations.push(relative);
    }
    if (retiredLocalNames.some((value) => contents.includes(value))) {
      violations.push(relative);
    }
  }
}

await scan(path.join(root, 'src'));
const packageJson = await readFile(path.join(root, 'package.json'), 'utf8');
if (forbidden.some((value) => packageJson.includes(value))) violations.push('package.json');
const packageLock = await readFile(path.join(root, 'package-lock.json'), 'utf8');
if (forbidden.some((value) => packageLock.includes(value))) violations.push('package-lock.json');

if (violations.length > 0) {
  console.error(`Forbidden SDK coupling found:\n${violations.join('\n')}`);
  process.exit(1);
}

if (directFetchViolations.length > 0) {
  console.error(
    `Admin services must use the contract transport; direct fetch found in:\n${directFetchViolations.join('\n')}`,
  );
  process.exit(1);
}

if (genericModelTransportViolations.length > 0) {
  console.error(
    `Contract-native model-family services must not use generic HTTP transport calls:\n${genericModelTransportViolations.join('\n')}`,
  );
  process.exit(1);
}

console.log('WebAdmin Admin and Gateway API boundaries are local and contract-backed.');
