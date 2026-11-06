using Veille;

using var client = new VeilleClient(); // reads VEILLE_API_KEY
var response = await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com" });
Console.WriteLine(response.Json);
