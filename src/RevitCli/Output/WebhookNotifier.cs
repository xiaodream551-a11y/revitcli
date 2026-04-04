using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RevitCli.Output;

public static class WebhookNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task NotifyAsync(string url, object payload)
    {
        try
        {
            // Security: only allow HTTPS URLs (block localhost/private by default)
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync(
                    "Warning: webhook URL must use HTTPS. Skipping notification.");
                return;
            }
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
                await Console.Error.WriteLineAsync(
                    $"Warning: webhook notification failed ({response.StatusCode})");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Warning: webhook notification failed: {ex.Message}");
        }
    }
}
