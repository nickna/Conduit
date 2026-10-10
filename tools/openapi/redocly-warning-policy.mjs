export function warningKeys(reports) {
  const keys = [];
  for (const report of reports) {
    if (!report.totals || !Array.isArray(report.problems) ||
        !Number.isInteger(report.totals.errors) || report.totals.errors !== 0 ||
        !Number.isInteger(report.totals.warnings) || report.totals.warnings < 0 ||
        report.problems.some(p => !p || !['warn', 'error'].includes(p.severity)) ||
        report.problems.some(p => p.severity === 'error'))
      throw new Error('Redocly error or malformed report');
    if (report.problems.filter(p => p.severity === 'warn').length !== report.totals.warnings)
      throw new Error('Truncated Redocly warning inventory');
    for (const problem of report.problems.filter(p => p.severity === 'warn')) {
      if (typeof problem.ruleId !== 'string' || !problem.ruleId ||
          typeof problem.message !== 'string' || !problem.message ||
          !Array.isArray(problem.location) || !problem.location.length ||
          problem.location.some(l => typeof l.source?.ref !== 'string' || !l.source.ref ||
            typeof l.pointer !== 'string' || !l.pointer)) throw new Error('Warning lacks rule/location/message');
      const locations = problem.location.map(l => `${l.source.ref.replaceAll('\\', '/').split('/').pop()}:${l.pointer}`).sort();
      keys.push(`${problem.ruleId}:${locations.join('|')}:${problem.message}`);
    }
  }
  return [...new Set(keys)].sort();
}
export function enforceWarnings(keys) {
  if (keys.length) throw new Error(`Redocly warnings are not allowed (${keys.length}):\n${keys.join('\n')}`);
  return { warnings: 0 };
}
