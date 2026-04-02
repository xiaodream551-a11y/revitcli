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
        SetRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SetRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<SetResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<SetResult>.Fail("Request body is required")));
            return;
        }

        var data = await _operations.SetParametersAsync(request);
        var response = ApiResponse<SetResult>.Ok(data);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
