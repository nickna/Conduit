# Undiscoverable migration drafts

These source files never had EF migration/DbContext metadata or designers in the
repository's active migration inventory. They are retained as `.cs.txt` references
and are neither compiled nor applied. Moving them here preserves the existing EF
migration IDs, startup schema gate, and generated deployment SQL.

| Draft | Decision |
| --- | --- |
| `20251119003000_SeedExaParameterSchema` | Archive the unshipped data update. It would overwrite every Exa configuration's parameter schema; enabling it retroactively could destroy operator customizations. |
| `20260319000000_RemoveUnusedCacheConfigurationTables` | Archive the unshipped table drops. Earlier active migrations create the legacy tables; their continued existence is compatible with the current application. Do not remove deployed tables as a side effect of repairing validation. |
| `20260720020000_RemoveLLMCompletionCacheSetting` | Archive the unshipped setting deletion. The current application does not use this setting, so leaving a historical value in an existing database is harmless. |

Do not attach historical migration IDs to these drafts or modify a deployed
`__EFMigrationsHistory` to match them. If an operator has independently applied
one of these drafts, record that deployment's actual schema/history and reconcile
it explicitly. Any future cleanup must be a new, forward migration with upgrade
coverage and a deliberate decision about preserving operator data.

The validator uses EF's authoritative metadata and fails if it cannot obtain an
inventory. It also rejects new timestamped `.cs` sources outside that inventory,
so a future orphan must be reconciled instead of silently becoming a fallback
entry. See [#1455](https://github.com/nickna/Conduit/issues/1455).
