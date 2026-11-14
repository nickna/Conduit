import { test } from 'node:test';
import assert from 'node:assert/strict';
import { requireCompatibleSdk } from './native-sdk.mjs';
const global = { sdk: { version: '11.0.100', rollForward: 'latestFeature', allowPrerelease: false } };
function docker(version) { return { admin: `FROM mcr.microsoft.com/dotnet/sdk:${version}-resolute@sha256:${'a'.repeat(64)} AS build\r\n` }; }
test('accepts current patch and higher compatible feature bands', () => {
  for (const version of ['11.0.100', '11.0.101', '11.0.200']) requireCompatibleSdk(global, docker(version));
});
test('rejects lower feature bands and incompatible versions', () => {
  for (const version of ['11.0.99', '10.0.404', '12.0.100', '11.1.100', '11.0.100-preview.1', '11.0']) {
    assert.throws(() => requireCompatibleSdk(global, docker(version)));
  }
});
test('checks both Dockerfiles', () => assert.throws(() => requireCompatibleSdk(global, {
  ...docker('11.0.101'), gateway: docker('10.0.404').admin,
}), /gateway/));

test('rejects a different OS family and unstable SDK policy', () => {
  assert.throws(() => requireCompatibleSdk(global, {
    admin: 'FROM mcr.microsoft.com/dotnet/sdk:11.0.100-noble AS build\n',
  }), /resolute/);
  for (const sdk of [{ ...global.sdk, allowPrerelease: true }, { ...global.sdk, rollForward: 'latestMajor' }]) {
    assert.throws(() => requireCompatibleSdk({ sdk }, docker('11.0.100')), /policy/);
  }
});

test('rejects a floating or malformed SDK digest', () => {
  for (const line of [docker('11.0.100').admin.replace(/@sha256:[a-f0-9]{64}/, ''),
    docker('11.0.100').admin.replace('a'.repeat(64), 'a'.repeat(63))]) {
    assert.throws(() => requireCompatibleSdk(global, { admin: line }), /digest/);
  }
});
