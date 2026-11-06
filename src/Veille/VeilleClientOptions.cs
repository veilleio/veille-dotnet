namespace Veille;

/// <summary>Configuration for <see cref="VeilleClient"/>.</summary>
public sealed class VeilleClientOptions
{
    /// <summary>API key sent in the <c>x-api-key</c> header. Defaults to the <c>VEILLE_API_KEY</c> environment variable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>API origin. Defaults to <see cref="VeilleClient.DefaultBaseUrl"/>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Client-side deadline per request. Defaults to 120 seconds; <see cref="Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Custom <see cref="System.Net.Http.HttpClient"/>; the caller keeps ownership. Disable redirects on it.</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Headers sent with every request.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    /// <summary>Retries after a network error or a 408/429/5xx response. Defaults to 3; 0 disables retries.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Delay before the first retry, growing 1.5x per retry. Defaults to 1 second.</summary>
    public TimeSpan? RetryDelay { get; set; }
}
