# Repository Pattern Implementation for Conduit

This directory contains interfaces and implementations for the Repository pattern, which provides an abstraction layer between the domain model and the data access layer.

## Implementation Strategy

The Repository pattern is being implemented gradually in the Conduit codebase. The initial focus is on the `VirtualKeyRepository` as a proof of concept. Once this is working correctly, the pattern will be extended to other entities.

## Key Benefits

1. **Separation of Concerns**: Data access logic is separated from business logic
2. **Testability**: Services are easier to test as database access can be mocked
3. **Maintainability**: Database access code is centralized and follows a consistent pattern
4. **Flexibility**: The pattern makes it easier to transition between direct database access and API calls

## Current Implementation Status

- ✅ `IVirtualKeyRepository` and `VirtualKeyRepository` - Complete
- ✅ `IGlobalSettingRepository` - extracted to the EF-free persistence abstractions
- ✅ `GlobalSettingRepository` - explicit-operation EF reference implementation
- ✅ `NpgsqlGlobalSettingRepository` - typed-Npgsql parity implementation
- ✅ `IIpFilterRepository` - extracted explicit contract for global/per-key access policy
- ✅ `IpFilterRepository` - explicit-operation EF reference implementation
- ✅ `NpgsqlIpFilterRepository` - typed-Npgsql parity implementation
- ✅ `IProviderRepository` / `IProviderKeyCredentialRepository` - extracted provider consistency contracts
- ✅ EF provider repositories - explicit-operation production/reference implementations
- ✅ typed-Npgsql provider repositories - JSONB, graph, cascade, and primary-rotation parity
- ❌ Other repositories - Planned

## Usage Example

```csharp
// In services
public class VirtualKeyService
{
    private readonly IVirtualKeyRepository _repository;
    
    public VirtualKeyService(IVirtualKeyRepository repository)
    {
        _repository = repository;
    }
    
    public async Task<bool> ResetSpendAsync(int id)
    {
        var virtualKey = await _repository.GetByIdAsync(id);
        if (virtualKey == null) return false;
        
        virtualKey.CurrentSpend = 0;
        virtualKey.BudgetStartDate = DetermineBudgetStartDate(virtualKey.BudgetDuration);
        virtualKey.UpdatedAt = DateTime.UtcNow;
        
        return await _repository.UpdateAsync(virtualKey);
    }
}

// In controllers, you can use either direct DB access (WebAdmin admin pages)
// or API calls (external applications) without changing service logic
```

## Next Steps

1. Complete the implementation of the VirtualKeyRepository
2. Add unit tests for the repository
3. Refactor the VirtualKeyService to use the repository
4. Gradually implement repositories for other entities
