using System.Text.Json;

namespace Samples;

// Deliberately broken. Used by EDGE-101.2 to verify each persona in isolation
// and by EDGE-101.3/101.4 for an end-to-end run.
//
// Planted defects, in descending severity:
//   [BLOCKER] swallowed exception - failures vanish silently
//   [MAJOR]   HttpClient created per call - socket exhaustion under load
//   [MAJOR]   undisposed StreamReader
//   [NIT]     non-descriptive parameter name
public class ReportFetcher
{
    public string Fetch(string u)
    {
        try
        {
            var client = new HttpClient();
            var response = client.GetAsync(u).Result;
            var reader = new StreamReader(response.Content.ReadAsStream());
            var body = reader.ReadToEnd();

            return JsonSerializer.Deserialize<string>(body) ?? "";
        }
        catch (Exception)
        {
        }

        return "";
    }
}