using Veille;

using var client = new VeilleClient(); // reads VEILLE_API_KEY
var submitted = await client.IntelligenceEmailAsync(new IntelligenceEmailParams { Query = "jane.doe@example.com", IsAsync = true });
var job = await client.WaitForJobAsync(
    submitted.Json.GetProperty("job_id").GetString()!,
    onProgress: j => Console.WriteLine($"{j.GetProperty("status")} {j.GetProperty("progress")}%"));
Console.WriteLine(job.Json);
