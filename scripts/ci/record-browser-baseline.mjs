import { readFileSync, writeFileSync } from 'node:fs';
import { validateBrowserBaseline } from './browser-performance-policy.mjs';

const report = JSON.parse(readFileSync(process.argv[2], 'utf8'));
validateBrowserBaseline(report);
delete report.comparison;
writeFileSync(new URL('./browser-performance-baseline.json', import.meta.url), JSON.stringify(report, null, 2) + '\n');
console.log(`Recorded controlled browser reference from ${report.source.runUrl}`);
