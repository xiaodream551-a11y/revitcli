using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Server;

public sealed class ReloadController : WebApiController
{
    private readonly IReloadService _reloadService;

    public ReloadController(IReloadService reloadService)
    {
        _reloadService = reloadService;
    }

    [Route(HttpVerbs.Post, "/reload")]
    public async Task Reload()
    {
        var result = await _reloadService.RequestReloadAsync();
        HttpContext.Response.StatusCode = result.Status switch
        {
            "accepted" => 202,
            "already-running" => 409,
            "shutting-down" => 503,
            "unsupported" => 405,
            _ => 500
        };
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();
        var response = result.Status == "accepted"
            ? ApiResponse<ReloadResponse>.Ok(result)
            : ApiResponse<ReloadResponse>.Fail(result.Message);
        response.Data = result;
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
