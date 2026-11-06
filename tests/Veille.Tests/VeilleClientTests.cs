using System.Net;
using System.Text;
using System.Text.Json;
using Veille;
using Xunit;

namespace Veille.Tests;

public sealed class VeilleClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json(200, "{\"ok\":true}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return Responder(request);
        }
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private readonly RecordingHandler _handler = new();
    private readonly VeilleClient _client;

    public VeilleClientTests()
    {
        _client = new VeilleClient(new VeilleClientOptions
        {
            ApiKey = "test-key",
            HttpClient = new HttpClient(_handler),
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });
    }

    private VeilleClient ClientWithBaseUrl(string baseUrl) =>
        new(new VeilleClientOptions { ApiKey = "k", BaseUrl = baseUrl, HttpClient = new HttpClient(_handler) });

    [Fact]
    public void RequiresApiKey()
    {
        Environment.SetEnvironmentVariable("VEILLE_API_KEY", null);
        Assert.Throws<VeilleConfigurationException>(() => new VeilleClient());
        Assert.Throws<VeilleConfigurationException>(() => new VeilleClient(new VeilleClientOptions { ApiKey = "   " }));
        Assert.Throws<VeilleConfigurationException>(() => new VeilleClient(new VeilleClientOptions { ApiKey = "k", BaseUrl = "ftp://x" }));
    }

    [Fact]
    public async Task FeaturedOperation()
    {
        _handler.Responder = _ => Json(200, "{\"hello\":\"world\"}");
        var response = await _client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" });
        Assert.Equal("world", response.Json.GetProperty("hello").GetString());
        var request = _handler.Requests[0].Request;
        Assert.Equal("GET", request.Method.Method);
        Assert.Equal("https://api.veille.io/v1/intelligence/email", request.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
        Assert.StartsWith("veille-dotnet/", string.Join(" ", request.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task QuerySerialization()
    {
        using var client = ClientWithBaseUrl("https://example.test/");
        await client.RequestAsync("GET", "/v1/anything", new Dictionary<string, object?>
        {
            ["text"] = "a b&c",
            ["flag"] = false,
            ["count"] = 0,
            ["ratio"] = 1.5,
            ["whole"] = 2.0,
            ["list"] = new[] { "x", "y" },
            ["nothing"] = null,
        });
        Assert.Equal(
            "https://example.test/v1/anything?text=a%20b%26c&flag=false&count=0&ratio=1.5&whole=2&list=x&list=y",
            _handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PostSendsJsonBody()
    {
        await _client.RequestAsync("POST", "/v1/anything", new Dictionary<string, object?> { ["query"] = "x", ["page"] = 2, ["gone"] = null });
        var (request, body) = _handler.Requests[0];
        Assert.Equal("POST", request.Method.Method);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"query\":\"x\",\"page\":2}", body);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
    }

    [Fact]
    public async Task PostKeepsGlobalParametersInQuery()
    {
        await _client.RequestAsync("POST", "/v1/anything", new Dictionary<string, object?> { ["query"] = "x", ["is_async"] = true, ["fields"] = "a" });
        var (request, body) = _handler.Requests[0];
        Assert.Equal("{\"query\":\"x\"}", body);
        Assert.Equal("?is_async=true&fields=a", request.RequestUri!.Query);
    }

    [Fact]
    public async Task PathParameters()
    {
        await _client.RequestAsync("GET", "/v1/items/{id}", new Dictionary<string, object?> { ["id"] = "a/b c" });
        Assert.Equal("https://api.veille.io/v1/items/a%2Fb%20c", _handler.Requests[0].Request.RequestUri!.AbsoluteUri);
        await Assert.ThrowsAsync<VeilleSerializationException>(() => _client.RequestAsync("GET", "/v1/items/{id}"));
    }

    [Fact]
    public async Task UnsupportedMethod()
    {
        await Assert.ThrowsAsync<VeilleConfigurationException>(() => _client.IntelligenceDomainAsync(new IntelligenceDomainParams { Query = "test" }, new RequestOptions { Method = "POST" }));
    }

    [Fact]
    public async Task EveryEndpoint()
    {
        var calls = new Dictionary<string, Func<Task<ApiResponse>>>
        {
            ["intelligence_domain"] = () => _client.IntelligenceDomainAsync(new IntelligenceDomainParams { Query = "test" }),
            ["intelligence_email"] = () => _client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "john@example.com" }),
            ["intelligence_emailq"] = () => _client.IntelligenceEmailqAsync(new IntelligenceEmailqParams { Query = "john@example.com" }),
            ["intelligence_ip"] = () => _client.IntelligenceIpAsync(new IntelligenceIpParams { Query = "test" }),
            ["intelligence_phone"] = () => _client.IntelligencePhoneAsync(new IntelligencePhoneParams { Query = "+33612345678" }),
            ["jobs"] = () => _client.JobsAsync(new JobsParams { Id = "test" }),
            ["stats"] = () => _client.StatsAsync(new StatsParams()),
            ["usage"] = () => _client.UsageAsync(),
            ["vat"] = () => _client.VatAsync(new VatParams { Query = "test" }),
            ["vat_iban"] = () => _client.VatIbanAsync(new VatIbanParams { Query = "test" }),
            ["vat_rates"] = () => _client.VatRatesAsync(new VatRatesParams()),
        };
        foreach (var (name, endpoint) in Endpoints.All)
        {
            await calls[name]();
            var request = _handler.Requests[^1].Request;
            Assert.Equal("https://api.veille.io" + endpoint.Path.Replace("{id}", "test"), request.RequestUri!.GetLeftPart(UriPartial.Path));
            Assert.Equal(endpoint.Methods[0], request.Method.Method);
        }
        Assert.Equal(Endpoints.All.Count, calls.Count);
    }

    [Fact]
    public async Task BinaryResponse()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) { Headers = { { "Content-Type", "image/png" } } },
        };
        var response = await _client.RequestAsync("GET", "/v1/anything");
        Assert.False(response.IsJson);
        Assert.Equal(4, response.Bytes.Length);
        Assert.Equal("image/png", response.ContentType);
    }

    [Fact]
    public async Task NonJsonSuccessForJsonOperation()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") };
        await Assert.ThrowsAsync<VeilleResponseDecodingException>(() => _client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" }));
    }

    [Fact]
    public async Task ApiError()
    {
        _handler.Responder = _ => Json(401, "{\"error\":\"Invalid API Key\"}");
        var error = await Assert.ThrowsAsync<VeilleApiException>(() => _client.RequestAsync("GET", "/v1/usage"));
        Assert.Equal(401, error.StatusCode);
        Assert.Equal("Invalid API Key", error.Message);
        Assert.Equal("Invalid API Key", error.ResponseBody!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NonJsonErrorBody()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("Bad gateway") };
        var error = await Assert.ThrowsAsync<VeilleApiException>(
            () => _client.RequestAsync("GET", "/v1/usage", null, new RequestOptions { MaxRetries = 0 }));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("Bad gateway", Encoding.UTF8.GetString(error.RawResponse.Body));
    }

    [Fact]
    public async Task TransportError()
    {
        _handler.Responder = _ => throw new HttpRequestException("network down");
        await Assert.ThrowsAsync<VeilleTransportException>(() => _client.RequestAsync("GET", "/v1/usage"));
    }

    [Fact]
    public async Task DeserializesIntoTypes()
    {
        _handler.Responder = _ => Json(200, "{\"value\":42}");
        var response = await _client.RequestAsync("GET", "/v1/usage");
        Assert.Equal(42, response.Deserialize<Payload>()!.Value);
    }

    private sealed record Payload(int Value);

    [Fact]
    public async Task RetriesThenSucceeds()
    {
        var calls = 0;
        _handler.Responder = _ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("reset");
            }
            if (calls == 2)
            {
                var busy = Json(429, "{}");
                busy.Headers.TryAddWithoutValidation("Retry-After", "0");
                return busy;
            }
            return calls == 3 ? Json(503, "{}") : Json(200, "{}");
        };
        var response = await _client.RequestAsync("GET", "/v1/usage");
        Assert.True(response.IsJson);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task RetryLimitsAndClientErrors()
    {
        using var limited = new VeilleClient(new VeilleClientOptions
        {
            ApiKey = "k",
            HttpClient = new HttpClient(_handler),
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });
        _handler.Responder = _ => Json(503, "{}");
        await Assert.ThrowsAsync<VeilleApiException>(() => limited.RequestAsync("GET", "/v1/usage"));
        Assert.Equal(3, _handler.Requests.Count);
        _handler.Responder = _ => Json(402, "{}");
        await Assert.ThrowsAsync<VeilleApiException>(() => limited.RequestAsync("GET", "/v1/usage"));
        Assert.Equal(4, _handler.Requests.Count);
        _handler.Responder = _ => Json(500, "{}");
        await Assert.ThrowsAsync<VeilleApiException>(
            () => limited.RequestAsync("GET", "/v1/usage", null, new RequestOptions { MaxRetries = 0 }));
        Assert.Equal(5, _handler.Requests.Count);
    }

    [Theory]
    [InlineData("Rate limit exceeded: You've exceeded the rate limit on your subscription", 2)]
    [InlineData("Rate limit exceeded for the API key: quota daily", 1)]
    [InlineData("Invalid API Key", 1)]
    public async Task RateLimitsAreRetriedButNotQuotas(string message, int expectedCalls)
    {
        var calls = 0;
        _handler.Responder = _ => ++calls == 1 ? Json(401, JsonSerializer.Serialize(new { error = message })) : Json(200, "{}");
        try
        {
            await _client.RequestAsync("GET", "/v1/usage");
        }
        catch (VeilleApiException)
        {
        }
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task AcceptsGlobalParameters()
    {
        await _client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com", Fields = "a,b.c", IsAsync = true, SoftFail = false });
        var query = _handler.Requests[0].Request.RequestUri!.Query;
        Assert.Contains("fields=a%2Cb.c", query);
        Assert.Contains("is_async=true", query);
        Assert.Contains("soft_fail=false", query);
    }

    [Fact]
    public async Task BatchKeepsOrderAndIsolatesFailures()
    {
        var running = 0;
        var peak = 0;
        var results = await _client.BatchAsync(Enumerable.Range(1, 6), async (n, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref running);
            if (n == 4)
            {
                throw new InvalidOperationException("boom");
            }
            return n * 10;
        }, concurrency: 2);
        Assert.Equal(2, peak);
        Assert.Equal(6, results.Count);
        Assert.False(results[3].Ok);
        Assert.Equal(60, results[5].Value);
        Assert.Equal(3, results[3].Index);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public async Task WaitForJob()
    {
        var statuses = new Queue<string>(new[] { "Queued", "Running", "CompletedJob" });
        _handler.Responder = _ => Json(200, "{\"status\":\"" + statuses.Dequeue() + "\",\"progress\":50}");
        var seen = 0;
        var job = await _client.WaitForJobAsync("abc", TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1), _ => seen++);
        Assert.Equal("CompletedJob", job.Json.GetProperty("status").GetString());
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal("/v1/jobs/abc", _handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task WaitForJobTimesOut()
    {
        _handler.Responder = _ => Json(200, "{\"status\":\"Running\"}");
        await Assert.ThrowsAsync<VeilleJobTimeoutException>(
            () => _client.WaitForJobAsync("abc", TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1)));
    }
}
