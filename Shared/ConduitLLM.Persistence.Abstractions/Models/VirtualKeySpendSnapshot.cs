namespace ConduitLLM.Persistence;

/// <summary>Current key membership and persisted group balance from one fresh read.</summary>
public readonly record struct VirtualKeySpendSnapshot(int GroupId, decimal Balance);
