#if !CONDUIT_NATIVE_AOT
using ConduitLLM.Functions.Entities;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>JIT reference for execution reads with no-tracking configuration includes.</summary>
public sealed class EfFunctionExecutionRuntimeReader : IFunctionExecutionRuntimeReader
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfFunctionExecutionRuntimeReader(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<FunctionExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.FunctionExecutions.AsNoTracking().Include(row => row.FunctionConfiguration)
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<FunctionExecution>> ListByVirtualKeyAsync(
        int virtualKeyId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.FunctionExecutions.AsNoTracking().Include(row => row.FunctionConfiguration)
            .Where(row => row.VirtualKeyId == virtualKeyId).OrderByDescending(row => row.RequestedAt)
            .ToListAsync(cancellationToken);
    }
}
#endif
