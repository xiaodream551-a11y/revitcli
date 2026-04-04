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
    private readonly IRevitOperations _operations;

    public SetController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/elements/set")]
    public async Task SetParameter()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        SetRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<SetRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Fail("Request body is required")));
            return;
        }

        try
        {
            var data = await _operations.SetParametersAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Ok(data)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<SetResult>.Fail(ex.Message)));
        }
    }
}
