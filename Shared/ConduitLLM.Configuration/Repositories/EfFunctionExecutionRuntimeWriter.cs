#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Functions.Entities;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference for scalar execution inserts and optimistic updates.</summary>
public sealed class EfFunctionExecutionRuntimeWriter : IFunctionExecutionRuntimeWriter
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfFunctionExecutionRuntimeWriter(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<Guid> CreateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        RequireScalarSnapshot(execution);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (execution.Id == Guid.Empty) execution.Id = Guid.NewGuid();
        context.FunctionExecutions.Add(execution);
        await context.SaveChangesAsync(cancellationToken);
        return execution.Id;
    }

    public async Task<bool> UpdateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        RequireScalarSnapshot(execution);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var originalVersion = execution.Version;
        execution.Version = unchecked(originalVersion + 1);
        context.FunctionExecutions.Attach(execution);
        context.Entry(execution).State = EntityState.Modified;
        context.Entry(execution).Property(row => row.Version).OriginalValue = originalVersion;
        try { return await context.SaveChangesAsync(cancellationToken) > 0; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private static void RequireScalarSnapshot(FunctionExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.FunctionConfiguration is not null || execution.AuditEvents.Count != 0)
            throw new ArgumentException("Execution navigation graphs are outside this scalar writer.", nameof(execution));
    }
}
#endif
