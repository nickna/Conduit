export function warningKeys(reports) {
  const keys = [];
  for (const report of reports) {
    if (!report.totals || !Array.isArray(report.problems) || report.totals.errors || report.problems.some(p => p.severity === 'error'))
      throw new Error('Redocly error or malformed report');
    if (report.problems.filter(p => p.severity === 'warn').length !== report.totals.warnings)
      throw new Error('Truncated Redocly warning inventory');
    for (const problem of report.problems.filter(p => p.severity === 'warn')) {
      if (!problem.ruleId || !problem.location?.length) throw new Error('Warning lacks rule/location');
      const locations = problem.location.map(l => `${l.source.ref.replaceAll('\\', '/').split('/').pop()}:${l.pointer}`).sort();
      keys.push(`${problem.ruleId}:${locations.join('|')}:${problem.message}`);
    }
  }
  return [...new Set(keys)].sort();
}
export function enforceWarnings(keys, allowed) {
  const added = keys.filter(key => !allowed.includes(key));
  if (added.length) throw new Error(`New Redocly warnings (${added.length}):\n${added.join('\n')}`);
  return { knownWarnings: keys.length, removedWarnings: allowed.filter(key => !keys.includes(key)).length };
}
