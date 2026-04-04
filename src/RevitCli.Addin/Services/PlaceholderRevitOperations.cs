using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitCli.Shared;

namespace RevitCli.Addin.Services;

public class PlaceholderRevitOperations : IRevitOperations
{
    public Task<StatusInfo> GetStatusAsync()
    {
        return Task.FromResult(new StatusInfo
        {
            RevitVersion = "2025",
            DocumentName = "Placeholder.rvt"
        });
    }

    public Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter)
    {
        return Task.FromResult(Array.Empty<ElementInfo>());
    }

    public Task<ElementInfo?> GetElementByIdAsync(long id)
    {
        return Task.FromResult<ElementInfo?>(new ElementInfo { Id = id, Name = $"Element {id}" });
    }

    public Task<ExportProgress> ExportAsync(ExportRequest request)
    {
        return Task.FromResult(new ExportProgress
        {
            TaskId = Guid.NewGuid().ToString("N")[..8],
            Status = "completed",
            Progress = 100
        });
    }

    public Task<ExportProgress> GetExportProgressAsync(string taskId)
    {
        return Task.FromResult(new ExportProgress
        {
            TaskId = taskId,
            Status = "completed",
            Progress = 100
        });
    }

    public Task<SetResult> SetParametersAsync(SetRequest request)
    {
        return Task.FromResult(new SetResult { Affected = 0 });
    }

    public Task<AuditResult> RunAuditAsync(AuditRequest request)
    {
        return Task.FromResult(new AuditResult
        {
            Passed = 5,
            Failed = 0,
            Issues = new List<AuditIssue>()
        });
    }
}
