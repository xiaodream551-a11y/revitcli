using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Shared;

namespace RevitCli.Client;

public class RevitClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RevitClient(HttpClient http)
    {
        _http = http;
    }

    public RevitClient(string baseUrl = "http://localhost:17839")
    {
        _http = new HttpClient { BaseAddress = new System.Uri(baseUrl) };
    }

    public async Task<ApiResponse<StatusInfo>> GetStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/status");
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<StatusInfo>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<StatusInfo>.Fail("Revit is not running or plugin is not loaded.");
        }
    }

    public async Task<ApiResponse<ElementInfo[]>> QueryElementsAsync(string? category, string? filter)
    {
        try
        {
            var query = new StringBuilder("/api/elements?");
            if (category != null) query.Append($"category={category}");
            if (filter != null) query.Append($"&filter={System.Uri.EscapeDataString(filter)}");

            var response = await _http.GetAsync(query.ToString());
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<ElementInfo[]>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<ElementInfo[]>.Fail("Revit is not running or plugin is not loaded.");
        }
    }

    public async Task<ApiResponse<ElementInfo>> QueryElementByIdAsync(int id)
    {
        try
        {
            var response = await _http.GetAsync($"/api/elements/{id}");
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<ElementInfo>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<ElementInfo>.Fail("Revit is not running or plugin is not loaded.");
        }
    }

    public async Task<ApiResponse<ExportProgress>> ExportAsync(ExportRequest request)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync("/api/export", content);
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<ExportProgress>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<ExportProgress>.Fail("Revit is not running or plugin is not loaded.");
        }
    }

    public async Task<ApiResponse<ExportProgress>> GetExportProgressAsync(string taskId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/tasks/{taskId}");
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<ExportProgress>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<ExportProgress>.Fail("Revit is not running or plugin is not loaded.");
        }
    }

    public async Task<ApiResponse<SetResult>> SetParameterAsync(SetRequest request)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync("/api/elements/set", content);
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<SetResult>>(json, JsonOptions)!;
        }
        catch (HttpRequestException)
        {
            return ApiResponse<SetResult>.Fail("Revit is not running or plugin is not loaded.");
        }
    }
}
