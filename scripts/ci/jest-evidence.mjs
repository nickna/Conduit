import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
export function validateJest(result) {
  if (result.numPassedTests < 461 || result.numFailedTests !== 0 || result.numPendingTests !== 0) {
    throw new Error(`WebAdmin discovery/execution regression: passed=${result.numPassedTests}, failed=${result.numFailedTests}, skipped=${result.numPendingTests}`);
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const result = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  console.log(JSON.stringify({ discovered: result.numTotalTests, executed: result.numPassedTests + result.numFailedTests,
    failed: result.numFailedTests, skipped: result.numPendingTests }));
  validateJest(result);
}
