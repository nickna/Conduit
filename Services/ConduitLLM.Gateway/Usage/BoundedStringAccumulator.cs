using System.Text;

namespace ConduitLLM.Gateway.UsageTracking;

public sealed class BoundedStringAccumulator
{
    private StringBuilder? _builder = new();
    private readonly bool _discardOnOverflow;

    public BoundedStringAccumulator(int maximumCharacters)
        : this(maximumCharacters, discardOnOverflow: false)
    {
    }

    internal BoundedStringAccumulator(int maximumCharacters, bool discardOnOverflow)
    {
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        MaximumCharacters = maximumCharacters;
        _discardOnOverflow = discardOnOverflow;
    }

    public int MaximumCharacters { get; }

    public int Length => _builder?.Length ?? 0;

    public long TotalCharactersObserved { get; private set; }

    public bool LimitExceeded { get; private set; }

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        TotalCharactersObserved += value.Length;
        if (_builder is null)
            return;

        var remaining = MaximumCharacters - _builder.Length;
        if (value.Length > remaining)
        {
            LimitExceeded = true;
            if (_discardOnOverflow)
                _builder = null;
            else if (remaining > 0)
                _builder.Append(value.AsSpan(0, remaining));
            return;
        }

        _builder.Append(value);
    }

    public override string ToString() => _builder?.ToString() ?? string.Empty;
}
