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
            RevitYear = 2025,
            AddinVersion = "0.0.0",
            DocumentName = "Placeholder.rvt",
            Capabilities = new List<string>
            {
                "status", "query", "query.filter", "query.id",
                "set", "set.dry-run", "audit",
                "export.dwg", "export.pdf", "export.ifc"
            }
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

    public Task<ScheduleInfo[]> ListSchedulesAsync()
    {
        return Task.FromResult(new[]
        {
            new ScheduleInfo { Id = 1001, Name = "Door Schedule", Category = "Doors", FieldCount = 5, RowCount = 12 },
            new ScheduleInfo { Id = 1002, Name = "Room Schedule", Category = "Rooms", FieldCount = 4, RowCount = 8 }
        });
    }

    public Task<ScheduleData> ExportScheduleAsync(ScheduleExportRequest request)
    {
        return Task.FromResult(new ScheduleData
        {
            Columns = new List<string> { "Name", "Level", "Type" },
            Rows = new List<Dictionary<string, string>>(),
            TotalRows = 0
        });
    }

    public Task<ScheduleCreateResult> CreateScheduleAsync(ScheduleCreateRequest request)
    {
        return Task.FromResult(new ScheduleCreateResult
        {
            ViewId = 2001,
            Name = request.Name,
            FieldCount = request.Fields?.Count ?? 0,
            RowCount = 0,
            PlacedOnSheet = null
        });
    }
}
