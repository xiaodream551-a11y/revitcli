using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ElementsController : WebApiController
{
    private readonly IRevitOperations _operations;

    public ElementsController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Get, "/elements")]
    public async Task QueryElements([QueryField] string? category, [QueryField] string? filter)
    {
        var elements = await _operations.QueryElementsAsync(category, filter);
        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }

    [Route(HttpVerbs.Get, "/elements/{id}")]
    public async Task GetElement(int id)
    {
        var element = await _operations.GetElementByIdAsync(id);
        var response = ApiResponse<ElementInfo?>.Ok(element);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
