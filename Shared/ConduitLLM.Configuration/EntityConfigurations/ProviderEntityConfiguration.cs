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
                (left, right) => ProviderSettingsComparer.AreEqual(left, right),
                value => ProviderSettingsComparer.ComputeHashCode(value),
                value => ProviderSettingsComparer.Snapshot(value));

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
                (left, right) => ProviderSettingsComparer.AreEqual(left, right),
                value => ProviderSettingsComparer.ComputeHashCode(value),
                value => ProviderSettingsComparer.Snapshot(value));

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

    /// <summary>
    /// Structural change tracking for settings bags, independent of insertion order and
    /// the caller's dictionary comparer. Public helpers are used by the generated model.
    /// </summary>
    public static class ProviderSettingsComparer
    {
        public static bool AreEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            // KeyValuePair's default equality compares string keys and values ordinally.
            // Sorting also avoids inheriting a case-insensitive caller's lookup semantics.
            return left.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SequenceEqual(right.OrderBy(entry => entry.Key, StringComparer.Ordinal));
        }

        public static int ComputeHashCode(Dictionary<string, string>? value)
        {
            if (value == null)
            {
                return 0;
            }

            var hash = new HashCode();
            foreach (var entry in value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                hash.Add(entry.Key, StringComparer.Ordinal);
                hash.Add(entry.Value, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }

        public static Dictionary<string, string>? Snapshot(Dictionary<string, string>? value) =>
            value == null ? null : new Dictionary<string, string>(value, StringComparer.Ordinal);
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class ProviderSettingsJsonContext : JsonSerializerContext;
}
