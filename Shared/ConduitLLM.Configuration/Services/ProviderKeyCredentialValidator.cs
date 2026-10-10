using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Validates provider-credential business rules with fixed, statically analyzable
/// query shapes.
/// </summary>
public sealed class ProviderKeyCredentialValidator
{
    private const int MaxKeysPerProvider = 32;

    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly ILogger<ProviderKeyCredentialValidator> _logger;

    public ProviderKeyCredentialValidator(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        ILogger<ProviderKeyCredentialValidator> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates whether another credential can be added to a provider.
    /// </summary>
    public async Task<ConduitValidationResult> ValidateAddKeyAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var currentCount = await context.ProviderKeyCredentials.CountAsync(
            credential => credential.ProviderId == providerId,
            cancellationToken);
        if (currentCount < MaxKeysPerProvider)
        {
            return ConduitValidationResult.Success();
        }

        _logger.LogWarning(
            "Credential add rejected: provider {ProviderId} already has {CurrentCount}/{Maximum} keys",
            providerId,
            currentCount,
            MaxKeysPerProvider);
        return ConduitValidationResult.Failure(
            $"Provider already has the maximum of {MaxKeysPerProvider} keys");
    }

    /// <summary>
    /// Validates that an enabled credential exists before it is selected as primary.
    /// </summary>
    public async Task<ConduitValidationResult> ValidateSetPrimaryAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var credential = await context.ProviderKeyCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning("Set-primary rejected: key {KeyId} not found", id);
            return ConduitValidationResult.Failure("key not found");
        }

        if (!credential.IsEnabled)
        {
            _logger.LogWarning("Set-primary rejected: key {KeyId} is disabled", id);
            return ConduitValidationResult.Failure("Cannot set a disabled key as primary");
        }

        return ConduitValidationResult.Success();
    }

    /// <summary>
    /// Validates that a credential can be disabled.
    /// </summary>
    public async Task<ConduitValidationResult> ValidateDisableKeyAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var credential = await context.ProviderKeyCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning("Disable rejected: key {KeyId} not found", id);
            return ConduitValidationResult.Failure("key not found");
        }

        if (credential.IsPrimary)
        {
            _logger.LogWarning("Disable rejected: key {KeyId} is primary", id);
            return ConduitValidationResult.Failure(
                "Cannot disable a primary key. Set another key as primary first.");
        }

        return ConduitValidationResult.Success();
    }

    /// <summary>
    /// Validates that a provider has at least one enabled credential.
    /// </summary>
    public async Task<ConduitValidationResult> ValidateProviderHasEnabledKeyAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var hasEnabled = await context.ProviderKeyCredentials.AnyAsync(
            credential => credential.ProviderId == providerId && credential.IsEnabled,
            cancellationToken);
        if (hasEnabled)
        {
            return ConduitValidationResult.Success();
        }

        _logger.LogWarning("Validation failed: provider {ProviderId} has no enabled keys", providerId);
        return ConduitValidationResult.Failure("Provider must have at least one enabled key");
    }
}
