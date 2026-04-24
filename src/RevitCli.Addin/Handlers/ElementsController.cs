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
    private readonly IRevitOperations _operations;

    public ElementsController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Get, "/elements")]
    public async Task QueryElements([QueryField] string? category, [QueryField] string? filter)
    {
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();

        try
        {
            var elements = await _operations.QueryElementsAsync(category, filter);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo[]>.Ok(elements)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo[]>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo[]>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo[]>.Fail(ex.Message)));
        }
    }

    [Route(HttpVerbs.Get, "/elements/{id}")]
    public async Task GetElement(long id)
    {
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();

        try
        {
            var element = await _operations.GetElementByIdAsync(id);

            if (element == null)
            {
                HttpContext.Response.StatusCode = 404;
                await writer.WriteAsync(JsonSerializer.Serialize(
                    ApiResponse<ElementInfo>.Fail($"Element {id} not found")));
                return;
            }

            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo>.Ok(element)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ElementInfo>.Fail(ex.Message)));
        }
    }
}
