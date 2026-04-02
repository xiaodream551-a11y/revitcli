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
    private readonly Func<Action<object?>, Task<object?>> _revitInvoke;

    public SetController(Func<Action<object?>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Post, "/elements/set")]
    public async Task SetParameter()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var request = JsonSerializer.Deserialize<SetRequest>(body);

        var result = await _revitInvoke(_ => { });

        var setResult = result as SetResult ?? new SetResult { Affected = 0 };

        var response = ApiResponse<SetResult>.Ok(setResult);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
