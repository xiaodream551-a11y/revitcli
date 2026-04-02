using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ExportController : WebApiController
{
    private readonly Func<Action<Action<object?>>, Task<object?>> _revitInvoke;

    public ExportController(Func<Action<Action<object?>>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Post, "/export")]
    public async Task Export()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var request = JsonSerializer.Deserialize<ExportRequest>(body);

        var result = await _revitInvoke(setResult =>
        {
            // Placeholder: real implementation initiates export via Revit API
            setResult(new ExportProgress
            {
                TaskId = Guid.NewGuid().ToString("N")[..8],
                Status = "completed",
                Progress = 100
            });
        });

        var progress = (ExportProgress)result!;

        var response = ApiResponse<ExportProgress>.Ok(progress);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }

    [Route(HttpVerbs.Get, "/tasks/{taskId}")]
    public async Task GetProgress(string taskId)
    {
        var progress = new ExportProgress
        {
            TaskId = taskId,
            Status = "completed",
            Progress = 100
        };

        var response = ApiResponse<ExportProgress>.Ok(progress);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
