using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class StatusController : WebApiController
{
    private readonly Func<Action<object?>, Task<object?>> _revitInvoke;

    public StatusController(Func<Action<object?>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Get, "/status")]
    public async Task GetStatus()
    {
        var result = await _revitInvoke(_ => { });

        var status = result as StatusInfo ?? new StatusInfo
        {
            RevitVersion = "2025",
            DocumentName = "Placeholder.rvt"
        };

        var response = ApiResponse<StatusInfo>.Ok(status);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
