namespace Veille;

/// <summary>Per-request options.</summary>
public sealed class RequestOptions
{
    /// <summary>GET (default when supported) sends query parameters; POST sends a JSON body.</summary>
    public string? Method { get; init; }

    /// <summary>Overrides the client deadline for this request.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Extra headers for this request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Overrides the client retry count for this request (0 disables retries).</summary>
    public int? MaxRetries { get; init; }
}
