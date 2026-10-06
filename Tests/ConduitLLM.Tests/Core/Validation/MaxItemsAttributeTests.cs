using System.ComponentModel.DataAnnotations;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Validation;

using AwesomeAssertions;

namespace ConduitLLM.Tests.Core.Validation;

public sealed class MaxItemsAttributeTests
{
    private readonly MaxItemsAttribute _attribute = new(2);

    [Fact]
    public void IsValid_AcceptsNullAndCollectionsWithinTheLimit()
    {
        _attribute.IsValid(null).Should().BeTrue();
        _attribute.IsValid(Array.Empty<int>()).Should().BeTrue();
        _attribute.IsValid(new[] { 1, 2 }).Should().BeTrue();
    }

    [Fact]
    public void IsValid_RejectsCollectionsOverTheLimitAndNonCollections()
    {
        _attribute.IsValid(new[] { 1, 2, 3 }).Should().BeFalse();
        _attribute.IsValid("not a collection contract").Should().BeFalse();
    }

    [Fact]
    public void FormatErrorMessage_IncludesTheMemberAndLimit()
    {
        _attribute.FormatErrorMessage("InjectionPoints")
            .Should().Be("The field InjectionPoints must contain no more than 2 items.");
    }

    [Fact]
    public void Constructor_RejectsNegativeLimits()
    {
        var action = () => new MaxItemsAttribute(-1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PromptCachingRule_UsesTheCollectionConstraint()
    {
        var rule = new PromptCachingRule
        {
            Name = "bounded",
            Provider = "OpenRouter",
            ModelPattern = "anthropic/*",
            Strategy = PromptCachingStrategy.Explicit,
            InjectionPoints = Enumerable.Range(0, PromptCachingConstants.MaxExplicitBreakpoints + 1)
                .Select(index => new CacheInjectionPoint { Index = index })
                .ToList()
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            rule,
            new ValidationContext(rule),
            results,
            validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().ContainSingle(result =>
            result.MemberNames.Contains(nameof(PromptCachingRule.InjectionPoints)));
    }
}
