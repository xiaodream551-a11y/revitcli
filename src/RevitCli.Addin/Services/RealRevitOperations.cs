using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

    public Task<ElementInfo?> GetElementByIdAsync(int id)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Element id must be a positive integer.");

        return _bridge.InvokeAsync<ElementInfo?>(app =>
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active document is open.");

            var element = doc.GetElement(new ElementId(id));
            if (element == null)
                return null;

            return MapElement(doc, element);
        });
    }

    private static ElementInfo MapElement(Document doc, Element element)
    {
        return new ElementInfo
        {
            Id = ToCliElementId(element.Id),
            Name = !string.IsNullOrWhiteSpace(element.Name) ? element.Name : element.GetType().Name,
            Category = element.Category?.Name ?? "",
            TypeName = ResolveTypeName(doc, element),
            Parameters = ReadVisibleParameters(doc, element)
        };
    }

    private static string ResolveTypeName(Document doc, Element element)
    {
        if (element is ElementType)
            return element.Name;

        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return "";

        var typeElement = doc.GetElement(typeId) as ElementType;
        return typeElement?.Name ?? "";
    }

    private static Dictionary<string, string> ReadVisibleParameters(Document doc, Element element)
    {
        var result = new Dictionary<string, string>();
        var nameCounts = new Dictionary<string, int>();

        foreach (var param in element.GetOrderedParameters())
        {
            if (!param.HasValue)
                continue;

            var value = FormatParameterValue(doc, param);
            if (value == null)
                continue;

            var baseName = param.Definition.Name;

            // Handle duplicate parameter names
            if (!nameCounts.TryGetValue(baseName, out var count))
            {
                nameCounts[baseName] = 1;
                result[baseName] = value;
            }
            else
            {
                nameCounts[baseName] = count + 1;
                result[$"{baseName} [{count + 1}]"] = value;
            }
        }

        return result;
    }

    private static string? FormatParameterValue(Document doc, Parameter parameter)
    {
        // Prefer AsValueString — closest to Revit UI display
        var valueString = parameter.AsValueString();
        if (!string.IsNullOrEmpty(valueString))
            return valueString;

        // Fallback by storage type
        return parameter.StorageType switch
        {
            StorageType.String => NullIfEmpty(parameter.AsString()),
            StorageType.Integer => parameter.AsInteger().ToString(),
            StorageType.Double => null, // Skip raw internal doubles — no reliable unit formatting without AsValueString
            StorageType.ElementId => FormatElementIdValue(doc, parameter.AsElementId()),
            _ => null
        };
    }

    private static string? FormatElementIdValue(Document doc, ElementId refId)
    {
        if (refId == ElementId.InvalidElementId)
            return null;

        var refElement = doc.GetElement(refId);
        if (refElement != null)
            return $"{refId.Value}: {refElement.Name}";

        return refId.Value.ToString();
    }

    private static int ToCliElementId(ElementId elementId) =>
        checked((int)elementId.Value);

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    // Remaining methods keep placeholder behavior until implemented

    public Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter)
        => Task.FromResult(Array.Empty<ElementInfo>());

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
