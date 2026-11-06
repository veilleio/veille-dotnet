namespace Veille;

/// <summary>Buffered HTTP response.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Headers">Lower-cased header names mapped to every received value.</param>
/// <param name="Body">Exact body bytes.</param>
public sealed record RawResponse(int StatusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> Headers, byte[] Body)
{
    /// <summary>The Content-Type header, or null.</summary>
    public string? ContentType => Headers.TryGetValue("content-type", out var values) && values.Count > 0 ? values[0] : null;
}
