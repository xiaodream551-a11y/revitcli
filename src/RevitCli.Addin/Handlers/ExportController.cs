using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ExportController : WebApiController
{
    private readonly IRevitOperations _operations;

    public ExportController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/export")]
    public async Task Export()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        ExportRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ExportRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<ExportProgress>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<ExportProgress>.Fail("Request body is required")));
            return;
        }

        var progress = await _operations.ExportAsync(request);
        var response = ApiResponse<ExportProgress>.Ok(progress);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }

    [Route(HttpVerbs.Get, "/tasks/{taskId}")]
    public async Task GetProgress(string taskId)
    {
        var progress = await _operations.GetExportProgressAsync(taskId);
        var response = ApiResponse<ExportProgress>.Ok(progress);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
