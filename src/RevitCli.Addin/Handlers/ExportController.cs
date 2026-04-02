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
        var request = JsonSerializer.Deserialize<ExportRequest>(body)!;

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
