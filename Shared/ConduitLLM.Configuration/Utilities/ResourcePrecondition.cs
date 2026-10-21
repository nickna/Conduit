namespace ConduitLLM.Configuration.Utilities;

/// <summary>The database values used to identify one resource snapshot.</summary>
public sealed record ResourceVersion(byte[]? RowVersion, DateTime UpdatedAt)
{
    /// <summary>Creates a group version that includes its member count.</summary>
    public ResourceVersion(byte[]? rowVersion, DateTime updatedAt, int virtualKeyCount)
        : this(rowVersion, updatedAt)
    {
        VirtualKeyCount = virtualKeyCount;
    }

    /// <summary>Member count when the represented resource is a virtual key group.</summary>
    public int? VirtualKeyCount { get; init; }

    // Some supported stores do not generate RowVersion. Always include the stored timestamp.
    public string ETag => VirtualKeyCount is { } count
        ? $"\"{Convert.ToBase64String(RowVersion ?? [])}.{UpdatedAt.Ticks:x}.{count:x}\""
        : $"\"{Convert.ToBase64String(RowVersion ?? [])}.{UpdatedAt.Ticks:x}\"";
}

/// <summary>A strong If-Match condition passed explicitly to the persistence boundary.</summary>
public sealed class ResourcePrecondition
{
    private readonly string[] _candidates;
    public ResourcePrecondition(IEnumerable<string?> headers) => _candidates = headers
        .SelectMany(value => value?.Split(',') ?? [])
        .Select(value => value.Trim()).ToArray();
    public bool IsMissing => _candidates.Length == 0 || _candidates.All(string.IsNullOrEmpty);
    public bool Matches(ResourceVersion version) => _candidates.Any(value =>
        value == "*" || string.Equals(value, version.ETag, StringComparison.Ordinal));
}

public sealed class ResourcePreconditionRequiredException : Exception
{
    public ResourcePreconditionRequiredException() : base("Fetch the resource and send its ETag in the If-Match header.") { }
}

public sealed class ResourcePreconditionFailedException : Exception
{
    public ResourcePreconditionFailedException() : base("The resource changed after it was read. Fetch it again and retry.") { }
}

/// <summary>Server-only version metadata for a returned representation.</summary>
public interface IVersionedRepresentation
{
    string? ResourceETag { get; }
}
