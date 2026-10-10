export function parseTag(tag) {
  const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/.exec(tag);
  if (!match || match[4]?.split('.').some(part => /^\d+$/.test(part) && part.length > 1 && part[0] === '0')) {
    throw new Error(`Invalid release tag: ${tag}. Expected vMAJOR.MINOR.PATCH[-PRERELEASE], without build metadata.`);
  }
  return { version: tag.slice(1), channel: match[4] ? 'beta' : 'latest', prerelease: Boolean(match[4]) };
}

export function validatedRun(runs, sha) {
  const eligible = runs.filter(run => run.head_sha === sha && run.head_branch === 'master' && run.event === 'push')
    .sort((a, b) => b.id - a.id);
  const latest = eligible[0];
  if (!latest) return null;
  if (latest.status !== 'completed') return null;
  if (latest.conclusion !== 'success') throw new Error(`CI run ${latest.id} for ${sha}: ${latest.conclusion}`);
  return latest;
}

export function requireAggregate(jobs) {
  const aggregate = jobs.filter(job => job.name === 'CI required');
  if (aggregate.length !== 1 || aggregate[0].conclusion !== 'success') {
    throw new Error('Exact-SHA CI required check is missing or unsuccessful');
  }
}
