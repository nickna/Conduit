import { predecessorsReady } from './scheduling-policy.mjs';

if (process.env.GITHUB_EVENT_NAME !== 'workflow_dispatch') throw new Error('Scheduling experiment is manual only');
const base = `${process.env.GITHUB_API_URL}/repos/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}/jobs`;
const deadline = Date.now() + 25 * 60_000;
while (true) {
  const jobs = [];
  for (let page = 1; ; page++) {
    const response = await fetch(`${base}?per_page=100&page=${page}`, {
      headers: { Authorization: `Bearer ${process.env.GH_TOKEN}`, Accept: 'application/vnd.github+json' },
      signal: AbortSignal.timeout(30_000)
    });
    if (!response.ok) throw new Error(`Scheduling measurement lookup failed: ${response.status}`);
    const result = await response.json();
    if (!Array.isArray(result.jobs)) throw new Error('Missing measurement job inventory');
    jobs.push(...result.jobs);
    if (result.jobs.length < 100) break;
  }
  if (predecessorsReady(jobs)) break;
  if (Date.now() >= deadline) throw new Error('Previous-scheduling barrier timed out');
  console.log('Controlled before-mode: wait for backend, WebAdmin and contract proof before packaged builds.');
  await new Promise(resolve => setTimeout(resolve, 15_000));
}
