import { test } from 'node:test';
import assert from 'node:assert/strict';
import { requireCompatibleSdk } from './native-sdk.mjs';
const global = { sdk: { version: '10.0.400', rollForward: 'latestFeature', allowPrerelease: false } };
function docker(version) { return { admin: `FROM mcr.microsoft.com/dotnet/sdk:${version}-noble AS build\r\n` }; }
test('accepts current patch and higher compatible feature bands', () => {
  for (const version of ['10.0.400', '10.0.401', '10.0.500']) requireCompatibleSdk(global, docker(version));
});
test('rejects the audited 10.0.302 drift and incompatible versions', () => {
  for (const version of ['10.0.302', '9.0.400', '11.0.400', '10.1.400', '10.0.400-preview.1', '10.0']) {
    assert.throws(() => requireCompatibleSdk(global, docker(version)));
  }
});
test('checks both Dockerfiles', () => assert.throws(() => requireCompatibleSdk(global, {
  ...docker('10.0.401'), gateway: docker('10.0.302').admin,
}), /gateway/));
