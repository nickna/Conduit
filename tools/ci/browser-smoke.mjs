import { chromium } from 'playwright';
import { generateKeyPairSync, sign } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';

// A fresh signing key exercises Clerk's supported offline JWT verification.
// These credentials exist only for this isolated deployment; no auth bypass is used.
export async function smoke({ start, admin, env, master, output, report }) {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const frontend = 'ci-conduit.clerk.accounts.dev';
  const webadmin = start('webadmin', process.env.CI_WEBADMIN_IMAGE ?? 'conduit-webadmin:ci', 3000, {
    ...env, CONDUIT_ADMIN_API_BASE_URL: 'http://admin:8080', CONDUIT_API_BASE_URL: 'http://gateway:8080',
    CONDUIT_ADMIN_API_EXTERNAL_URL: admin,
    NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY: `pk_test_${Buffer.from(`${frontend}$`).toString('base64')}`,
    CLERK_SECRET_KEY: 'sk_test_Y2ktb2ZmbGluZS1pc29sYXRlZC1zaWduaW5nLW9ubHk',
    CLERK_JWT_KEY: publicKey.export({ type: 'spki', format: 'pem' }),
    NEXT_PUBLIC_CLERK_SIGN_IN_URL: '/sign-in', NEXT_PUBLIC_CLERK_SIGN_UP_URL: '/sign-up'
  });
  const deadline = Date.now() + 90_000;
  while (true) {
    try { if ((await fetch(`${webadmin}/api/health`)).status === 200) break; } catch { /* startup */ }
    assert.ok(Date.now() < deadline, 'WebAdmin packaged health endpoint did not start');
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  function token(adminRole = true) {
    const now = Math.floor(Date.now() / 1000);
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    const payload = `${encode({ alg: 'RS256', typ: 'JWT', kid: 'ci-isolated' })}.${encode({
      iss: `https://${frontend}`, sub: 'user_ci', sid: 'sess_ci', azp: webadmin,
      iat: now, nbf: now - 10, exp: now + 600, metadata: { siteadmin: adminRole }
    })}`;
    return `${payload}.${sign('RSA-SHA256', Buffer.from(payload), privateKey).toString('base64url')}`;
  }
  const unauthenticated = await fetch(`${webadmin}/virtualkeys`, { redirect: 'manual' });
  assert.ok([302, 303, 307, 308, 401].includes(unauthenticated.status), `Unsigned session: ${unauthenticated.status}`);
  const denied = await fetch(`${webadmin}/virtualkeys`, { redirect: 'manual', headers: { Authorization: `Bearer ${token(false)}` } });
  assert.ok(denied.headers.get('location')?.includes('/access-denied'), `Non-admin session: ${denied.status}`);
  const session = token();
  const parts = session.split('.');
  parts[2] = (parts[2][0] === 'A' ? 'B' : 'A') + parts[2].slice(1);
  const forged = await fetch(`${webadmin}/virtualkeys`, { redirect: 'manual', headers: { Authorization: `Bearer ${parts.join('.')}` } });
  assert.ok([302, 303, 307, 308, 400, 401, 403].includes(forged.status), `A forged session must not authenticate: ${forged.status}`);
  report.checks.push('Unsigned, forged, and non-admin sessions rejected');

  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${session}` } });
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    const response = await page.goto(`${webadmin}/virtualkeys`, { waitUntil: 'domcontentloaded', timeout: 30_000 });
    assert.equal(response.status(), 200);
    await page.getByRole('heading', { name: 'Virtual Keys', exact: true }).waitFor({ timeout: 30_000 });
    await page.getByText('CI business key', { exact: true }).waitFor({ timeout: 30_000 });
    await page.getByRole('button', { name: 'Create Virtual Key', exact: true }).click();
    await page.getByRole('dialog').waitFor();
    const issuance = await page.evaluate(async () => {
      const response = await fetch('/api/auth/ephemeral-master-key', { method: 'POST' });
      return { status: response.status, data: await response.json() };
    });
    assert.equal(issuance.status, 200, JSON.stringify(issuance.data));
    assert.notEqual(issuance.data.ephemeralMasterKey, master, 'Production must never disclose the permanent master key');
    const credential = issuance.data.ephemeralMasterKey;
    const resource = `${issuance.data.adminApiUrl}/v1/admin/virtual-keys`;
    const roundTrip = await page.evaluate(async ({ resource, credential }) => {
      const call = () => fetch(resource, { headers: { 'X-Master-Key': credential } });
      const first = await call(); const replay = await call();
      return { status: first.status, replay: replay.status, data: await first.json() };
    }, { resource, credential });
    assert.equal(roundTrip.status, 200, JSON.stringify(roundTrip.data));
    assert.ok([401, 403].includes(roundTrip.replay), 'Ephemeral master keys must be single use');
    await context.setExtraHTTPHeaders({});
    const signedOut = await context.request.get(`${webadmin}/virtualkeys`, { maxRedirects: 0 });
    assert.ok([302, 303, 307, 308, 401].includes(signedOut.status()), 'Removing the session must revoke page access');
    report.checks.push('Packaged browser renders real keys and opens creation flow', 'Browser uses single-use Admin credential');
    report.browser = { engine: 'Chromium', version: browser.version(), pageErrors: errors,
      navigation: await page.evaluate(() => performance.getEntriesByType('navigation')[0]?.toJSON()) };
    writeFileSync(`${output}/browser-performance.json`, JSON.stringify(report.browser, null, 2));
  } finally {
    await page.screenshot({ path: `${output}/browser.png`, fullPage: true });
    await context.tracing.stop({ path: `${output}/browser-trace.zip` });
    await browser.close();
  }
}
