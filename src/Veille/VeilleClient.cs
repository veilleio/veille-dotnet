using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Veille;

/// <summary>
/// Official Veille API client. Create one instance and reuse it; it is thread-safe.
/// </summary>
public sealed partial class VeilleClient : IDisposable
{
    /// <summary>Production API origin.</summary>
    public const string DefaultBaseUrl = "https://api.veille.io";

    /// <summary>Environment variable read when no API key is configured.</summary>
    public const string ApiKeyEnvironmentVariable = "VEILLE_API_KEY";

    /// <summary>SDK version sent in the User-Agent header.</summary>
    public const string Version = "0.1.0";

    /// <summary>Default client-side deadline per request.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Job statuses <see cref="WaitForJobAsync"/> stops on.</summary>
    public static readonly IReadOnlyList<string> JobTerminalStatuses = new[] { "CompletedJob", "Error", "NotFound", "BadRequest" };

    /// <summary>Tasks run at the same time by <see cref="BatchAsync{TItem, TResult}"/>.</summary>
    public const int DefaultConcurrency = 10;

    /// <summary>Retries after a network error or a 408, 429, 500, 502, 503 or 504 response.</summary>
    public const int MaxRetries = 3;

    /// <summary>Each retry waits this many times longer than the previous one (a Retry-After header wins).</summary>
    public const double RetryBackoffFactor = 1.5;

    /// <summary>Delay before the first retry.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Longest delay between two attempts.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly int[] RetryableStatuses = { 408, 500, 502, 503, 504 };
    private static readonly string[] ErrorMessageKeys = { "error", "message", "error_message", "detail" };
    private static readonly Regex PathParameter = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan? _timeout;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;

    /// <summary>Creates a client. Without options the API key is read from <c>VEILLE_API_KEY</c>.</summary>
    /// <param name="options">Client configuration.</param>
    public VeilleClient(VeilleClientOptions? options = null)
    {
        options ??= new VeilleClientOptions();
        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new VeilleConfigurationException(
                $"A non-blank Veille API key is required. Set ApiKey or {ApiKeyEnvironmentVariable}.");
        }
        if (apiKey.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new VeilleConfigurationException("The Veille API key cannot be used in an HTTP header.");
        }
        var baseUrl = (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new VeilleConfigurationException("BaseUrl must be an absolute http or https URL.");
        }
        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new VeilleConfigurationException("Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.MaxRetries is < 0)
        {
            throw new VeilleConfigurationException("MaxRetries must be zero or more.");
        }
        if (options.RetryDelay is { } delay && delay < TimeSpan.Zero)
        {
            throw new VeilleConfigurationException("RetryDelay must be zero or more.");
        }
        _maxRetries = options.MaxRetries ?? MaxRetries;
        _retryDelay = options.RetryDelay ?? DefaultRetryDelay;
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _timeout = options.Timeout == Timeout.InfiniteTimeSpan ? null : options.Timeout ?? DefaultTimeout;
        _headers = new Dictionary<string, string>(options.Headers);
        _ownsHttpClient = options.HttpClient is null;
        _httpClient = options.HttpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Sends any request, including endpoints released after this SDK version. GET parameters travel in
    /// the query string; POST parameters travel in a JSON body.
    /// </summary>
    /// <param name="method">GET or POST.</param>
    /// <param name="path">Path starting with "/"; <c>{name}</c> segments are filled from <paramref name="parameters"/>.</param>
    /// <param name="parameters">Parameters; null values are omitted.</param>
    /// <param name="options">Per-request options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<ApiResponse> RequestAsync(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, object?>>? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        SendAsync((method ?? string.Empty).ToUpperInvariant(), path, parameters, options, expectJson: false, cancellationToken);

    /// <summary>
    /// Runs <paramref name="task"/> for every item with at most <paramref name="concurrency"/> tasks in flight and
    /// returns one <see cref="BatchResult{T}"/> per item, in input order. A failing item never stops the others.
    /// </summary>
    /// <typeparam name="TItem">Input type.</typeparam>
    /// <typeparam name="TResult">Task result type.</typeparam>
    /// <param name="items">Inputs.</param>
    /// <param name="task">Work for one input, usually an endpoint call.</param>
    /// <param name="concurrency">Tasks in flight at the same time; default 10.</param>
    /// <param name="cancellationToken">Stops starting new tasks and is passed to each task.</param>
    public async Task<IReadOnlyList<BatchResult<TResult>>> BatchAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> task,
        int concurrency = DefaultConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (concurrency < 1)
        {
            throw new VeilleConfigurationException("concurrency must be a positive integer.");
        }
        using var slots = new SemaphoreSlim(concurrency);
        var running = items.Select(async (item, index) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return new BatchResult<TResult>(index, await task(item, cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new BatchResult<TResult>(index, default, exception);
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        return await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Polls <c>GET /v1/jobs/{id}</c> until the job reaches a terminal status.</summary>
    /// <param name="jobId">The job id returned by an async endpoint.</param>
    /// <param name="interval">First delay between polls (default 3 seconds); it grows 1.5x per poll up to 60 seconds.</param>
    /// <param name="timeout">Total wait; default 12 hours.</param>
    /// <param name="onProgress">Called with the job document after every poll.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<ApiResponse> WaitForJobAsync(
        string jobId,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        Action<JsonElement>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new VeilleSerializationException("A non-blank job id is required.", "id");
        }
        var pause = interval ?? TimeSpan.FromSeconds(3);
        var deadline = timeout ?? TimeSpan.FromHours(12);
        var maxPause = pause > TimeSpan.FromMinutes(1) ? pause : TimeSpan.FromMinutes(1);
        var started = DateTime.UtcNow;
        while (true)
        {
            var last = await RequestAsync(
                "GET", "/v1/jobs/{id}", new Dictionary<string, object?> { ["id"] = jobId }, null, cancellationToken)
                .ConfigureAwait(false);
            var job = last.Json;
            onProgress?.Invoke(job);
            if (job.ValueKind == JsonValueKind.Object
                && job.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && JobTerminalStatuses.Contains(status.GetString()))
            {
                return last;
            }
            if (DateTime.UtcNow - started + pause > deadline)
            {
                throw new VeilleJobTimeoutException(jobId, last);
            }
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 3 / 2, maxPause.Ticks));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private Task<ApiResponse> CallAsync(
        string name, IVeilleParams? parameters, RequestOptions? options, CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.All[name];
        var method = options?.Method?.ToUpperInvariant() ?? endpoint.Methods[0];
        if (!endpoint.Methods.Contains(method))
        {
            throw new VeilleConfigurationException(
                $"{endpoint.Path} supports {string.Join(", ", endpoint.Methods)}, not {method}.");
        }
        var values = parameters?.ToWire().ToList() ?? new List<KeyValuePair<string, object?>>();
        foreach (var required in endpoint.Required)
        {
            if (!values.Any(pair => pair.Key == required && pair.Value is not null))
            {
                throw new VeilleSerializationException($"The \"{required}\" parameter is required.", required);
            }
        }
        return SendAsync(method, endpoint.Path, values, options, endpoint.Json, cancellationToken);
    }

    private async Task<ApiResponse> SendAsync(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, object?>>? parameters,
        RequestOptions? options,
        bool expectJson,
        CancellationToken cancellationToken)
    {
        if (method != "GET" && method != "POST")
        {
            throw new VeilleConfigurationException($"Unsupported HTTP method: {method}.");
        }
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
        {
            throw new VeilleConfigurationException("The request path must start with \"/\".");
        }

        var remaining = (parameters ?? Enumerable.Empty<KeyValuePair<string, object?>>())
            .Where(pair => pair.Value is not null)
            .ToList();
        var filledPath = PathParameter.Replace(path, match =>
        {
            var name = match.Groups[1].Value;
            var index = remaining.FindIndex(pair => pair.Key == name);
            if (index < 0)
            {
                throw new VeilleSerializationException($"The \"{name}\" path parameter is required.", name);
            }
            var value = WireString(name, remaining[index].Value!);
            remaining.RemoveAt(index);
            return Uri.EscapeDataString(value);
        });

        var url = new StringBuilder(_baseUrl).Append(filledPath);
        byte[]? payload = null;
        if (method == "GET")
        {
            var pairs = new List<string>();
            foreach (var (name, value) in remaining)
            {
                if (value is System.Collections.IEnumerable items && value is not string
                    && value is not System.Collections.IDictionary)
                {
                    foreach (var item in items)
                    {
                        if (item is not null)
                        {
                            pairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(WireString(name, item)));
                        }
                    }
                }
                else
                {
                    pairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(WireString(name, value!)));
                }
            }
            if (pairs.Count > 0)
            {
                url.Append('?').Append(string.Join("&", pairs));
            }
        }
        else
        {
            var body = new Dictionary<string, object?>();
            var queryPairs = new List<string>();
            foreach (var (name, value) in remaining)
            {
                // The API reads the global parameters from the query string, whatever the method.
                if (name is "fields" or "is_async" or "soft_fail")
                {
                    queryPairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(WireString(name, value!)));
                    continue;
                }
                if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                {
                    throw new VeilleSerializationException($"The \"{name}\" parameter must be a finite number.", name);
                }
                body[name] = value;
            }
            if (queryPairs.Count > 0)
            {
                url.Append('?').Append(string.Join("&", queryPairs));
            }
            payload = JsonSerializer.SerializeToUtf8Bytes(body);
        }

        var maxRetries = Math.Max(options?.MaxRetries ?? _maxRetries, 0);
        for (var attempt = 0; ; attempt++)
        {
            var (raw, failure) = await AttemptAsync(method, url.ToString(), payload, options, cancellationToken).ConfigureAwait(false);
            if (raw is not null && raw.StatusCode >= 200 && raw.StatusCode <= 299)
            {
                return Decode(raw, expectJson);
            }
            if (attempt >= maxRetries || (raw is not null && !IsRetryable(raw)))
            {
                throw raw is not null ? CreateApiException(raw) : failure!;
            }
            var retryAfter = raw is not null && raw.Headers.TryGetValue("retry-after", out var values) && values.Count > 0
                ? values[0]
                : null;
            await Task.Delay(RetryDelay(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(RawResponse? Raw, VeilleTransportException? Failure)> AttemptAsync(
        string method, string url, byte[]? payload, RequestOptions? options, CancellationToken cancellationToken)
    {
        HttpContent? content = null;
        if (payload is not null)
        {
            content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, */*;q=0.8");
        request.Headers.TryAddWithoutValidation("User-Agent", "veille-dotnet/" + Version);
        foreach (var (name, value) in _headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (options is not null)
        {
            foreach (var (name, value) in options.Headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        request.Headers.Remove("x-api-key");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);

        var timeout = options?.Timeout ?? _timeout;
        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            using var response = await _httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                headers[header.Key.ToLowerInvariant()] = header.Value.ToList();
            }
            return (new RawResponse((int)response.StatusCode, headers, bytes), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new VeilleTransportException($"Veille request timed out after {timeout}.", exception);
        }
        catch (HttpRequestException exception)
        {
            return (null, new VeilleTransportException("Veille request failed before an HTTP response was received.", exception));
        }
    }

    // Server errors and plan rate limits (401/429 "Rate limit exceeded") are retried; per-key quotas are not.
    private static bool IsRetryable(RawResponse raw)
    {
        if (Array.IndexOf(RetryableStatuses, raw.StatusCode) >= 0)
        {
            return true;
        }
        if (raw.StatusCode != 401 && raw.StatusCode != 429)
        {
            return false;
        }
        var message = CreateApiException(raw).ErrorMessage?.ToLowerInvariant() ?? string.Empty;
        if (message.Contains("quota", StringComparison.Ordinal))
        {
            return false;
        }
        return message.Contains("rate limit", StringComparison.Ordinal) || (raw.StatusCode == 429 && message.Length == 0);
    }

    private TimeSpan RetryDelay(int attempt, string? retryAfter)
    {
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 60));
        }
        var milliseconds = _retryDelay.TotalMilliseconds * Math.Pow(RetryBackoffFactor, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxRetryDelay.TotalMilliseconds));
    }

    private static ApiResponse Decode(RawResponse raw, bool expectJson)
    {
        var result = new ApiResponse(raw);
        if (result.IsJson)
        {
            try
            {
                _ = result.Json;
            }
            catch (VeilleResponseDecodingException exception)
            {
                throw new VeilleResponseDecodingException(
                    "Veille returned a successful JSON response that could not be decoded.", raw, exception);
            }
        }
        else if (expectJson)
        {
            throw new VeilleResponseDecodingException(
                "Veille returned a successful non-JSON response for a JSON operation.", raw);
        }
        return result;
    }

    internal static string WireString(string name, object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        double number when double.IsNaN(number) || double.IsInfinity(number) =>
            throw new VeilleSerializationException($"The \"{name}\" parameter must be a finite number.", name),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => ((double)number).ToString("R", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ when value is System.Collections.IEnumerable => JsonSerializer.Serialize(value),
        _ => value.ToString() ?? string.Empty,
    };

    private static VeilleApiException CreateApiException(RawResponse raw)
    {
        JsonElement? body = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(raw.Body);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
        }
        if (body is { ValueKind: JsonValueKind.Object } element)
        {
            foreach (var key in ErrorMessageKeys)
            {
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(value.GetString()))
                {
                    message = value.GetString();
                    break;
                }
            }
        }
        return new VeilleApiException(raw, body, message);
    }
}
