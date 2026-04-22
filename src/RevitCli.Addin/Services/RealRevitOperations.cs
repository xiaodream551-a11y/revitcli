using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCli.Addin.Bridge;
using RevitCli.Shared;
using CliElementFilter = RevitCli.Shared.ElementFilter;

namespace RevitCli.Addin.Services;

public sealed class RealRevitOperations : IRevitOperations
{
    private readonly RevitBridge _bridge;

    public RealRevitOperations(RevitBridge bridge)
    {
        _bridge = bridge;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Shared helpers
    // ═══════════════════════════════════════════════════════════════

    private static Document RequireActiveDocument(UIApplication app)
    {
        return app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No active document is open.");
    }

    private static long ToCliElementId(ElementId elementId) =>
        elementId.Value;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    private const int MaxMatches = 200;

    // ── Element mapping ─────────────────────────────────────────

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
        var valueString = parameter.AsValueString();
        if (!string.IsNullOrEmpty(valueString))
            return valueString;

        return parameter.StorageType switch
        {
            StorageType.String => NullIfEmpty(parameter.AsString()),
            StorageType.Integer => parameter.AsInteger().ToString(),
            StorageType.Double => null,
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

    // ── Category resolution ─────────────────────────────────────

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

        if (CategoryAliases.TryGetValue(key, out var resolved))
            return resolved;

        foreach (Category cat in doc.Settings.Categories)
        {
            if (cat.Id.Value < 0 && Normalize(cat.Name) == key)
                return (BuiltInCategory)cat.Id.Value;
        }

        throw new ArgumentException($"Unknown category: '{category}'");
    }

    // ── Parameter lookup (shared by query filter & set) ─────────

    /// <summary>
    /// Find a parameter by name using the same visibility/counting rules as ReadVisibleParameters.
    /// Supports "Foo [2]" duplicate suffix syntax.
    /// </summary>
    private static Parameter? FindParameterByFilterName(Document doc, Element element, string filterName)
    {
        var baseName = filterName;
        var targetIndex = 1;

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
    /// Find a parameter for writing. Supports "Foo [2]" duplicate suffix syntax.
    /// Rejects ambiguous names when duplicates exist without explicit index.
    /// Unlike FindParameterByFilterName, does NOT require HasValue (we want to SET values).
    /// </summary>
    private static Parameter? FindWritableParameter(Element element, string paramName)
    {
        var baseName = paramName;
        var targetIndex = 1;
        var explicitIndex = false;

        // Parse "Foo [N]" syntax
        var bracketStart = paramName.LastIndexOf('[');
        if (bracketStart > 0 && paramName.EndsWith("]"))
        {
            var indexStr = paramName.Substring(bracketStart + 1, paramName.Length - bracketStart - 2).Trim();
            if (int.TryParse(indexStr, out var parsed) && parsed >= 2)
            {
                baseName = paramName.Substring(0, bracketStart).TrimEnd();
                targetIndex = parsed;
                explicitIndex = true;
            }
        }

        // First pass: count all matching parameters
        var totalMatches = 0;
        foreach (var param in element.GetOrderedParameters())
        {
            if (string.Equals(param.Definition.Name, baseName, StringComparison.OrdinalIgnoreCase))
                totalMatches++;
        }

        if (totalMatches == 0)
            return null;

        // Reject ambiguous names unless caller used [N] syntax
        if (totalMatches > 1 && !explicitIndex)
            throw new ArgumentException(
                $"Parameter '{baseName}' has {totalMatches} definitions on this element. " +
                $"Use '{baseName} [N]' syntax to disambiguate.");

        // Second pass: return the Nth match
        var count = 0;
        foreach (var param in element.GetOrderedParameters())
        {
            if (!string.Equals(param.Definition.Name, baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
            if (count == targetIndex)
                return param;
        }

        return null;
    }

    // ── Value coercion for Set ──────────────────────────────────

    /// <summary>
    /// Validate that the value can be coerced to the parameter's storage type
    /// without actually modifying the parameter. Used by dry-run.
    /// </summary>
    private static void ValidateCoercion(Parameter param, string value)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                break; // Always valid
            case StorageType.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException(
                        $"Cannot convert '{value}' to integer for parameter '{param.Definition.Name}'.");
                break;
            case StorageType.Double:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException(
                        $"Cannot convert '{value}' to number for parameter '{param.Definition.Name}'.");
                break;
            case StorageType.ElementId:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException(
                        $"Cannot convert '{value}' to ElementId for parameter '{param.Definition.Name}'.");
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported storage type for parameter '{param.Definition.Name}'.");
        }
    }

    private static void SetParameterValue(Document doc, Parameter param, string value)
    {
        if (param.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{param.Definition.Name}' is read-only.");

        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(value);
                break;

            case StorageType.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                    throw new ArgumentException($"Cannot convert '{value}' to integer for parameter '{param.Definition.Name}'.");
                param.Set(intVal);
                break;

            case StorageType.Double:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
                    throw new ArgumentException($"Cannot convert '{value}' to number for parameter '{param.Definition.Name}'.");
                // Convert from display units to internal units
                try
                {
                    var specTypeId = param.Definition.GetDataType();
                    var formatOptions = doc.GetUnits().GetFormatOptions(specTypeId);
                    var unitTypeId = formatOptions.GetUnitTypeId();
                    dblVal = UnitUtils.ConvertToInternalUnits(dblVal, unitTypeId);
                }
                catch
                {
                    // If unit conversion fails, use raw value
                }
                param.Set(dblVal);
                break;

            case StorageType.ElementId:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eidVal))
                    throw new ArgumentException($"Cannot convert '{value}' to ElementId for parameter '{param.Definition.Name}'.");
                param.Set(new ElementId(eidVal));
                break;

            default:
                throw new ArgumentException($"Unsupported storage type for parameter '{param.Definition.Name}'.");
        }
    }

    // ── Filter matching ─────────────────────────────────────────

    private static readonly HashSet<string> PseudoFields = new(StringComparer.OrdinalIgnoreCase)
        { "id", "name", "category", "type", "typename" };

    private static readonly HashSet<string> NumericPseudoFields = new(StringComparer.OrdinalIgnoreCase)
        { "id" };

    private static bool IsNumericOperator(string op) =>
        op is ">" or "<" or ">=" or "<=";

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

    private static bool MatchesFilter(Document doc, Element element, CliElementFilter filter,
        double? filterRhsNumeric, ref bool fieldFound)
    {
        if (PseudoFields.Contains(filter.Property))
        {
            fieldFound = true;

            if (IsNumericOperator(filter.Operator) && !NumericPseudoFields.Contains(filter.Property))
                throw new ArgumentException(
                    $"Operator '{filter.Operator}' cannot be used with string field '{filter.Property}'.");

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

        var param = FindParameterByFilterName(doc, element, filter.Property);
        if (param == null)
            return false;

        fieldFound = true;

        if (IsNumericOperator(filter.Operator) &&
            param.StorageType is not (StorageType.Integer or StorageType.Double))
            throw new ArgumentException(
                $"Operator '{filter.Operator}' cannot be used with non-numeric parameter '{filter.Property}'.");

        if (filterRhsNumeric.HasValue &&
            param.StorageType is StorageType.Integer or StorageType.Double)
        {
            double actual = param.StorageType == StorageType.Integer
                ? param.AsInteger()
                : param.AsDouble();

            var compareTo = ConvertFilterValueToInternal(doc, param, filterRhsNumeric.Value);
            return CompareNumeric(actual, compareTo, filter.Operator);
        }

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

    // ═══════════════════════════════════════════════════════════════
    //  status
    // ═══════════════════════════════════════════════════════════════

    private static readonly string AddinVersionString =
        typeof(RealRevitOperations).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<StatusInfo> GetStatusAsync()
    {
        return _bridge.InvokeAsync(app =>
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc?.Document;
            int.TryParse(app.Application.VersionNumber, out var year);
            return new StatusInfo
            {
                RevitVersion = app.Application.VersionNumber,
                RevitYear = year,
                AddinVersion = AddinVersionString,
                DocumentName = doc?.Title,
                DocumentPath = string.IsNullOrWhiteSpace(doc?.PathName) ? null : doc.PathName,
                Capabilities = BuildCapabilities(year)
            };
        });
    }

    private static List<string> BuildCapabilities(int revitYear)
    {
        var caps = new List<string>
        {
            "status",
            "query",
            "query.filter",
            "query.id",
            "set",
            "set.dry-run",
            "audit",
            "export.dwg",
            "export.ifc"
        };

        // PDF export requires Revit 2022+
        if (revitYear >= 2022)
            caps.Add("export.pdf");

        return caps;
    }

    // ═══════════════════════════════════════════════════════════════
    //  query
    // ═══════════════════════════════════════════════════════════════

    public Task<ElementInfo?> GetElementByIdAsync(long id)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Element id must be a positive integer.");

        return _bridge.InvokeAsync<ElementInfo?>(app =>
        {
            var doc = RequireActiveDocument(app);
            var element = doc.GetElement(new ElementId(id));
            if (element == null)
                return null;

            return MapElement(doc, element);
        });
    }

    public Task<ElementInfo[]> QueryElementsAsync(string? category, string? filter)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category is required.");

        CliElementFilter? parsedFilter = null;
        double? filterRhsNumeric = null;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            parsedFilter = CliElementFilter.Parse(filter);
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
            var doc = RequireActiveDocument(app);
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

            if (sawAnyElement && !fieldFound)
                throw new ArgumentException(
                    $"Filter field '{parsedFilter!.Property}' not found on any {category} element.");

            results.Sort((a, b) => a.Id.CompareTo(b.Id));
            return results.ToArray();
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  set — parameter modification with transaction
    // ═══════════════════════════════════════════════════════════════

    public Task<SetResult> SetParametersAsync(SetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Param))
            throw new ArgumentException("Parameter name (--param) is required.");

        var hasElementIds = request.ElementIds != null && request.ElementIds.Count > 0;
        if (request.ElementId == null && string.IsNullOrWhiteSpace(request.Category) && !hasElementIds)
            throw new ArgumentException("Provide a category, --id, or --stdin to target elements.");

        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);

            // Step 1: Resolve target elements
            var targets = ResolveSetTargets(doc, request);
            if (targets.Count == 0)
                throw new InvalidOperationException("No elements matched the target criteria.");

            // Step 2: Validate ALL targets — all-or-nothing, fail on any error
            var preview = new List<SetPreviewItem>();

            foreach (var element in targets)
            {
                var param = FindWritableParameter(element, request.Param);
                if (param == null)
                    throw new ArgumentException(
                        $"Element {ToCliElementId(element.Id)} ({element.Name}): parameter '{request.Param}' not found.");

                if (param.IsReadOnly)
                    throw new InvalidOperationException(
                        $"Element {ToCliElementId(element.Id)} ({element.Name}): parameter '{request.Param}' is read-only.");

                // Validate type coercion before adding to preview (catches "abc" → int early)
                ValidateCoercion(param, request.Value);

                var oldValue = FormatParameterValue(doc, param);
                preview.Add(new SetPreviewItem
                {
                    Id = ToCliElementId(element.Id),
                    Name = element.Name ?? element.GetType().Name,
                    OldValue = oldValue,
                    NewValue = request.Value
                });
            }

            // Step 3: Dry-run — return preview without modifying
            if (request.DryRun)
            {
                return new SetResult
                {
                    Affected = preview.Count,
                    Preview = preview
                };
            }

            // Step 4: Real write — single transaction, all-or-nothing
            using var tx = new Transaction(doc, $"RevitCLI Set {request.Param}");
            tx.Start();
            try
            {
                foreach (var item in preview)
                {
                    var element = doc.GetElement(new ElementId(item.Id));
                    var param = FindWritableParameter(element, request.Param)!;
                    SetParameterValue(doc, param, request.Value);
                }

                var commitStatus = tx.Commit();
                if (commitStatus != TransactionStatus.Committed)
                    throw new InvalidOperationException(
                        $"Transaction failed with status: {commitStatus}. Changes were not saved.");
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
                throw;
            }

            return new SetResult
            {
                Affected = preview.Count,
                Preview = preview
            };
        });
    }

    private List<Element> ResolveSetTargets(Document doc, SetRequest request)
    {
        // Multiple element IDs (from --stdin pipe)
        if (request.ElementIds != null && request.ElementIds.Count > 0)
        {
            var elementResults = new List<Element>();
            foreach (var eid in request.ElementIds)
            {
                var element = doc.GetElement(new ElementId(eid));
                if (element == null)
                    throw new ArgumentException($"Element with ID {eid} not found.");
                elementResults.Add(element);
            }
            return elementResults;
        }

        // Single element by ID
        if (request.ElementId.HasValue)
        {
            var element = doc.GetElement(new ElementId(request.ElementId.Value));
            if (element == null)
                throw new ArgumentException($"Element with ID {request.ElementId.Value} not found.");
            return new List<Element> { element };
        }

        // By category, optionally filtered
        var builtInCat = ResolveCategory(doc, request.Category!);
        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(builtInCat);

        CliElementFilter? parsedFilter = null;
        double? filterRhsNumeric = null;

        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            parsedFilter = CliElementFilter.Parse(request.Filter);
            if (parsedFilter == null)
                throw new ArgumentException($"Invalid filter expression: '{request.Filter}'");

            if (double.TryParse(parsedFilter.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var num))
                filterRhsNumeric = num;
        }

        var results = new List<Element>();
        var fieldFound = parsedFilter == null;

        foreach (var element in collector)
        {
            if (parsedFilter != null)
            {
                if (!MatchesFilter(doc, element, parsedFilter, filterRhsNumeric, ref fieldFound))
                    continue;
            }

            results.Add(element);

            if (results.Count > MaxMatches)
                throw new InvalidOperationException(
                    $"Set target matched more than {MaxMatches} elements. Add --filter to narrow down.");
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    //  audit — model quality checking
    // ═══════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, Func<Document, List<AuditIssue>>> AuditRuleRegistry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["unplaced-rooms"] = AuditUnplacedRooms,
            ["room-bounds"] = AuditRoomBounds,
            ["level-consistency"] = AuditLevelConsistency,
            ["naming"] = AuditNaming,
            ["views-not-on-sheets"] = AuditViewsNotOnSheets,
            ["imported-dwg"] = AuditImportedDwg,
            ["in-place-families"] = AuditInPlaceFamilies,
            ["duplicate-room-numbers"] = AuditDuplicateRoomNumbers,
            ["room-metadata"] = AuditRoomMetadata,
            ["sheets-missing-info"] = AuditSheetsMissingInfo,
        };

    public Task<AuditResult> RunAuditAsync(AuditRequest request)
    {
        var rulesToRun = request.Rules.Count > 0
            ? request.Rules
            : AuditRuleRegistry.Keys.ToList();

        var unknownRules = rulesToRun.Where(r => !AuditRuleRegistry.ContainsKey(r)).ToList();
        if (unknownRules.Count > 0)
            throw new ArgumentException($"Unknown audit rule(s): {string.Join(", ", unknownRules)}");

        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);

            var allIssues = new List<AuditIssue>();
            var passed = 0;
            var failed = 0;

            foreach (var ruleName in rulesToRun)
            {
                var ruleFunc = AuditRuleRegistry[ruleName];
                var issues = ruleFunc(doc);

                if (issues.Count == 0)
                    passed++;
                else
                    failed++;

                allIssues.AddRange(issues);
            }

            // Run required-parameter checks (server-side, batch)
            foreach (var spec in request.RequiredParameters)
            {
                var issues = AuditRequiredParameter(doc, spec);
                if (issues.Count == 0)
                    passed++;
                else
                    failed++;
                allIssues.AddRange(issues);
            }

            // Run custom naming pattern checks
            foreach (var spec in request.NamingPatterns)
            {
                var issues = AuditNamingPattern(doc, spec);
                if (issues.Count == 0)
                    passed++;
                else
                    failed++;
                allIssues.AddRange(issues);
            }

            return new AuditResult
            {
                Passed = passed,
                Failed = failed,
                Issues = allIssues
            };
        });
    }

    // ── Audit: required-parameter (server-side batch) ───────────

    private static List<AuditIssue> AuditRequiredParameter(Document doc, RequiredParameterSpec spec)
    {
        var issues = new List<AuditIssue>();

        BuiltInCategory builtInCat;
        try { builtInCat = ResolveCategory(doc, spec.Category); }
        catch { return new List<AuditIssue> { new() {
            Rule = "required-parameter", Severity = "error",
            Message = $"Unknown category: '{spec.Category}'"
        }}; }

        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(builtInCat);

        var count = 0;
        foreach (var element in collector)
        {
            // Use case-insensitive ordered scan instead of ambiguous LookupParameter
            Parameter? param = null;
            var matchCount = 0;
            foreach (var p in element.GetOrderedParameters())
            {
                if (string.Equals(p.Definition.Name, spec.Parameter, StringComparison.OrdinalIgnoreCase))
                {
                    param ??= p;
                    matchCount++;
                }
            }
            var missing = param == null;

            if (!missing && spec.RequireNonEmpty)
            {
                missing = param!.StorageType == StorageType.String
                    ? string.IsNullOrWhiteSpace(param.AsString())
                    : !param.HasValue;
            }

            if (missing)
            {
                count++;
                if (count <= 20)
                {
                    issues.Add(new AuditIssue
                    {
                        Rule = "required-parameter",
                        Severity = spec.Severity,
                        Message = $"{spec.Category} '{element.Name}' is missing required parameter '{spec.Parameter}'.",
                        ElementId = ToCliElementId(element.Id)
                    });
                }
            }
        }

        if (count > 20)
        {
            issues.Add(new AuditIssue
            {
                Rule = "required-parameter",
                Severity = "info",
                Message = $"... and {count - 20} more {spec.Category} elements missing '{spec.Parameter}'."
            });
        }

        return issues;
    }

    // ── Audit: custom naming pattern ────────────────────────────

    private static List<AuditIssue> AuditNamingPattern(Document doc, NamingPatternSpec spec)
    {
        var issues = new List<AuditIssue>();
        var regex = new Regex(spec.Pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        var target = spec.Target.ToLowerInvariant();

        IEnumerable<Element> elements;
        if (target is "views" or "view")
        {
            elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Cast<Element>();
        }
        else if (target is "sheets" or "sheet")
        {
            elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfClass(typeof(ViewSheet))
                .Cast<Element>();
        }
        else
        {
            // Try as category
            try
            {
                var cat = ResolveCategory(doc, spec.Target);
                elements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(cat);
            }
            catch
            {
                return new List<AuditIssue> { new() {
                    Rule = "naming-pattern", Severity = "error",
                    Message = $"Unknown naming target: '{spec.Target}'"
                }};
            }
        }

        var count = 0;
        foreach (var element in elements)
        {
            bool matched;
            try
            {
                matched = regex.IsMatch(element.Name);
            }
            catch (RegexMatchTimeoutException)
            {
                issues.Add(new AuditIssue
                {
                    Rule = "naming-pattern",
                    Severity = "warning",
                    Message = $"Regex pattern '{spec.Pattern}' timed out on '{element.Name}'. Skipping remaining checks.",
                    ElementId = ToCliElementId(element.Id)
                });
                break;
            }

            if (!matched)
            {
                count++;
                if (count <= 50)
                {
                    issues.Add(new AuditIssue
                    {
                        Rule = "naming-pattern",
                        Severity = spec.Severity,
                        Message = $"{spec.Target} '{element.Name}' does not match pattern '{spec.Pattern}'.",
                        ElementId = ToCliElementId(element.Id)
                    });
                }
            }
        }

        if (count > 50)
        {
            issues.Add(new AuditIssue
            {
                Rule = "naming-pattern",
                Severity = "info",
                Message = $"... and {count - 50} more {spec.Target} elements violating naming pattern."
            });
        }

        return issues;
    }

    // ── Audit rule: unplaced-rooms ──────────────────────────────

    private static List<AuditIssue> AuditUnplacedRooms(Document doc)
    {
        var issues = new List<AuditIssue>();
        var rooms = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Rooms);

        foreach (var element in rooms)
        {
            if (element.Location == null)
            {
                issues.Add(new AuditIssue
                {
                    Rule = "unplaced-rooms",
                    Severity = "warning",
                    Message = $"Room '{element.Name}' is not placed in the model.",
                    ElementId = ToCliElementId(element.Id)
                });
            }
        }

        return issues;
    }

    // ── Audit rule: room-bounds ─────────────────────────────────

    private static List<AuditIssue> AuditRoomBounds(Document doc)
    {
        var issues = new List<AuditIssue>();
        var rooms = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Rooms);

        foreach (var element in rooms)
        {
            if (element.Location == null)
                continue; // Unplaced rooms handled by unplaced-rooms rule

            // Check Area parameter — 0 area means unbounded or redundant
            var areaParam = element.get_Parameter(BuiltInParameter.ROOM_AREA);
            if (areaParam != null && areaParam.AsDouble() <= 0)
            {
                issues.Add(new AuditIssue
                {
                    Rule = "room-bounds",
                    Severity = "error",
                    Message = $"Room '{element.Name}' has zero area — likely not enclosed or redundant.",
                    ElementId = ToCliElementId(element.Id)
                });
            }
        }

        return issues;
    }

    // ── Audit rule: level-consistency ───────────────────────────

    private static List<AuditIssue> AuditLevelConsistency(Document doc)
    {
        var issues = new List<AuditIssue>();
        var levels = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Levels)
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        if (levels.Count < 2)
            return issues;

        // Check for duplicate elevations
        for (var i = 1; i < levels.Count; i++)
        {
            var prev = levels[i - 1];
            var curr = levels[i];
            var gap = Math.Abs(curr.Elevation - prev.Elevation);

            if (gap < 0.001) // Essentially same elevation
            {
                issues.Add(new AuditIssue
                {
                    Rule = "level-consistency",
                    Severity = "warning",
                    Message = $"Level '{curr.Name}' has same elevation as '{prev.Name}' ({curr.Elevation:F2} ft).",
                    ElementId = ToCliElementId(curr.Id)
                });
            }
        }

        return issues;
    }

    // ── Audit rule: naming ──────────────────────────────────────

    // Known Revit default view name prefixes (English + Chinese + other locales)
    // These are system-generated and should be flagged as default names.
    private static readonly string[] DefaultViewPrefixes =
    {
        // English
        "Floor Plan", "Ceiling Plan", "Structural Plan",
        "Section", "Callout", "Detail", "Elevation",
        "3D View", "Drafting View", "Legend",
        "{3D}", "Copy of",
        // Chinese
        "楼层平面", "天花板平面", "结构平面",
        "剖面", "详图索引", "详图", "立面",
        "三维视图", "起草视图", "图例",
    };

    // System-generated names that are NOT user-fixable (level/grid names used as view names)
    private static readonly HashSet<string> SystemNamePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Level names commonly used as plan view names — these are fine
        "标高", "Level", "Ebene", "Niveau",
        // Grid-based names
        "Grid", "轴网",
    };

    private static bool IsDefaultViewName(string name)
    {
        // Skip system-generated names (level plans, etc.) — these are normal
        foreach (var sys in SystemNamePrefixes)
        {
            if (name.StartsWith(sys, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Check against known default prefixes
        foreach (var prefix in DefaultViewPrefixes)
        {
            // Match "Section 1", "Elevation 2", "3D View 5", etc.
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = name.Substring(prefix.Length).TrimStart();
                // Exact prefix match (e.g. "{3D}") or prefix + number
                if (remainder.Length == 0 || int.TryParse(remainder, out _))
                    return true;
                // "Copy of X" pattern
                if (prefix == "Copy of")
                    return true;
            }
        }

        return false;
    }

    private static List<AuditIssue> AuditNaming(Document doc)
    {
        var issues = new List<AuditIssue>();

        var views = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate);

        foreach (var view in views)
        {
            if (IsDefaultViewName(view.Name))
            {
                issues.Add(new AuditIssue
                {
                    Rule = "naming",
                    Severity = "info",
                    Message = $"View '{view.Name}' appears to use a default name.",
                    ElementId = ToCliElementId(view.Id)
                });

                if (issues.Count >= 50)
                    break;
            }
        }

        return issues;
    }

    // ── Audit rule: duplicate-room-numbers ─────────────────────

    private static List<AuditIssue> AuditDuplicateRoomNumbers(Document doc)
    {
        var issues = new List<AuditIssue>();
        var rooms = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Rooms)
            .Where(r => r.Location != null); // only placed rooms

        var numberGroups = new Dictionary<string, List<(string Name, long Id)>>();

        foreach (var room in rooms)
        {
            var numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
            var number = numberParam?.AsString();
            if (string.IsNullOrWhiteSpace(number))
                continue;

            if (!numberGroups.TryGetValue(number, out var group))
            {
                group = new List<(string, long)>();
                numberGroups[number] = group;
            }
            group.Add((room.Name, ToCliElementId(room.Id)));
        }

        foreach (var (number, group) in numberGroups)
        {
            if (group.Count > 1)
            {
                foreach (var (name, id) in group)
                {
                    issues.Add(new AuditIssue
                    {
                        Rule = "duplicate-room-numbers",
                        Severity = "error",
                        Message = $"Room number '{number}' is used by {group.Count} rooms ('{name}').",
                        ElementId = id
                    });
                }

                if (issues.Count >= 50)
                    break;
            }
        }

        return issues;
    }

    // ── Audit rule: room-metadata ───────────────────────────────

    private static List<AuditIssue> AuditRoomMetadata(Document doc)
    {
        var issues = new List<AuditIssue>();
        var rooms = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Rooms)
            .Where(r => r.Location != null); // only placed rooms

        foreach (var room in rooms)
        {
            // Check room number
            var numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
            if (numberParam == null || string.IsNullOrWhiteSpace(numberParam.AsString()))
            {
                issues.Add(new AuditIssue
                {
                    Rule = "room-metadata",
                    Severity = "warning",
                    Message = $"Room '{room.Name}' has no room number.",
                    ElementId = ToCliElementId(room.Id)
                });
            }

            // Check room name is not default
            if (string.IsNullOrWhiteSpace(room.Name) || room.Name == "Room")
            {
                issues.Add(new AuditIssue
                {
                    Rule = "room-metadata",
                    Severity = "warning",
                    Message = $"Room (ID {ToCliElementId(room.Id)}) has default or empty name.",
                    ElementId = ToCliElementId(room.Id)
                });
            }

            if (issues.Count >= 50)
                break;
        }

        return issues;
    }

    // ── Audit rule: sheets-missing-info ─────────────────────────

    private static List<AuditIssue> AuditSheetsMissingInfo(Document doc)
    {
        var issues = new List<AuditIssue>();
        var sheets = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>();

        foreach (var sheet in sheets)
        {
            // Check sheet number
            if (string.IsNullOrWhiteSpace(sheet.SheetNumber))
            {
                issues.Add(new AuditIssue
                {
                    Rule = "sheets-missing-info",
                    Severity = "error",
                    Message = $"Sheet '{sheet.Name}' has no sheet number.",
                    ElementId = ToCliElementId(sheet.Id)
                });
            }

            // Check sheet has viewports or schedule instances (not empty)
            var viewports = sheet.GetAllViewports();
            var hasViewports = viewports != null && viewports.Count > 0;
            var hasSchedules = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Any();
            if (!hasViewports && !hasSchedules)
            {
                issues.Add(new AuditIssue
                {
                    Rule = "sheets-missing-info",
                    Severity = "warning",
                    Message = $"Sheet '{sheet.SheetNumber} - {sheet.Name}' has no views or schedules placed on it.",
                    ElementId = ToCliElementId(sheet.Id)
                });
            }

            if (issues.Count >= 50)
                break;
        }

        return issues;
    }

    // ── Audit rule: views-not-on-sheets ────────────────────────

    private static List<AuditIssue> AuditViewsNotOnSheets(Document doc)
    {
        var issues = new List<AuditIssue>();

        // Collect all placed view IDs (viewports + schedule instances)
        var placedViewIds = new HashSet<ElementId>();

        var viewports = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(Viewport));
        foreach (Viewport vp in viewports)
            placedViewIds.Add(vp.ViewId);

        // Schedules are placed via ScheduleSheetInstance, not Viewport
        var scheduleInstances = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(ScheduleSheetInstance));
        foreach (ScheduleSheetInstance ssi in scheduleInstances)
            placedViewIds.Add(ssi.ScheduleId);

        // Check all non-template, non-sheet, printable views
        var views = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && v.CanBePrinted);

        foreach (var view in views)
        {
            if (!placedViewIds.Contains(view.Id))
            {
                issues.Add(new AuditIssue
                {
                    Rule = "views-not-on-sheets",
                    Severity = "warning",
                    Message = $"View '{view.Name}' ({view.ViewType}) is not placed on any sheet.",
                    ElementId = ToCliElementId(view.Id)
                });

                if (issues.Count >= 50)
                    break;
            }
        }

        return issues;
    }

    // ── Audit rule: imported-dwg ────────────────────────────────

    private static List<AuditIssue> AuditImportedDwg(Document doc)
    {
        var issues = new List<AuditIssue>();

        var importInstances = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(ImportInstance));

        foreach (ImportInstance import in importInstances)
        {
            // Linked DWGs are also ImportInstance but with IsLinked = true
            if (!import.IsLinked)
            {
                issues.Add(new AuditIssue
                {
                    Rule = "imported-dwg",
                    Severity = "warning",
                    Message = $"Imported (not linked) CAD file detected: '{import.Name}'. Consider using Link CAD instead.",
                    ElementId = ToCliElementId(import.Id)
                });

                if (issues.Count >= 30)
                    break;
            }
        }

        return issues;
    }

    // ── Audit rule: in-place-families ────────────────────────────

    private static List<AuditIssue> AuditInPlaceFamilies(Document doc)
    {
        var issues = new List<AuditIssue>();

        var familyInstances = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        var seenFamilies = new HashSet<string>();

        foreach (var fi in familyInstances)
        {
            var family = fi.Symbol?.Family;
            if (family != null && family.IsInPlace)
            {
                var familyName = family.Name;
                if (seenFamilies.Add(familyName))
                {
                    issues.Add(new AuditIssue
                    {
                        Rule = "in-place-families",
                        Severity = "warning",
                        Message = $"In-place family '{familyName}' found. Consider converting to a loadable family.",
                        ElementId = ToCliElementId(fi.Id)
                    });

                    if (issues.Count >= 30)
                        break;
                }
            }
        }

        return issues;
    }

    // ═══════════════════════════════════════════════════════════════
    //  export — synchronous view/sheet export
    // ═══════════════════════════════════════════════════════════════

    public Task<ExportProgress> ExportAsync(ExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Format))
            throw new ArgumentException("Export format is required.");

        if (string.IsNullOrWhiteSpace(request.OutputDir))
            throw new ArgumentException("Output directory is required.");

        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);
            var format = request.Format.ToLowerInvariant();
            var taskId = Guid.NewGuid().ToString("N")[..8];

            // Ensure output directory exists
            if (!Directory.Exists(request.OutputDir))
                Directory.CreateDirectory(request.OutputDir);

            // IFC exports entire model — no view/sheet targets needed
            if (format == "ifc")
            {
                ExportIfc(doc, request.OutputDir);
                return new ExportProgress
                {
                    TaskId = taskId,
                    Status = "completed",
                    Progress = 100,
                    Message = $"Exported model (IFC) to {request.OutputDir}"
                };
            }

            // DWG/PDF require view/sheet targets
            var targets = ResolveExportTargets(doc, request);
            if (targets.Count == 0)
                throw new InvalidOperationException("No matching views or sheets found.");

            switch (format)
            {
                case "dwg":
                    ExportDwg(doc, targets, request.OutputDir);
                    break;
                case "pdf":
                    ExportPdf(doc, targets, request.OutputDir);
                    break;
                default:
                    throw new ArgumentException($"Unsupported export format: '{request.Format}'");
            }

            return new ExportProgress
            {
                TaskId = taskId,
                Status = "completed",
                Progress = 100,
                Message = $"Exported {targets.Count} view(s) to {request.OutputDir}"
            };
        });
    }

    public Task<ExportProgress> GetExportProgressAsync(string taskId)
    {
        // v1 is synchronous — export is always completed when this is called
        return Task.FromResult(new ExportProgress
        {
            TaskId = taskId,
            Status = "completed",
            Progress = 100
        });
    }

    private static List<View> ResolveExportTargets(Document doc, ExportRequest request)
    {
        var allViews = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate && v.CanBePrinted)
            .ToList();

        var sheetPatterns = request.Sheets ?? new List<string>();
        var viewPatterns = request.Views ?? new List<string>();

        // If no targets specified, export sheets only (not views)
        if (sheetPatterns.Count == 0 && viewPatterns.Count == 0)
        {
            // Check if any sheets exist first
            var hasSheets = allViews.Any(v => v is ViewSheet);
            if (hasSheets)
                sheetPatterns = new List<string> { "all" };
            else
                throw new InvalidOperationException(
                    "No sheets found in document. Use --views to export specific views, or --sheets to specify sheet patterns.");
        }

        var matched = new HashSet<ElementId>();
        var results = new List<View>();

        // Match sheets
        foreach (var pattern in sheetPatterns)
        {
            var isAll = string.Equals(pattern, "all", StringComparison.OrdinalIgnoreCase);
            foreach (var view in allViews)
            {
                if (view is not ViewSheet sheet)
                    continue;
                if (!isAll && !MatchesPattern(sheet.SheetNumber + " - " + sheet.Name, pattern)
                           && !MatchesPattern(sheet.SheetNumber, pattern)
                           && !MatchesPattern(sheet.Name, pattern))
                    continue;

                if (matched.Add(view.Id))
                    results.Add(view);
            }
        }

        // Match views (non-sheet)
        foreach (var pattern in viewPatterns)
        {
            var isAll = string.Equals(pattern, "all", StringComparison.OrdinalIgnoreCase);
            foreach (var view in allViews)
            {
                if (view is ViewSheet)
                    continue; // Sheets handled above
                if (!isAll && !MatchesPattern(view.Name, pattern))
                    continue;

                if (matched.Add(view.Id))
                    results.Add(view);
            }
        }

        return results;
    }

    /// <summary>
    /// Simple wildcard matching: "A1*" matches "A101 - Floor Plan".
    /// Supports * as wildcard only.
    /// </summary>
    private static bool MatchesPattern(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        if (!pattern.Contains('*'))
            return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);

        // Convert simple wildcard to regex
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
    }

    // ── DWG export ──────────────────────────────────────────────

    private static void ExportDwg(Document doc, List<View> views, string outputDir)
    {
        var options = new DWGExportOptions
        {
            MergedViews = true,
            FileVersion = ACADVersion.R2018
        };

        var viewIds = views.Select(v => v.Id).ToList();
        var success = doc.Export(outputDir, "RevitCLI_Export", viewIds, options);
        if (!success)
            throw new InvalidOperationException("DWG export failed. Check that the views are valid and the output directory is writable.");
    }

    // ── PDF export ──────────────────────────────────────────────

    private static void ExportPdf(Document doc, List<View> views, string outputDir)
    {
        var viewIds = views.Select(v => v.Id).ToList();

        var pdfOptions = new PDFExportOptions
        {
            FileName = "RevitCLI_Export",
            Combine = true // Combine all views into single PDF
        };

        var success = doc.Export(outputDir, viewIds, pdfOptions);
        if (!success)
            throw new InvalidOperationException("PDF export failed. Check that the views are printable and the output directory is writable.");
    }

    // ── IFC export ──────────────────────────────────────────────

    private static void ExportIfc(Document doc, string outputDir)
    {
        // IFC exports the entire model, not individual views
        using var tx = new Transaction(doc, "RevitCLI IFC Export");
        tx.Start();
        try
        {
            var ifcOptions = new IFCExportOptions
            {
                FileVersion = IFCVersion.IFC2x3
            };

            var success = doc.Export(outputDir, "RevitCLI_Export.ifc", ifcOptions);

            var commitStatus = tx.Commit();
            if (commitStatus != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"IFC export transaction failed with status: {commitStatus}.");

            if (!success)
                throw new InvalidOperationException("IFC export failed. Check that the model is valid and the output directory is writable.");
        }
        catch
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            throw;
        }
    }
}
