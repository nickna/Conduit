using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

public partial class FunctionExecutionService
{
    private static readonly TimeSpan KeyedExecutionTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Adds durable idempotency to the repository-backed service without changing its
    /// original public constructor or permitting a keyed request to fall back to metadata only.
    /// </summary>
    public static FunctionExecutionService CreateWithIdempotency(
        IFunctionConfigurationRepository functionConfigurationRepository,
        IFunctionCredentialRepository credentialRepository,
        IFunctionExecutionRepository executionRepository,
        IFunctionCostCalculationService costCalculationService,
        IFunctionClientFactory clientFactory,
        IFunctionCredentialProtector credentialProtector,
        ILogger<FunctionExecutionService> logger,
        IFunctionExecutionIdempotencyStore idempotencyStore) =>
        new(functionConfigurationRepository, credentialRepository, executionRepository,
            costCalculationService, clientFactory, credentialProtector, logger)
        {
            _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore))
        };

    private async Task<FunctionExecution> ExecuteIdempotentAsync(
        int functionConfigurationId,
        int virtualKeyId,
        Dictionary<string, object> parameters,
        string idempotencyKey,
        Dictionary<string, object>? metadata,
        string? providerToolName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new FunctionIdempotencyException(400, "invalid_idempotency_key", Guid.Empty,
                "Idempotency-Key must contain 1 to 200 nonblank characters.");

        var store = _idempotencyStore ?? throw IdempotencyUnavailable(Guid.Empty);
        var fingerprint = ComputeFingerprint(functionConfigurationId, parameters, metadata, providerToolName);
        var keyHash = Hash(Encoding.UTF8.GetBytes(idempotencyKey));
        FunctionExecutionClaim claim;
        try
        {
            claim = await store.ClaimAsync(virtualKeyId, keyHash, fingerprint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not acquire a durable function claim for virtual key {VirtualKeyId}", virtualKeyId);
            throw IdempotencyUnavailable(Guid.Empty);
        }

        if (!string.Equals(claim.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new FunctionIdempotencyException(409, "idempotency_conflict", claim.ExecutionId,
                "This Idempotency-Key is already associated with a different function request.");

        // Once claimed, disconnecting the HTTP client cannot cancel the provider call or
        // its durable result/billing. A bounded server-owned token prevents an endless call.
        using var executionDeadline = new CancellationTokenSource(KeyedExecutionTimeout);
        var token = executionDeadline.Token;
        if (!claim.IsOwner)
        {
            if (claim.State != FunctionExecutionClaimState.Completed)
            {
                var uncertain = claim.State == FunctionExecutionClaimState.Uncertain ||
                    DateTime.UtcNow - claim.CreatedAtUtc >= KeyedExecutionTimeout;
                throw new FunctionIdempotencyException(409,
                    uncertain ? "idempotency_outcome_unknown" : "idempotency_in_progress", claim.ExecutionId,
                    uncertain
                        ? "This function request has an uncertain outcome and will not be dispatched again. Inspect its execution before taking further action."
                        : "This function request is already in progress. Retry with the same key to read its result.");
            }

            FunctionExecution replay;
            try
            {
                replay = claim.ResultJson is { } resultJson
                    ? JsonSerializer.Deserialize(resultJson, FunctionsJsonContext.Default.FunctionExecution)
                        ?? throw new InvalidOperationException("The completed claim has no execution snapshot.")
                    : throw new InvalidOperationException("The completed claim has no execution snapshot.");
                if (replay.Id != claim.ExecutionId || replay.VirtualKeyId != virtualKeyId ||
                    replay.FunctionConfigurationId != functionConfigurationId)
                    throw new InvalidOperationException("The completed claim's execution identity is inconsistent.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read the completed function claim {ExecutionId}", claim.ExecutionId);
                throw IdempotencyUnavailable(claim.ExecutionId);
            }

            await SettleClaimBillingAsync(store, claim, replay, token);
            replay.IsIdempotencyReplay = true;
            replay.IsIdempotencyBillingSettled = true;
            return replay;
        }

        var completed = false;
        try
        {
            var execution = await ExecuteCoreAsync(functionConfigurationId, virtualKeyId, parameters,
                metadata, providerToolName, claim.ExecutionId, claim, token);
            var resultJson = JsonSerializer.Serialize(execution, FunctionsJsonContext.Default.FunctionExecution);
            if (!await store.CompleteAsync(claim, resultJson, token))
                throw IdempotencyOutcomeUnknown(claim.ExecutionId);
            completed = true;

            await SettleClaimBillingAsync(store, claim, execution, token);
            execution.IsIdempotencyBillingSettled = true;
            return execution;
        }
        catch (Exception executionError)
        {
            // Completion is never overwritten: if settlement failed, replay can still
            // recover the saved result and safely retry the execution-scoped debit.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.MarkUncertainAsync(claim, cleanupDeadline.Token); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not mark function claim {ExecutionId} uncertain; it remains unreclaimable", claim.ExecutionId);
            }
            if (completed) throw;
            _logger.LogWarning(executionError, "Function execution {ExecutionId} did not publish a durable result", claim.ExecutionId);
            throw IdempotencyOutcomeUnknown(claim.ExecutionId);
        }
    }

    private async Task SettleClaimBillingAsync(
        IFunctionExecutionIdempotencyStore store,
        FunctionExecutionClaim claim,
        FunctionExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.SettleBillingAsync(claim,
                execution.ActualCost ?? execution.EstimatedCost ?? 0m, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not settle durable billing for function execution {ExecutionId}", claim.ExecutionId);
            throw IdempotencyUnavailable(claim.ExecutionId);
        }
    }

    private static FunctionIdempotencyException IdempotencyUnavailable(Guid executionId) =>
        new(503, "idempotency_unavailable", executionId,
            "Durable function execution or billing is unavailable. Retry with the same Idempotency-Key; do not use a new key for this operation.");

    private static FunctionIdempotencyException IdempotencyOutcomeUnknown(Guid executionId) =>
        new(409, "idempotency_outcome_unknown", executionId,
            "This function request has an uncertain outcome and will not be dispatched again. Inspect its execution before taking further action.");

    private static string ComputeFingerprint(
        int functionConfigurationId,
        Dictionary<string, object> parameters,
        Dictionary<string, object>? metadata,
        string? providerToolName)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("functionConfigurationId", functionConfigurationId);
            writer.WritePropertyName("parameters");
            WriteCanonical(writer, JsonSerializer.SerializeToElement(parameters, FunctionsJsonContext.Default.DictionaryStringObject));
            writer.WritePropertyName("metadata");
            if (metadata is null) writer.WriteNullValue();
            else WriteCanonical(writer, JsonSerializer.SerializeToElement(metadata, FunctionsJsonContext.Default.DictionaryStringObject));
            writer.WriteString("providerToolName", string.IsNullOrWhiteSpace(providerToolName) ? null : providerToolName);
            writer.WriteEndObject();
        }
        return Hash(output.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var value in element.EnumerateArray()) WriteCanonical(writer, value);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static string Hash(byte[] input) => Convert.ToHexStringLower(SHA256.HashData(input));
}
