using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class SetController : WebApiController
{
    private readonly IRevitOperations _operations;

    public SetController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/elements/set")]
    public async Task SetParameter()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var request = JsonSerializer.Deserialize<SetRequest>(body)!;

        var data = await _operations.SetParametersAsync(request);
        var response = ApiResponse<SetResult>.Ok(data);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
