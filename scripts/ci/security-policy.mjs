export function npmFindings(audit, lock, scope) {
  if (audit.auditReportVersion !== 2 || !audit.vulnerabilities || audit.error) throw new Error('Invalid or failed npm audit');
  const findings = [];
  for (const vulnerability of Object.values(audit.vulnerabilities)) {
    for (const advisory of vulnerability.via.filter(v => typeof v === 'object')) {
      for (const node of vulnerability.nodes) {
        const version = lock.packages[node]?.version;
        if (!version) throw new Error(`Audit package missing from lock: ${node}`);
        findings.push({ scope, package: vulnerability.name, version, advisory: advisory.url, severity: advisory.severity.toLowerCase(),
          developmentOnly: lock.packages[node].dev === true });
      }
    }
  }
  return findings;
}
export function nugetFindings(audit) {
  if (audit.version !== 1 || !Array.isArray(audit.projects) || audit.projects.length === 0 ||
      audit.projects.some(p => p.logs?.some(l => l.level?.toLowerCase() === 'error')))
    throw new Error('Invalid or failed NuGet audit');
  return audit.projects.flatMap(project => (project.frameworks ?? []).flatMap(framework =>
    [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])].flatMap(pkg =>
      (pkg.vulnerabilities ?? []).map(v => ({ scope: 'nuget', package: pkg.id, version: pkg.resolvedVersion,
        advisory: v.advisoryurl, severity: v.severity.toLowerCase() })))));
}
export function imageFindings(report, scope) {
  if (!Array.isArray(report.Results) || !report.ArtifactName?.includes('@sha256:')) throw new Error('Require a digest-addressed Trivy report');
  return report.Results.flatMap(result => (result.Vulnerabilities ?? []).map(v => ({ scope, package: v.PkgName,
    version: v.InstalledVersion, advisory: v.VulnerabilityID, severity: v.Severity.toLowerCase(), fixedVersion: v.FixedVersion })));
}
export function enforce(findings, exceptions, mode = 'dependency', now = new Date()) {
  const fields = ['scope', 'package', 'version', 'advisory'];
  for (const exception of exceptions) {
    if ([...fields, 'owner', 'reason', 'expires', 'approval'].some(f => typeof exception[f] !== 'string' || !exception[f].trim()) ||
        !/^https:\/\/github.com\/nickna\/Conduit\/(?:pull|issues)\/\d+/.test(exception.approval) ||
        (Object.hasOwn(exception, 'developmentOnly') && typeof exception.developmentOnly !== 'boolean') ||
        !/^\d{4}-\d{2}-\d{2}$/.test(exception.expires) || !Number.isFinite(Date.parse(exception.expires)) ||
        new Date(`${exception.expires}T23:59:59Z`) < now)
      throw new Error('Security exception needs exact identity, owner, reason, unexpired date and approval record');
  }
  const isExcepted = finding => exceptions.some(exception => fields.every(field => exception[field] === finding[field]) &&
    (exception.developmentOnly !== true || finding.developmentOnly === true));
  const blocked = findings.filter(f => (mode === 'image' ? f.severity === 'critical' : ['critical', 'high'].includes(f.severity)) &&
    !isExcepted(f));
  if (blocked.length) throw new Error(`Security policy blocked ${blocked.length} findings:\n${blocked.map(f => JSON.stringify(f)).join('\n')}`);
  return { checked: findings.length, exceptions: findings.filter(isExcepted).length };
}
