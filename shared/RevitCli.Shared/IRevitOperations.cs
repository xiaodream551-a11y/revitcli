using System.Threading.Tasks;

namespace RevitCli.Shared;

public interface IRevitOperations
{
    Task<StatusInfo> GetStatusAsync();
    Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter);
    Task<ElementInfo?> GetElementByIdAsync(int id);
    Task<ExportProgress> ExportAsync(ExportRequest request);
    Task<ExportProgress> GetExportProgressAsync(string taskId);
    Task<SetResult> SetParametersAsync(SetRequest request);
    Task<AuditResult> RunAuditAsync(AuditRequest request);
}
