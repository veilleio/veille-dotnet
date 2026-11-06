# Veille .NET SDK

The official .NET SDK for the [Veille](https://www.veille.io) fraud prevention API.

Validate emails, domains, IPs, VAT numbers and IBANs in milliseconds to block fake sign-ups, bots and fraud.

The SDK exposes **11 typed async methods**, one per documented endpoint (with `required` properties checked at compile time), plus a generic `RequestAsync`.

## Requirements

- .NET 8 or newer

## Installation

```sh
dotnet add package Veille
```

## Get your API key

[Sign up for Veille](https://app.veille.io/register) or sign in, then copy your API key from the [dashboard](https://app.veille.io). `new VeilleClient()` reads the `VEILLE_API_KEY` environment variable.

## Quick start

```csharp
using Veille;

using var client = new VeilleClient();
var response = await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" });
Console.WriteLine(response.Json);
```

Parameter classes mirror the [API reference](https://app.veille.io/openapi). Use `response.Deserialize<MyType>()` to map the JSON onto your own types.

## Async jobs

Any call accepts `is_async: true` (useful past the 60-second server limit): the response is then a `job_id`. `WaitForJobAsync` polls `GET /v1/jobs/{id}` until a terminal status (`CompletedJob`, `Error`, `NotFound`, `BadRequest`):

```csharp
var submitted = await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com", IsAsync = true });
var job = await client.WaitForJobAsync(
    submitted.Json.GetProperty("job_id").GetString()!,
    onProgress: j => Console.WriteLine($"{j.GetProperty("status")} {j.GetProperty("progress")}%"));
```

## GET or POST

Methods use GET when the endpoint supports it (query string). POST sends the same parameters as JSON:

## Any endpoint

```csharp
var response = await client.RequestAsync("GET", "/v1/usage");
```

## Errors

```csharp
try
{
    await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" });
}
catch (VeilleApiException exception)
{
    Console.Error.WriteLine($"{exception.StatusCode}: {exception.Message}");
}
```

| Exception | When |
| --- | --- |
| `VeilleConfigurationException` | Missing API key, invalid options or unsupported method. |
| `VeilleSerializationException` | A required parameter is missing or a value can't be sent. |
| `VeilleTransportException` | Network failure or timeout. |
| `VeilleApiException` | Non-2xx status (`StatusCode`, `ErrorMessage`, `ResponseBody`, `RawResponse`). |
| `VeilleResponseDecodingException` | A success response could not be decoded. |

All of them derive from `VeilleException`. Caller cancellation surfaces as `OperationCanceledException`.

## Retries

Requests are retried up to 3 times after a network error, a `408`, `500`, `502`, `503` or `504`, or a rate limit — which the API returns as `401` (or `429`) with a `Rate limit exceeded` message. Per-key quota errors (`…: quota daily`) are not retried, nor are `400`, `402`, `404` or client-side timeouts. The delay starts at 1 second and grows 1.5× per retry (capped at 30 seconds); a `Retry-After` header wins.

```csharp
using var client = new VeilleClient(new VeilleClientOptions { MaxRetries = 5, RetryDelay = TimeSpan.FromMilliseconds(500) });
await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" }, new RequestOptions { MaxRetries = 0 }); // one request without retries
```

## Batch requests

`BatchAsync` runs a function per item with at most 10 in flight (`concurrency`) and returns one `BatchResult` per item, in input order. A failing item never stops the others.

```csharp
string[] inputs = { "jane.doe@example.com", "john@mailinator.com", "contact@veille.io" };
var results = await client.BatchAsync(inputs, (value, ct) =>
    client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = value }, cancellationToken: ct), concurrency: 5);
foreach (var result in results)
{
    if (result.Ok)
    {
        Console.WriteLine(result.Value!.Json);
    }
    else
    {
        Console.Error.WriteLine($"{inputs[result.Index]}: {result.Error!.Message}");
    }
}
```

| Constant | Default | Description |
| --- | --- | --- |
| `VeilleClient.DefaultConcurrency` | `10` | Tasks run at the same time by batch |
| `VeilleClient.DefaultTimeout` | `TimeSpan.FromSeconds(120)` | Request timeout |
| `VeilleClient.MaxRetries` | `3` | Retry attempts after a network error, a 408/5xx or a rate limit |
| `VeilleClient.RetryBackoffFactor` | `1.5` | Exponential backoff multiplier |
| `VeilleClient.DefaultRetryDelay` | `TimeSpan.FromSeconds(1)` | Delay before the first retry |

## Configuration

```csharp
using var client = new VeilleClient(new VeilleClientOptions
{
    ApiKey = Environment.GetEnvironmentVariable("VEILLE_API_KEY"),
    Timeout = TimeSpan.FromSeconds(60), // default 120 s
    HttpClient = myHttpClient,           // optional, you keep ownership
});
```

## Endpoints

11 methods, generated from the [OpenAPI document](https://app.veille.io/openapi).

| Method | HTTP | Description |
| --- | --- | --- |
| `IntelligenceDomainAsync` | GET `/v1/intelligence/domain` | Check any domain instantly. Detects disposable email providers, public webmail, relay services, and education domains. Returns DNS health w… |
| `IntelligenceEmailAsync` | GET `/v1/intelligence/email` | Validate any email address in a single call. Detects disposable and temporary inboxes, identifies role accounts, public webmail, relay serv… |
| `IntelligenceEmailqAsync` | GET `/v1/intelligence/emailq` | Deep email deliverability verification. Performs full Email Validation plus SMTP inbox check, catch-all detection, and returns a qualificat… |
| `IntelligenceIpAsync` | GET `/v1/intelligence/ip` | Assess the reputation of any IP address. Detects VPNs, proxies, Tor exit nodes, datacenter hosting, abusive traffic, and bots. Returns geol… |
| `IntelligencePhoneAsync` | GET `/v1/intelligence/phone` | Validate any phone number in E.164 format. Detects disposable and virtual numbers, identifies carrier and line type (mobile/fixed/VoIP), re… |
| `JobsAsync` | GET `/v1/jobs/{id}` | Poll the status, progress (0–100) and result of a job started with is_async=true. Polling is free. Poll until status is terminal: Completed… |
| `StatsAsync` | GET `/v1/stats` | Infrastructure health for an engine or endpoint over the last hour. Not scoped to your account or tenant. Enabled per account: HTTP 403 mea… |
| `UsageAsync` | GET `/v1/usage` | Retrieve real-time usage statistics and credit balance for your Veille account. |
| `VatAsync` | GET `/v1/vat` | Validate a European VAT number against the VIES database. Returns company name, registered address, and validity status for any EU member s… |
| `VatIbanAsync` | GET `/v1/vat/iban` | Validate any IBAN and retrieve associated bank details. Returns the BIC/SWIFT code, bank name, country, BBAN structural breakdown, and SEPA… |
| `VatRatesAsync` | GET `/v1/vat/rates` | Get current ECB exchange rates for any supported base currency. Optionally filter to specific target currencies. Updated daily from the Eur… |

## All Veille SDKs

| SDK | Package | Install |
| --- | --- | --- |
| Node.js | [`@veilleio/sdk`](https://www.npmjs.com/package/@veilleio/sdk) | `npm install @veilleio/sdk` |
| Python | [`veille`](https://pypi.org/project/veille/) | `pip install veille` |
| Go | [`veille-go`](https://pkg.go.dev/github.com/veilleio/veille-go) | `go get github.com/veilleio/veille-go` |
| .NET | [`Veille`](https://www.nuget.org/packages/Veille) | `dotnet add package Veille` |
| Ruby | [`veille`](https://rubygems.org/gems/veille) | `gem install veille` |
| PHP | [`veille/sdk`](https://packagist.org/packages/veille/sdk) | `composer require veille/sdk` |
| Java | [`veille-java`](https://github.com/veilleio/veille-java) | build from source, see its README |
| CLI | [`@veilleio/cli`](https://www.npmjs.com/package/@veilleio/cli) | `npm install -g @veilleio/cli` |
| MCP server | [`@veilleio/mcp`](https://www.npmjs.com/package/@veilleio/mcp) | `npx -y @veilleio/mcp` |

## License

MIT — see [LICENSE](LICENSE).
