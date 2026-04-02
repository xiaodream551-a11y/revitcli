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
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        AuditRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AuditRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<AuditResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            HttpContext.Response.ContentType = "application/json";
            await using var errWriter = HttpContext.OpenResponseText();
            await errWriter.WriteAsync(JsonSerializer.Serialize(ApiResponse<AuditResult>.Fail("Request body is required")));
            return;
        }

        var auditResult = await _operations.RunAuditAsync(request);
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
