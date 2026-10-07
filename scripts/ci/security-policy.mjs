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
function validApproval(value) {
  try {
    const url = new URL(value);
    // Reject normalization (whitespace, dot segments, credentials, default ports),
    // queries and trailing path text; only explicit GitHub approval anchors apply.
    if (url.origin !== 'https://github.com' || value !== `${url.origin}${url.pathname}${url.hash}` ||
        !/^\/nickna\/Conduit\/(?:issues|pull)\/[1-9]\d*$/.test(url.pathname)) return false;
    return !url.hash || /^#issuecomment-[1-9]\d*$/.test(url.hash) ||
      (url.pathname.startsWith('/nickna/Conduit/pull/') && /^#(?:discussion_r|pullrequestreview-)[1-9]\d*$/.test(url.hash));
  } catch { return false; }
}

function expiryBoundary(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return NaN;
  const date = new Date(`${value}T00:00:00.000Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value) return NaN;
  return date.getTime() + 86_400_000;
}

export function enforce(findings, exceptions, mode = 'dependency', now = new Date()) {
  if (!(now instanceof Date) || !Number.isFinite(now.getTime())) throw new Error('Require a valid policy evaluation time');
  const fields = ['scope', 'package', 'version', 'advisory'];
  for (const exception of exceptions) {
    const expiry = expiryBoundary(exception.expires);
    if ([...fields, 'owner', 'reason', 'expires', 'approval'].some(f => typeof exception[f] !== 'string' || !exception[f].trim()) ||
        !validApproval(exception.approval) ||
        (Object.hasOwn(exception, 'developmentOnly') && typeof exception.developmentOnly !== 'boolean') ||
        !Number.isFinite(expiry) || now.getTime() >= expiry)
      throw new Error('Security exception needs exact identity, owner, reason, unexpired date and approval record');
  }
  const isExcepted = finding => exceptions.some(exception => fields.every(field => exception[field] === finding[field]) &&
    (exception.developmentOnly !== true || finding.developmentOnly === true));
  const blocked = findings.filter(f => (mode === 'image' ? f.severity === 'critical' : ['critical', 'high'].includes(f.severity)) &&
    !isExcepted(f));
  if (blocked.length) throw new Error(`Security policy blocked ${blocked.length} findings:\n${blocked.map(f => JSON.stringify(f)).join('\n')}`);
  return { checked: findings.length, exceptions: findings.filter(isExcepted).length };
}
