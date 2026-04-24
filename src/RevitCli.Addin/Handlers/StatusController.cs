using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class StatusController : WebApiController
{
    private readonly IRevitOperations _operations;

    public StatusController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Get, "/status")]
    public async Task GetStatus()
    {
        var status = await _operations.GetStatusAsync();
        var response = ApiResponse<StatusInfo>.Ok(status);
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
