using System.Text;
using System.Text.Json;

namespace Veille;

/// <summary>A successful API response: JSON or a file (image, PDF…).</summary>
public sealed class ApiResponse
{
    private readonly Lazy<JsonElement> _json;

    internal ApiResponse(RawResponse raw)
    {
        RawResponse = raw;
        _json = new Lazy<JsonElement>(() =>
        {
            if (!IsJson)
            {
                throw new VeilleResponseDecodingException("The response is not JSON.", raw);
            }
            try
            {
                using var document = JsonDocument.Parse(raw.Body);
                return document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new VeilleResponseDecodingException("The JSON response could not be decoded.", raw, exception);
            }
        });
    }

    /// <summary>The buffered response.</summary>
    public RawResponse RawResponse { get; }

    /// <summary>HTTP status code.</summary>
    public int StatusCode => RawResponse.StatusCode;

    /// <summary>The Content-Type header, or null.</summary>
    public string? ContentType => RawResponse.ContentType;

    /// <summary>True when the Content-Type is application/json or +json.</summary>
    public bool IsJson
    {
        get
        {
            var mediaType = ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
            return mediaType is not null && (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal));
        }
    }

    /// <summary>The parsed JSON body.</summary>
    public JsonElement Json => _json.Value;

    /// <summary>The body bytes (image, PDF, JSON text…).</summary>
    public byte[] Bytes => RawResponse.Body;

    /// <summary>The body decoded as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(RawResponse.Body);

    /// <summary>Deserializes the JSON body with System.Text.Json.</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="options">Serializer options; defaults to web defaults (case-insensitive).</param>
    /// <returns>The deserialized value.</returns>
    public T? Deserialize<T>(JsonSerializerOptions? options = null)
    {
        try
        {
            return Json.Deserialize<T>(options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            throw new VeilleResponseDecodingException($"The JSON response could not be mapped to {typeof(T).Name}.", RawResponse, exception);
        }
    }
}
