using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ConduitLLM.Configuration.EntityConfigurations
{
    /// <summary>
    /// EF Core configuration for <see cref="Provider"/>. Maps the structured <see cref="Provider.Settings"/>
    /// dictionary to a PostgreSQL <c>jsonb</c> column with a value converter and comparer so change
    /// tracking works on the mutable dictionary.
    /// </summary>
    public class ProviderEntityConfiguration : IEntityTypeConfiguration<Provider>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<Provider> builder)
        {
            var converter = new ValueConverter<Dictionary<string, string>?, string?>(
                value => ProviderSettingsJson.Serialize(value),
                json => ProviderSettingsJson.Deserialize(json));

            var comparer = new ValueComparer<Dictionary<string, string>?>(
                (left, right) => ProviderSettingsJson.Serialize(left) == ProviderSettingsJson.Serialize(right),
                value => value == null ? 0 : ProviderSettingsJson.Serialize(value)!.GetHashCode(),
                value => value == null ? null : new Dictionary<string, string>(value));

            builder.Property(p => p.Settings)
                .HasColumnName("Settings")
                .HasColumnType("jsonb")
                .HasConversion(converter, comparer);
        }
    }

    /// <summary>
    /// EF Core configuration for <see cref="ProviderKeyCredential"/>. Maps the encrypted
    /// <see cref="ProviderKeyCredential.SecretSettings"/> dictionary to a PostgreSQL <c>jsonb</c>
    /// column with the same converter and comparer treatment as the provider settings bag.
    /// </summary>
    public class ProviderKeyCredentialEntityConfiguration : IEntityTypeConfiguration<ProviderKeyCredential>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProviderKeyCredential> builder)
        {
            var converter = new ValueConverter<Dictionary<string, string>?, string?>(
                value => ProviderSettingsJson.Serialize(value),
                json => ProviderSettingsJson.Deserialize(json));

            var comparer = new ValueComparer<Dictionary<string, string>?>(
                (left, right) => ProviderSettingsJson.Serialize(left) == ProviderSettingsJson.Serialize(right),
                value => value == null ? 0 : ProviderSettingsJson.Serialize(value)!.GetHashCode(),
                value => value == null ? null : new Dictionary<string, string>(value));

            builder.Property(p => p.SecretSettings)
                .HasColumnName("SecretSettings")
                .HasColumnType("jsonb")
                .HasConversion(converter, comparer);
        }
    }

    /// <summary>
    /// AOT-safe JSON conversion used by the EF provider settings value converters.
    /// Public methods allow EF's compiled-model generator to reproduce the expressions.
    /// </summary>
    public static class ProviderSettingsJson
    {
        public static string? Serialize(Dictionary<string, string>? value) =>
            value == null
                ? null
                : JsonSerializer.Serialize(value, ProviderSettingsJsonContext.Default.DictionaryStringString);

        public static Dictionary<string, string>? Deserialize(string? json) =>
            string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize(json, ProviderSettingsJsonContext.Default.DictionaryStringString);
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class ProviderSettingsJsonContext : JsonSerializerContext;
}
