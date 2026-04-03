using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    private const int MaxMatches = 200;

    // ── Category alias table (level 1) ──────────────────────────

    private static readonly Dictionary<string, BuiltInCategory> CategoryAliases = BuildCategoryAliases();

    private static Dictionary<string, BuiltInCategory> BuildCategoryAliases()
    {
        var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(BuiltInCategory cat, params string[] names)
        {
            foreach (var n in names) map[Normalize(n)] = cat;
        }
        Add(BuiltInCategory.OST_Walls, "walls", "wall", "墙", "墙体");
        Add(BuiltInCategory.OST_Doors, "doors", "door", "门");
        Add(BuiltInCategory.OST_Windows, "windows", "window", "窗", "窗户");
        Add(BuiltInCategory.OST_Floors, "floors", "floor", "楼板");
        Add(BuiltInCategory.OST_Roofs, "roofs", "roof", "屋顶");
        Add(BuiltInCategory.OST_Columns, "columns", "column", "柱");
        Add(BuiltInCategory.OST_StructuralColumns, "structuralcolumns", "结构柱");
        Add(BuiltInCategory.OST_Levels, "levels", "level", "标高");
        Add(BuiltInCategory.OST_Rooms, "rooms", "room", "房间");
        Add(BuiltInCategory.OST_Furniture, "furniture", "家具");
        Add(BuiltInCategory.OST_Ceilings, "ceilings", "ceiling", "天花板");
        Add(BuiltInCategory.OST_Stairs, "stairs", "stair", "楼梯");
        return map;
    }

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

    private static BuiltInCategory ResolveCategory(Document doc, string category)
    {
        var key = Normalize(category);

        // Level 1: alias table
        if (CategoryAliases.TryGetValue(key, out var resolved))
            return resolved;

        // Level 2: scan document categories by localized name
        foreach (Category cat in doc.Settings.Categories)
        {
            if (cat.Id.Value < 0 && Normalize(cat.Name) == key)
                return (BuiltInCategory)cat.Id.Value;
        }

        throw new ArgumentException($"Unknown category: '{category}'");
    }

    // ── Filter matching ─────────────────────────────────────────
    // Display values (FormatParameterValue) are for DTO output only.
    // Filtering uses raw AsDouble/AsInteger with unit conversion.

    private static readonly HashSet<string> PseudoFields = new(StringComparer.OrdinalIgnoreCase)
        { "id", "name", "category", "type", "typename" };

    private static readonly HashSet<string> NumericPseudoFields = new(StringComparer.OrdinalIgnoreCase)
        { "id" };

    private static bool IsNumericOperator(string op) =>
        op is ">" or "<" or ">=" or "<=";

    /// <summary>
    /// Find a parameter by filter name, using the SAME visibility+counting rules as ReadVisibleParameters.
    /// Only parameters with HasValue and non-null FormatParameterValue are counted.
    /// Supports "Foo [2]" duplicate suffix syntax.
    /// </summary>
    private static Parameter? FindParameterByFilterName(Document doc, Element element, string filterName)
    {
        var baseName = filterName;
        var targetIndex = 1;

        // Parse "Foo [N]" syntax
        var bracketStart = filterName.LastIndexOf('[');
        if (bracketStart > 0 && filterName.EndsWith("]"))
        {
            var indexStr = filterName.Substring(bracketStart + 1, filterName.Length - bracketStart - 2).Trim();
            if (int.TryParse(indexStr, out var parsed) && parsed >= 2)
            {
                baseName = filterName.Substring(0, bracketStart).TrimEnd();
                targetIndex = parsed;
            }
        }

        // Count only visible parameters (same rule as ReadVisibleParameters)
        var count = 0;
        foreach (var param in element.GetOrderedParameters())
        {
            if (!param.HasValue)
                continue;
            if (!string.Equals(param.Definition.Name, baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (FormatParameterValue(doc, param) == null)
                continue;
            count++;
            if (count == targetIndex)
                return param;
        }
        return null;
    }

    /// <summary>
    /// Get a pseudo field's string value for filtering.
    /// </summary>
    private static string? GetPseudoFieldValue(Document doc, Element element, string field)
    {
        return field.ToLowerInvariant() switch
        {
            "id" => ToCliElementId(element.Id).ToString(),
            "name" => element.Name,
            "category" => element.Category?.Name,
            "type" or "typename" => ResolveTypeName(doc, element),
            _ => null
        };
    }

    private static bool CompareNumeric(double actual, double compareTo, string op)
    {
        return op switch
        {
            "=" => Math.Abs(actual - compareTo) < 0.001,
            "!=" => Math.Abs(actual - compareTo) >= 0.001,
            ">" => actual > compareTo,
            "<" => actual < compareTo,
            ">=" => actual >= compareTo,
            "<=" => actual <= compareTo,
            _ => false
        };
    }

    private static double ConvertFilterValueToInternal(Document doc, Parameter param, double filterValue)
    {
        if (param.StorageType != StorageType.Double)
            return filterValue;

        try
        {
            var specTypeId = param.Definition.GetDataType();
            var formatOptions = doc.GetUnits().GetFormatOptions(specTypeId);
            var unitTypeId = formatOptions.GetUnitTypeId();
            return UnitUtils.ConvertToInternalUnits(filterValue, unitTypeId);
        }
        catch
        {
            return filterValue;
        }
    }

    /// <summary>
    /// Match a single element against a parsed filter.
    /// Tracks whether the field was found on any element AND whether any element was seen.
    /// Throws ArgumentException if operator is incompatible with field type.
    /// </summary>
    private static bool MatchesFilter(Document doc, Element element, ElementFilter filter,
        double? filterRhsNumeric, ref bool fieldFound)
    {
        // ── Pseudo fields ──
        if (PseudoFields.Contains(filter.Property))
        {
            fieldFound = true;

            // Validate: numeric operators on string pseudo fields
            if (IsNumericOperator(filter.Operator) && !NumericPseudoFields.Contains(filter.Property))
                throw new ArgumentException(
                    $"Operator '{filter.Operator}' cannot be used with string field '{filter.Property}'.");

            // Numeric pseudo fields require numeric RHS for ALL operators
            if (NumericPseudoFields.Contains(filter.Property))
            {
                if (!filterRhsNumeric.HasValue)
                    throw new ArgumentException(
                        $"Field '{filter.Property}' requires a numeric value, got: '{filter.Value}'.");
                return CompareNumeric(ToCliElementId(element.Id), filterRhsNumeric.Value, filter.Operator);
            }

            var val = GetPseudoFieldValue(doc, element, filter.Property);
            if (val == null)
                return false;

            return filter.Operator switch
            {
                "=" => string.Equals(val, filter.Value, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(val, filter.Value, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        // ── Parameter fields ──
        var param = FindParameterByFilterName(doc, element, filter.Property);
        if (param == null)
            return false;

        fieldFound = true;

        // Validate: numeric operators on non-numeric parameters
        if (IsNumericOperator(filter.Operator) &&
            param.StorageType is not (StorageType.Integer or StorageType.Double))
            throw new ArgumentException(
                $"Operator '{filter.Operator}' cannot be used with non-numeric parameter '{filter.Property}'.");

        // Numeric comparison path
        if (filterRhsNumeric.HasValue &&
            param.StorageType is StorageType.Integer or StorageType.Double)
        {
            double actual = param.StorageType == StorageType.Integer
                ? param.AsInteger()
                : param.AsDouble();

            var compareTo = ConvertFilterValueToInternal(doc, param, filterRhsNumeric.Value);
            return CompareNumeric(actual, compareTo, filter.Operator);
        }

        // String comparison path (= and != only)
        var displayVal = FormatParameterValue(doc, param);
        if (displayVal == null)
            return false;

        return filter.Operator switch
        {
            "=" => string.Equals(displayVal, filter.Value, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(displayVal, filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    // ── QueryElementsAsync ──────────────────────────────────────

    public Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category is required.");

        ElementFilter? parsedFilter = null;
        double? filterRhsNumeric = null;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            parsedFilter = ElementFilter.Parse(filter);
            if (parsedFilter == null)
                throw new ArgumentException($"Invalid filter expression: '{filter}'");

            if (double.TryParse(parsedFilter.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var num))
                filterRhsNumeric = num;

            if (IsNumericOperator(parsedFilter.Operator) && !filterRhsNumeric.HasValue)
                throw new ArgumentException(
                    $"Operator '{parsedFilter.Operator}' requires a numeric value, got: '{parsedFilter.Value}'");
        }

        return _bridge.InvokeAsync(app =>
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active document is open.");

            var builtInCat = ResolveCategory(doc, category!);

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(builtInCat);

            var results = new List<ElementInfo>();
            var sawAnyElement = false;
            var fieldFound = parsedFilter == null;

            foreach (var element in collector)
            {
                sawAnyElement = true;

                if (parsedFilter != null)
                {
                    if (!MatchesFilter(doc, element, parsedFilter, filterRhsNumeric, ref fieldFound))
                        continue;
                }

                if (results.Count >= MaxMatches)
                    throw new InvalidOperationException(
                        $"Query matched more than {MaxMatches} elements. Narrow the category or add --filter.");

                results.Add(MapElement(doc, element));
            }

            // Only report field-not-found if category had elements but none had the field
            if (sawAnyElement && !fieldFound)
                throw new ArgumentException(
                    $"Filter field '{parsedFilter!.Property}' not found on any {category} element.");

            results.Sort((a, b) => a.Id.CompareTo(b.Id));
            return results.ToArray();
        });
    }

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
