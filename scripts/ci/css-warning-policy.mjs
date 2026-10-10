export function enforceCss(counts, baseline) {
  for (const [key, count] of Object.entries(counts)) {
    if (!Number.isInteger(count) || count < 0 || count > (baseline[key] ?? 0))
      throw new Error(`CSS warning budget exceeded: ${key} ${count} > ${baseline[key] ?? 0}`);
  }
}
