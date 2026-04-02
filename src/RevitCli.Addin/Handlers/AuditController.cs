using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class AuditController : WebApiController
{
    private readonly Func<Action<Action<object?>>, Task<object?>> _revitInvoke;

    public AuditController(Func<Action<Action<object?>>, Task<object?>> revitInvoke)
    {
        _revitInvoke = revitInvoke;
    }

    [Route(HttpVerbs.Post, "/audit")]
    public async Task RunAudit()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var request = JsonSerializer.Deserialize<AuditRequest>(body);

        var result = await _revitInvoke(setResult =>
        {
            // Placeholder: real implementation will run rules against the Revit model
            setResult(new AuditResult
            {
                Passed = 5,
                Failed = 0,
                Issues = new List<AuditIssue>()
            });
        });

        var auditResult = (AuditResult)result!;
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();
        await writer.WriteAsync(JsonSerializer.Serialize(response));
    }
}
