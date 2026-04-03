using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitCli.Addin.Bridge;
using RevitCli.Shared;

namespace RevitCli.Addin.Services;

public sealed class RealRevitOperations : IRevitOperations
{
    private readonly RevitBridge _bridge;

    public RealRevitOperations(RevitBridge bridge)
    {
        _bridge = bridge;
    }

    public Task<StatusInfo> GetStatusAsync()
    {
        return _bridge.InvokeAsync(app =>
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc?.Document;
            return new StatusInfo
            {
                RevitVersion = app.Application.VersionNumber,
                DocumentName = doc?.Title,
                DocumentPath = string.IsNullOrWhiteSpace(doc?.PathName) ? null : doc.PathName
            };
        });
    }

    // Remaining methods keep placeholder behavior until implemented

    public Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter)
        => Task.FromResult(Array.Empty<ElementInfo>());

    public Task<ElementInfo?> GetElementByIdAsync(int id)
        => Task.FromResult<ElementInfo?>(new ElementInfo { Id = id, Name = $"Element {id}" });

    public Task<ExportProgress> ExportAsync(ExportRequest request)
        => Task.FromResult(new ExportProgress
        {
            TaskId = Guid.NewGuid().ToString("N")[..8],
            Status = "completed",
            Progress = 100
        });

    public Task<ExportProgress> GetExportProgressAsync(string taskId)
        => Task.FromResult(new ExportProgress
        {
            TaskId = taskId,
            Status = "completed",
            Progress = 100
        });

    public Task<SetResult> SetParametersAsync(SetRequest request)
        => Task.FromResult(new SetResult { Affected = 0 });

    public Task<AuditResult> RunAuditAsync(AuditRequest request)
        => Task.FromResult(new AuditResult
        {
            Passed = 5,
            Failed = 0,
            Issues = new List<AuditIssue>()
        });
}
