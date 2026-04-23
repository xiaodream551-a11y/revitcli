using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class SnapshotController : WebApiController
{
    private readonly IRevitOperations _operations;

    public SnapshotController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/snapshot")]
    public async Task CaptureSnapshot()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        SnapshotRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = string.IsNullOrWhiteSpace(body)
                ? new SnapshotRequest()
                : JsonSerializer.Deserialize<SnapshotRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        request ??= new SnapshotRequest();

        try
        {
            var data = await _operations.CaptureSnapshotAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Ok(data)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
    }
}
