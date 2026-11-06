using Veille;

using var client = new VeilleClient(); // reads VEILLE_API_KEY
try
{
    var response = await client.RequestAsync("GET", "/v1/usage");
    Console.WriteLine(response.Text);
}
catch (VeilleApiException exception)
{
    Console.Error.WriteLine($"HTTP {exception.StatusCode}: {exception.Message}");
}
