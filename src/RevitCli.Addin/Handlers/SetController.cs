using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class SetController : WebApiController
{
    private readonly Func<Action<Action<object?>>, Task<object?>> _revitInvoke;

    public SetController(Func<Action<Action<object?>>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Post, "/elements/set")]
    public async Task SetParameter()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var request = JsonSerializer.Deserialize<SetRequest>(body);

        var result = await _revitInvoke(setResult =>
        {
            // Placeholder: real implementation uses Transaction to modify parameters
            setResult(new SetResult { Affected = 0 });
        });

        var data = (SetResult)result!;

        var response = ApiResponse<SetResult>.Ok(data);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
