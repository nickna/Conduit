using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Functions.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Functions.Services;

public sealed class FunctionParameterValidationServiceTests
{
    private readonly FunctionParameterValidationService _validator =
        new(NullLogger<FunctionParameterValidationService>.Instance);

    [Theory]
    [InlineData("dictionary")]
    [InlineData("typed-dictionary")]
    [InlineData("read-only-dictionary")]
    [InlineData("read-only-object-dictionary")]
    [InlineData("generic-dictionary")]
    [InlineData("hashtable")]
    [InlineData("empty-hashtable")]
    [InlineData("poco")]
    [InlineData("anonymous-object")]
    [InlineData("json-object")]
    [InlineData("json-node-object")]
    public void ObjectSchemaAcceptsSupportedObjectRepresentationsWithoutMutation(string representation)
    {
        object value = representation switch
        {
            "dictionary" => new Dictionary<string, object> { ["count"] = 1 },
            "typed-dictionary" => new Dictionary<string, int> { ["count"] = 1 },
            "read-only-dictionary" => new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int> { ["count"] = 1 }),
            "read-only-object-dictionary" => ImmutableDictionary<string, object>.Empty.Add("count", 1),
            "generic-dictionary" => StringKeyDictionary(),
            "hashtable" => new Hashtable { ["count"] = 1 },
            "empty-hashtable" => new Hashtable(),
            "poco" => new ObjectParameter { Count = 1 },
            "anonymous-object" => new { Count = 1 },
            "json-object" => JsonValue("{\"count\":1}"),
            "json-node-object" => new JsonObject { ["count"] = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(representation))
        };
        var nodeJson = (value as JsonNode)?.ToJsonString();

        AssertValidation(ValidateWithoutMutation(value, Schema("object")), valid: true);

        if (value is JsonNode node) Assert.Equal(nodeJson, node.ToJsonString());
    }

    [Theory]
    [InlineData("string")]
    [InlineData("character")]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("date")]
    [InlineData("guid")]
    [InlineData("uri")]
    [InlineData("version")]
    [InlineData("delegate")]
    [InlineData("array")]
    [InlineData("list")]
    [InlineData("numeric-key-dictionary")]
    [InlineData("numeric-key-hashtable")]
    [InlineData("mixed-key-hashtable")]
    [InlineData("json-string")]
    [InlineData("json-number")]
    [InlineData("json-boolean")]
    [InlineData("json-array")]
    [InlineData("json-node-string")]
    [InlineData("json-node-number")]
    [InlineData("json-node-boolean")]
    [InlineData("json-node-array")]
    public void ObjectSchemaRejectsScalarsArraysAndNonStringKeyMapsWithoutWarnings(string representation)
    {
        object value = representation switch
        {
            "string" => "plain string",
            "character" => 'x',
            "number" => 1,
            "boolean" => true,
            "date" => new DateTime(2026, 10, 16),
            "guid" => Guid.Empty,
            "uri" => new Uri("https://example.invalid/"),
            "version" => new Version(1, 0),
            "delegate" => (Action)(() => { }),
            "array" => new[] { 1, 2 },
            "list" => new List<int> { 1, 2 },
            "numeric-key-dictionary" => new Dictionary<int, object> { [1] = "value" },
            "numeric-key-hashtable" => new Hashtable { [1] = "value" },
            "mixed-key-hashtable" => new Hashtable { ["count"] = 1, [2] = "value" },
            "json-string" => JsonValue("\"plain string\""),
            "json-number" => JsonValue("1"),
            "json-boolean" => JsonValue("true"),
            "json-array" => JsonValue("[1,2]"),
            "json-node-string" => System.Text.Json.Nodes.JsonValue.Create("plain string")!,
            "json-node-number" => System.Text.Json.Nodes.JsonValue.Create(1)!,
            "json-node-boolean" => System.Text.Json.Nodes.JsonValue.Create(true)!,
            "json-node-array" => new JsonArray(1, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(representation))
        };

        var result = ValidateWithoutMutation(value, Schema("object"));

        AssertValidation(result, valid: false);
        Assert.Contains("must be an object", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectSchemaRejectsThrowingDictionaryKeysInsteadOfSkippingValidation()
    {
        AssertValidation(ValidateWithoutMutation(new ThrowingKeysDictionary(), Schema("object")), valid: false);
    }

    [Fact]
    public void ObjectSchemaRejectsDisposedJsonValuesInsteadOfSkippingValidation()
    {
        var document = JsonDocument.Parse("{\"count\":1}");
        var value = document.RootElement;
        document.Dispose();
        var parameters = new Dictionary<string, object> { ["value"] = value };
        var original = parameters.ToArray();

        var result = _validator.ValidateParameters(parameters, Schema("object"));

        AssertValidation(result, valid: false);
        AssertUnchanged(parameters, original);
    }

    [Theory]
    [InlineData("string", "\"snow 雪\"", true)]
    [InlineData("string", "\"\"", true)]
    [InlineData("string", "true", false)]
    [InlineData("string", "7", false)]
    [InlineData("boolean", "true", true)]
    [InlineData("boolean", "false", true)]
    [InlineData("boolean", "\"true\"", false)]
    [InlineData("boolean", "1", false)]
    [InlineData("number", "1.5", true)]
    [InlineData("number", "\"1.5\"", false)]
    [InlineData("number", "true", false)]
    [InlineData("select", "\"alpha\"", true)]
    [InlineData("select", "\"beta\"", true)]
    [InlineData("select", "\"Alpha\"", false)]
    [InlineData("select", "\"outside-options\"", false)]
    [InlineData("select", "1", false)]
    [InlineData("select", "true", false)]
    public void ClrAndJsonPrimitivesHaveTheSameTypeAndOptionValidation(
        string type, string json, bool valid)
    {
        var element = JsonValue(json);
        object clr = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Number => element.GetDouble(),
            _ => throw new InvalidOperationException("This matrix covers primitive values.")
        };
        var schema = Schema(type);
        var clrResult = ValidateWithoutMutation(clr, schema);
        var jsonResult = ValidateWithoutMutation(element, schema);

        AssertValidation(clrResult, valid);
        AssertValidation(jsonResult, valid);
        Assert.Equal(clrResult.Errors, jsonResult.Errors);
    }

    [Theory]
    [InlineData("1.249", false)]
    [InlineData("1.25", true)]
    [InlineData("1.875", true)]
    [InlineData("2.5", true)]
    [InlineData("2.501", false)]
    public void NumericBoundsAreInclusiveAndApplyToClrAndJsonValues(string json, bool valid)
    {
        const string schema = """{"optional":{"value":{"type":"number","min":1.25,"max":2.5}}}""";
        var clrResult = ValidateWithoutMutation(decimal.Parse(json, CultureInfo.InvariantCulture), schema);
        var jsonResult = ValidateWithoutMutation(JsonValue(json), schema);

        AssertValidation(clrResult, valid);
        AssertValidation(jsonResult, valid);
        Assert.Equal(clrResult.Errors, jsonResult.Errors);
    }

    [Fact]
    public void MaximumRejectsLargerIntegersBeyondDoublePrecisionForClrAndJson()
    {
        const string schema = """{"optional":{"value":{"type":"number","max":9007199254740992}}}""";
        object[] largerValues = [9007199254740993L, 9007199254740993m, JsonValue("9007199254740993")];

        foreach (var value in largerValues)
            AssertValidation(ValidateWithoutMutation(value, schema), valid: false);
    }

    [Fact]
    public void MaximumRetainsTheLastDecimalDigitForClrAndJson()
    {
        const string schema = """{"optional":{"value":{"type":"number","max":0.1234567890123456789012345678}}}""";
        object[] largerValues = [0.1234567890123456789012345679m,
            JsonValue("0.1234567890123456789012345679")];

        foreach (var value in largerValues)
            AssertValidation(ValidateWithoutMutation(value, schema), valid: false);
    }

    [Theory]
    [InlineData("max", "-0.0025", "-2.499e-3", false)]
    [InlineData("max", "-0.0025", "-2.500e-3", true)]
    [InlineData("max", "-0.0025", "-2.501e-3", true)]
    [InlineData("min", "-0.0025", "-2.501e-3", false)]
    [InlineData("min", "-0.0025", "-2.499e-3", true)]
    [InlineData("max", "2.5e-12", "2.500000000001e-12", false)]
    [InlineData("min", "2.5e-12", "2.499999999999e-12", false)]
    [InlineData("max", "2.5e-12", "2.5e-13", true)]
    public void SignedAndSmallExponentBoundsPreserveExactOrdering(
        string boundary, string bound, string json, bool valid)
    {
        var schema = "{\"optional\":{\"value\":{\"type\":\"number\",\"" + boundary + "\":" + bound + "}}}";
        var clrResult = ValidateWithoutMutation(decimal.Parse(json, NumberStyles.Float, CultureInfo.InvariantCulture), schema);
        var jsonResult = ValidateWithoutMutation(JsonValue(json), schema);

        AssertValidation(clrResult, valid);
        AssertValidation(jsonResult, valid);
        Assert.Equal(clrResult.Errors, jsonResult.Errors);
    }

    [Theory]
    [InlineData("9007199254740992.0000", "9.007199254740992e15", "9007199254740992")]
    [InlineData("0.1234567890123456789012345678", "0.123456789012345678901234567800", "1.234567890123456789012345678e-1")]
    [InlineData("2500e-3", "2.5000000", "2.5000")]
    [InlineData("-2500e-6", "-0.00250000", "-0.0025000")]
    public void EquivalentBoundsAcceptEqualityAcrossTrailingZerosAndExponentForms(
        string minimum, string maximum, string json)
    {
        var schema = "{\"optional\":{\"value\":{\"type\":\"number\",\"min\":" + minimum + ",\"max\":" + maximum + "}}}";

        AssertValidation(ValidateWithoutMutation(
            decimal.Parse(json, NumberStyles.Float, CultureInfo.InvariantCulture), schema), valid: true);
        AssertValidation(ValidateWithoutMutation(JsonValue(json), schema), valid: true);
    }

    [Theory]
    [InlineData("2.500000000001e-300", false)]
    [InlineData("2.5e-300", true)]
    [InlineData("2.499999999999e-300", true)]
    public void FiniteJsonValuesWithVerySmallExponentsRetainTheirOrdering(string json, bool valid)
    {
        const string schema = """{"optional":{"value":{"type":"number","max":2.5e-300}}}""";

        AssertValidation(ValidateWithoutMutation(JsonValue(json), schema), valid);
    }

    [Fact]
    public void EverySupportedFiniteClrNumberAndItsJsonRepresentationAreAccepted()
    {
        var numbers = new (object Clr, string Json)[]
        {
            (sbyte.MinValue, "-128"),
            (byte.MaxValue, "255"),
            (short.MinValue, "-32768"),
            (ushort.MaxValue, "65535"),
            (int.MinValue, "-2147483648"),
            (uint.MaxValue, "4294967295"),
            (long.MinValue, "-9223372036854775808"),
            (ulong.MaxValue, "18446744073709551615"),
            (12.125m, "12.125"),
            (decimal.MaxValue, "79228162514264337593543950335"),
            (0.5f, "0.5"),
            (float.MaxValue, "3.4028235e38"),
            (0.125d, "0.125"),
            (double.MaxValue, "1.7976931348623157e308"),
            (-0d, "-0")
        };
        foreach (var (clr, json) in numbers)
        {
            AssertValidation(ValidateWithoutMutation(clr, Schema("number")), valid: true);
            AssertValidation(ValidateWithoutMutation(JsonValue(json), Schema("number")), valid: true);
        }
    }

    [Fact]
    public void NonfiniteClrNumbersAreRejectedWithAndWithoutBounds()
    {
        object[] numbers = [float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        foreach (var value in numbers)
        {
            AssertValidation(ValidateWithoutMutation(value, Schema("number")), valid: false);
            AssertValidation(ValidateWithoutMutation(value,
                """{"optional":{"value":{"type":"number","min":0,"max":100}}}"""), valid: false);
        }
    }

    [Theory]
    [InlineData("1e10000")]
    [InlineData("-1e10000")]
    public void JsonNumbersOutsideTheFiniteDoubleRangeAreRejected(string json)
    {
        AssertValidation(ValidateWithoutMutation(JsonValue(json), Schema("number")), valid: false);
    }

    [Theory]
    [InlineData("1e-400")]
    [InlineData("-1e-400")]
    public void JsonNumbersThatUnderflowTheFiniteDoublePolicyAreRejectedWithoutNeedingBounds(string json)
    {
        var result = ValidateWithoutMutation(JsonValue(json), Schema("number"));

        AssertValidation(result, valid: false);
        Assert.Contains("number", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("boolean")]
    [InlineData("number")]
    [InlineData("select")]
    [InlineData("array")]
    [InlineData("object")]
    public void TypedFieldsRejectClrNullJsonNullAndUndefinedInsteadOfSkippingValidation(string type)
    {
        object?[] values = [null, JsonValue("null"), default(JsonElement)];
        foreach (var value in values)
            AssertValidation(ValidateWithoutMutation(value, Schema(type)), valid: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingSchemaPreservesTheExplicitValidationSkip(string? schema)
    {
        foreach (var value in new object?[] { null, double.NaN, default(JsonElement) })
            AssertValidation(ValidateWithoutMutation(value, schema), valid: true);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedSchemaAllowsTheRequestWithAnExplicitWarning(string schema)
    {
        var result = ValidateWithoutMutation("unchanged", schema);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("{\"type\":17}")]
    public void MalformedFieldSkipsOnlyThatFieldAndRetainsRequiredAndOtherFieldErrors(string brokenDefinition)
    {
        var schema = $$$$"""
            {"required":["missing"],"optional":{
                "broken":{{{{brokenDefinition}}}},"bounded":{"type":"number","min":1}}}
            """;
        var broken = new object();
        var parameters = new Dictionary<string, object> { ["broken"] = broken, ["bounded"] = 0 };
        var original = parameters.ToArray();

        var result = _validator.ValidateParameters(parameters, schema);

        Assert.False(result.IsValid);
        Assert.Collection(result.Errors,
            error => Assert.Contains("missing", error, StringComparison.Ordinal),
            error => Assert.Contains("bounded", error, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("broken", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("broken", StringComparison.Ordinal));
        AssertUnchanged(parameters, original);
    }

    [Fact]
    public void RequiredPresenceErrorsRemainWhenAnOptionalJsonPrimitiveIsValid()
    {
        const string schema = """{"required":["missing"],"optional":{"value":{"type":"string"}}}""";

        var result = ValidateWithoutMutation(JsonValue("\"accepted\""), schema);

        Assert.False(result.IsValid);
        Assert.Contains("missing", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    private ParameterValidationResult ValidateWithoutMutation(object? value, string? schema)
    {
        var ignored = new object();
        var parameters = new Dictionary<string, object> { ["value"] = value!, ["unmapped"] = ignored };
        var original = parameters.ToArray();
        var json = value is JsonElement element && element.ValueKind != JsonValueKind.Undefined
            ? element.GetRawText()
            : null;

        var result = _validator.ValidateParameters(parameters, schema);

        AssertUnchanged(parameters, original);
        if (value is JsonElement originalElement)
        {
            var after = Assert.IsType<JsonElement>(parameters["value"]);
            Assert.Equal(originalElement.ValueKind, after.ValueKind);
            if (json is not null) Assert.Equal(json, after.GetRawText());
        }
        return result;
    }

    private static void AssertUnchanged(
        Dictionary<string, object> actual, KeyValuePair<string, object>[] original)
    {
        Assert.Equal(original.Select(pair => pair.Key), actual.Keys);
        foreach (var pair in original)
            Assert.Same(pair.Value, actual[pair.Key]);
    }

    private static void AssertValidation(ParameterValidationResult result, bool valid)
    {
        Assert.Equal(valid, result.IsValid);
        if (valid) Assert.Empty(result.Errors);
        else Assert.Single(result.Errors);
        Assert.Empty(result.Warnings);
    }

    private static JsonElement JsonValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IDictionary<string, object> StringKeyDictionary()
    {
        IDictionary<string, object> value = new System.Dynamic.ExpandoObject();
        value["count"] = 1;
        return value;
    }

    private sealed class ObjectParameter
    {
        public int Count { get; init; }
    }

    private sealed class ThrowingKeysDictionary : Hashtable
    {
        public override ICollection Keys => throw new InvalidOperationException("Caller keys are unavailable.");
    }

    private static string Schema(string type) => type == "select"
        ? """{"optional":{"value":{"type":"select","options":["alpha","beta"]}}}"""
        : $$$$"""{"optional":{"value":{"type":"{{{{type}}}}"}}}""";
}
