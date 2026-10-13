using ConduitLLM.Configuration.DTOs.VirtualKey;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Services
{
    public partial class AdminVirtualKeyService
    {
        /// <inheritdoc />
        public override async Task<CreateVirtualKeyResponseDto> GenerateVirtualKeyAsync(CreateVirtualKeyRequestDto request)
        {
            await ValidateModelRateLimitsAsync(request.ModelRateLimits);
            return await base.GenerateVirtualKeyAsync(request);
        }

        /// <inheritdoc />
        public override async Task<bool> UpdateVirtualKeyAsync(int id, UpdateVirtualKeyRequestDto request)
        {
            await ValidateModelRateLimitsAsync(request.ModelRateLimits);
            return await base.UpdateVirtualKeyAsync(id, request);
        }

        /// <summary>
        /// Rejects per-model overrides that name aliases nobody can call.
        /// </summary>
        /// <remarks>
        /// A typo in an override is silent at request time — the rule simply never matches, and
        /// the operator believes an expensive model is capped when it is not. Failing the write
        /// is the only point at which the mistake is visible.
        /// </remarks>
        /// <exception cref="ArgumentException">An alias, or a prefix rule's stem, matches no configured model.</exception>
        private async Task ValidateModelRateLimitsAsync(Dictionary<string, ModelRateLimitDto>? modelRateLimits,
            ConduitLLM.Configuration.ConduitDbContext? guardedContext = null, CancellationToken cancellationToken = default)
        {
            if (modelRateLimits is null || modelRateLimits.Count == 0)
            {
                return;
            }

            List<string>? knownAliases = null;

            foreach (var (pattern, rule) in modelRateLimits)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    throw new ArgumentException("A per-model rate limit was configured against an empty model alias.");
                }

                if (rule is null || (rule.Rpm is not > 0 && rule.Tpm is not > 0))
                {
                    throw new ArgumentException(
                        $"The per-model rate limit for '{pattern}' sets neither a requests-per-minute nor a tokens-per-minute ceiling.");
                }

                if (pattern.EndsWith('*'))
                {
                    var prefix = pattern[..^1];
                    if (prefix.Length == 0)
                    {
                        throw new ArgumentException(
                            "A per-model rate limit of '*' would match every model; set the ceiling on the key instead.");
                    }

                    if (knownAliases is null)
                    {
                        await using var ownContext = guardedContext is null ? await _dbContextFactory.CreateDbContextAsync(cancellationToken) : null;
                        var context = guardedContext ?? ownContext!;
                        knownAliases = await context.ModelProviderMappings
                            .AsNoTracking()
                            .Select(m => m.ModelAlias)
                            .ToListAsync(cancellationToken);
                    }

                    if (!knownAliases.Any(alias => alias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new ArgumentException(
                            $"No configured model alias starts with '{prefix}', so the per-model rate limit '{pattern}' would never apply.");
                    }

                    continue;
                }

                if (guardedContext is not null
                    ? !await guardedContext.ModelProviderMappings.AnyAsync(mapping => mapping.ModelAlias == pattern, cancellationToken)
                    : await _modelProviderMappingRepository.GetByModelNameAsync(pattern) is null)
                {
                    throw new ArgumentException(
                        $"'{pattern}' is not a configured model alias, so the per-model rate limit for it would never apply.");
                }
            }
        }
    }
}
