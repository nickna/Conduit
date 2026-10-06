using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Validation;

/// <summary>
/// Validates the minimum size of a collection without reflecting over a Count property.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MinItemsAttribute(int minimum) : ValidationAttribute
{
    public int Minimum { get; } = minimum >= 0
        ? minimum
        : throw new ArgumentOutOfRangeException(nameof(minimum));

    public override bool IsValid(object? value)
    {
        if (value is null)
            return true;

        if (value is ICollection collection)
            return collection.Count >= Minimum;

        if (value is not IEnumerable enumerable)
            return false;

        var count = 0;
        foreach (var _ in enumerable)
        {
            if (++count >= Minimum)
                return true;
        }

        return Minimum == 0;
    }
}
