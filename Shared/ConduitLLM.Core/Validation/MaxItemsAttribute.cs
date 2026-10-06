using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace ConduitLLM.Core.Validation;

/// <summary>
/// Statically validates the number of items in a collection without probing for a
/// runtime <c>Count</c> property, unlike <see cref="MaxLengthAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MaxItemsAttribute : ValidationAttribute
{
    /// <summary>
    /// Initializes a collection-size constraint.
    /// </summary>
    /// <param name="maximum">The maximum accepted item count.</param>
    public MaxItemsAttribute(int maximum)
        : base("The field {0} must contain no more than {1} items.")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        Maximum = maximum;
    }

    /// <summary>Gets the maximum accepted item count.</summary>
    public int Maximum { get; }

    /// <inheritdoc />
    public override bool IsValid(object? value) =>
        value is null || value is ICollection collection && collection.Count <= Maximum;

    /// <inheritdoc />
    public override string FormatErrorMessage(string name) =>
        string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, Maximum);
}
