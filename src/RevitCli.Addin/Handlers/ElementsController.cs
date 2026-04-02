using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ElementsController : WebApiController
{
    private readonly Func<Action<object?>, Task<object?>> _revitInvoke;

    public ElementsController(Func<Action<object?>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Get, "/elements")]
    public async Task QueryElements([QueryField] string? category, [QueryField] string? filter)
    {
        var result = await _revitInvoke(_ => { });

        var elements = result as ElementInfo[] ?? Array.Empty<ElementInfo>();

        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }

    [Route(HttpVerbs.Get, "/elements/{id}")]
    public async Task GetElement(int id)
    {
        var result = await _revitInvoke(_ => { });

        var element = result as ElementInfo ?? new ElementInfo { Id = id, Name = $"Element {id}" };

        var response = ApiResponse<ElementInfo>.Ok(element);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
