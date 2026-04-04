using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class AuditController : WebApiController
{
    private readonly IRevitOperations _operations;

    public AuditController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/audit")]
    public async Task RunAudit()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        AuditRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<AuditRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Fail("Request body is required")));
            return;
        }

        try
        {
            var auditResult = await _operations.RunAuditAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Ok(auditResult)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<AuditResult>.Fail(ex.Message)));
        }
    }
}
