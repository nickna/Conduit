import { parseTag } from './release-policy.mjs';

export const services = ['admin', 'http', 'webadmin'];
export function imageSet(records) {
  if (records.length !== 3 || services.some(service => records.filter(r => r.service === service).length !== 1))
    throw new Error('Require exactly one Admin, Gateway and WebAdmin candidate');
  const { version, sha } = records[0];
  const { channel } = parseTag(`v${version}`);
  if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error('Invalid source SHA');
  for (const r of records) {
    if (r.version !== version || r.sha !== sha || r.image !== `ghcr.io/nickna/conduit-${r.service}` ||
        !/^sha256:[a-f0-9]{64}$/.test(r.digest)) throw new Error('Inconsistent candidate identity');
  }
  return { version, sha, channel, images: services.map(service => records.find(r => r.service === service)) };
}

export function compareVersions(a, b) {
  parseTag(`v${a}`); parseTag(`v${b}`);
  const [av, ap] = a.split(/-(.*)/s), [bv, bp] = b.split(/-(.*)/s);
  for (let i = 0; i < 3; i++) {
    const x = BigInt(av.split('.')[i]), y = BigInt(bv.split('.')[i]);
    if (x !== y) return x < y ? -1 : 1;
  }
  if (ap === bp) return 0;
  if (ap === undefined) return 1;
  if (bp === undefined) return -1;
  const ax = ap.split('.'), bx = bp.split('.');
  for (let i = 0; i < Math.max(ax.length, bx.length); i++) {
    if (ax[i] === undefined) return -1;
    if (bx[i] === undefined) return 1;
    if (ax[i] === bx[i]) continue;
    const an = /^\d+$/.test(ax[i]), bn = /^\d+$/.test(bx[i]);
    if (an && bn) return BigInt(ax[i]) < BigInt(bx[i]) ? -1 : 1;
    if (an !== bn) return an ? -1 : 1;
    return ax[i] < bx[i] ? -1 : 1;
  }
  return 0;
}

async function candidates(set, registry) {
  const identity = imageSet(set.images); // Revalidate artifacts at every boundary.
  if (identity.version !== set.version || identity.sha !== set.sha || identity.channel !== set.channel)
    throw new Error('Image set metadata disagrees with its candidates');
  for (const image of set.images) {
    const ref = `${image.image}@${image.digest}`;
    if (await registry.digest(ref) !== image.digest) throw new Error(`Candidate missing: ${ref}`);
    const labels = await registry.labels(ref);
    if (labels['org.opencontainers.image.version'] !== set.version || labels['org.opencontainers.image.revision'] !== set.sha)
      throw new Error(`Candidate labels disagree: ${ref}`);
    const versionDigest = await registry.digest(`${image.image}:${set.version}`);
    if (versionDigest && versionDigest !== image.digest) throw new Error('Immutable version tag already has another digest');
  }
}

export async function preflight(set, registry) {
  await candidates(set, registry);
  const previous = {};
  const versions = new Set();
  for (const image of set.images) {
    const digest = await registry.digest(`${image.image}:${set.channel}`);
    previous[image.service] = digest;
    if (!digest) continue;
    const labels = await registry.labels(`${image.image}@${digest}`);
    const version = labels['org.opencontainers.image.version'];
    if (!version) throw new Error('Channel lacks verified version labels; operator adoption/recovery required');
    if (parseTag(`v${version}`).channel !== set.channel || compareVersions(version, set.version) > 0)
      throw new Error('Refusing to move channel backward or across release channels');
    versions.add(version);
  }
  if (versions.size > 1 || (Object.values(previous).some(Boolean) && Object.values(previous).some(d => !d)))
    throw new Error('Partial channel detected; recover the recorded image set before a new release');
  return { ...set, previous, migrationSucceeded: false, state: 'preflight', events: [] };
}

export async function promote(ledger, registry, save = () => {}) {
  if (!ledger.migrationSucceeded) throw new Error('Migration has not succeeded');
  await candidates(ledger, registry);
  // Recovery may see the old set, the desired set, or a mixture of those two only.
  // An intervening release makes this ledger stale and must never be overwritten.
  for (const image of ledger.images) {
    const current = await registry.digest(`${image.image}:${ledger.channel}`);
    if (current !== ledger.previous[image.service] && current !== image.digest)
      throw new Error('Stale recovery ledger: channel changed since preflight');
  }
  async function copy(image, tag, digest) {
    const target = `${image.image}:${tag}`, source = `${image.image}@${digest}`;
    ledger.events.push({ at: new Date().toISOString(), target, digest, status: 'started' }); save(ledger);
    await registry.copy(source, target);
    if (await registry.digest(target) !== digest) throw new Error(`Digest verification failed: ${target}`);
    ledger.events.push({ at: new Date().toISOString(), target, digest, status: 'verified' }); save(ledger);
  }
  try {
    ledger.state = 'promoting'; save(ledger);
    for (const image of ledger.images) await copy(image, ledger.version, image.digest);
    for (const image of ledger.images) await copy(image, ledger.channel, image.digest);
    for (const image of ledger.images) {
      for (const tag of [ledger.version, ledger.channel]) {
        if (await registry.digest(`${image.image}:${tag}`) !== image.digest) throw new Error('Final image set verification failed');
      }
    }
    ledger.state = 'promoted'; save(ledger);
  } catch (error) {
    ledger.failure = error.message;
    ledger.state = 'recovery-required'; save(ledger);
    if (Object.values(ledger.previous).every(Boolean)) {
      try {
        for (const image of ledger.images) await copy(image, ledger.channel, ledger.previous[image.service]);
        ledger.state = 'rolled-back'; save(ledger);
      } catch (rollback) { ledger.rollbackFailure = rollback.message; save(ledger); }
    }
    throw error;
  }
  return ledger;
}
