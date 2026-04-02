using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Config;
using RevitCli.Shared;

namespace RevitCli.Client;

public class RevitClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool Verbose { get; set; }

    private async Task<string> SendAndRead(HttpResponseMessage response, string method, string url)
    {
        var json = await response.Content.ReadAsStringAsync();
        if (Verbose)
        {
            Console.Error.WriteLine($"[HTTP] {method} {url} -> {(int)response.StatusCode}");
        }
        return json;
    }

    public RevitClient(HttpClient http)
    {
        _http = http;
    }

    public RevitClient(string baseUrl = "http://localhost:17839")
    {
        _http = new HttpClient { BaseAddress = new System.Uri(baseUrl) };
    }

    public static string DiscoverServerUrl(string configuredUrl)
    {
        try
        {
            var serverInfoPath = CliConfig.ServerInfoPath;
            if (File.Exists(serverInfoPath))
            {
                var json = File.ReadAllText(serverInfoPath);
                var info = JsonSerializer.Deserialize<ServerInfo>(json);
                if (info != null && info.Port > 0)
                {
                    // Verify the process is still alive
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                        if (!proc.HasExited)
                            return $"http://localhost:{info.Port}";
                    }
                    catch (System.ArgumentException) { /* process not found */ }
                }
            }
        }
        catch { }
        return configuredUrl;
    }

    public async Task<ApiResponse<StatusInfo>> GetStatusAsync()
    {
        try
        {
            var url = "/api/status";
            var response = await _http.GetAsync(url);
            var json = await SendAndRead(response, "GET", url);
            return JsonSerializer.Deserialize<ApiResponse<StatusInfo>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<StatusInfo>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<StatusInfo>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ElementInfo[]>> QueryElementsAsync(string? category, string? filter)
    {
        try
        {
            var parts = new List<string>();
            if (category != null) parts.Add($"category={System.Uri.EscapeDataString(category)}");
            if (filter != null) parts.Add($"filter={System.Uri.EscapeDataString(filter)}");
            var url = $"/api/elements?{string.Join("&", parts)}";

            var response = await _http.GetAsync(url);
            var json = await SendAndRead(response, "GET", url);
            return JsonSerializer.Deserialize<ApiResponse<ElementInfo[]>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ElementInfo[]>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ElementInfo[]>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ElementInfo>> QueryElementByIdAsync(int id)
    {
        try
        {
            var url = $"/api/elements/{id}";
            var response = await _http.GetAsync(url);
            var json = await SendAndRead(response, "GET", url);
            return JsonSerializer.Deserialize<ApiResponse<ElementInfo>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ElementInfo>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ElementInfo>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ExportProgress>> ExportAsync(ExportRequest request)
    {
        try
        {
            var url = "/api/export";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<ExportProgress>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ExportProgress>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ExportProgress>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ExportProgress>> GetExportProgressAsync(string taskId)
    {
        try
        {
            var url = $"/api/tasks/{taskId}";
            var response = await _http.GetAsync(url);
            var json = await SendAndRead(response, "GET", url);
            return JsonSerializer.Deserialize<ApiResponse<ExportProgress>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ExportProgress>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ExportProgress>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<SetResult>> SetParameterAsync(SetRequest request)
    {
        try
        {
            var url = "/api/elements/set";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<SetResult>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<SetResult>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<SetResult>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<AuditResult>> AuditAsync(AuditRequest request)
    {
        try
        {
            var url = "/api/audit";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<AuditResult>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<AuditResult>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<AuditResult>.Fail($"Communication error: {ex.Message}");
        }
    }
}
