using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class SnapshotDiffer
{
    public static SnapshotDiff Diff(ModelSnapshot from, ModelSnapshot to,
                                    string? fromLabel = null, string? toLabel = null,
                                    SinceMode sinceMode = SinceMode.Meta)
    {
        if (from.SchemaVersion != to.SchemaVersion)
            throw new InvalidOperationException(
                $"Schema mismatch: from={from.SchemaVersion}, to={to.SchemaVersion}. Regenerate snapshots.");

        var result = new SnapshotDiff
        {
            SchemaVersion = from.SchemaVersion,
            From = fromLabel ?? "",
            To = toLabel ?? ""
        };

        if (!string.Equals(from.Revit.DocumentPath, to.Revit.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add(
                $"DocumentPath differs between snapshots: from='{from.Revit.DocumentPath}' to='{to.Revit.DocumentPath}'. Diff may be misleading.");
        }

        // Element-level diff by category
        var allCategories = new HashSet<string>(
            from.Categories.Keys.Concat(to.Categories.Keys), StringComparer.OrdinalIgnoreCase);
        foreach (var cat in allCategories.OrderBy(c => c, StringComparer.Ordinal))
        {
            var aItems = from.Categories.TryGetValue(cat, out var aList) ? aList : new List<SnapshotElement>();
            var bItems = to.Categories.TryGetValue(cat, out var bList) ? bList : new List<SnapshotElement>();
            result.Categories[cat] = DiffElementList(cat, aItems, bItems);
        }

        // Sheets — key on Number
        result.Sheets = DiffSheets(from.Sheets, to.Sheets, sinceMode);

        // Schedules — key on Id
        result.Schedules = DiffSchedules(from.Schedules, to.Schedules);

        // Summary
        result.Summary = BuildSummary(result);

        return result;
    }

    private static CategoryDiff DiffElementList(string categoryName,
        List<SnapshotElement> a, List<SnapshotElement> b)
    {
        // Defensively dedupe by Id (first occurrence wins). A well-formed snapshot
        // shouldn't have duplicate ElementIds in the same category, but matching the
        // sheet dedup behavior avoids a confusing ArgumentException on malformed input.
        var aById = a.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var bById = b.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var diff = new CategoryDiff();

        foreach (var id in bById.Keys.Except(aById.Keys))
        {
            var el = bById[id];
            diff.Added.Add(new AddedItem { Id = id, Key = $"{categoryName}:{el.Name}", Name = el.Name });
        }
        foreach (var id in aById.Keys.Except(bById.Keys))
        {
            var el = aById[id];
            diff.Removed.Add(new RemovedItem { Id = id, Key = $"{categoryName}:{el.Name}", Name = el.Name });
        }
        foreach (var id in aById.Keys.Intersect(bById.Keys))
        {
            var ae = aById[id];
            var be = bById[id];
            if (ae.Hash != be.Hash)
            {
                var mod = new ModifiedItem
                {
                    Id = id,
                    Key = $"{categoryName}:{be.Name}",
                    OldHash = ae.Hash, NewHash = be.Hash,
                    Changed = DiffParameters(ae.Parameters, be.Parameters)
                };
                diff.Modified.Add(mod);
            }
        }
        return diff;
    }

    private static Dictionary<string, ParamChange> DiffParameters(
        Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var changes = new Dictionary<string, ParamChange>();
        var allKeys = new HashSet<string>(a.Keys.Concat(b.Keys));
        foreach (var k in allKeys)
        {
            var va = a.TryGetValue(k, out var vaVal) ? vaVal : "";
            var vb = b.TryGetValue(k, out var vbVal) ? vbVal : "";
            if (va != vb)
                changes[k] = new ParamChange { From = va, To = vb };
        }
        return changes;
    }

    private static CategoryDiff DiffSheets(List<SnapshotSheet> a, List<SnapshotSheet> b, SinceMode sinceMode)
    {
        var aByNum = a.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var bByNum = b.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var diff = new CategoryDiff();

        foreach (var num in bByNum.Keys.Except(aByNum.Keys))
        {
            var s = bByNum[num];
            diff.Added.Add(new AddedItem { Id = s.ViewId, Key = $"sheet:{num}", Name = s.Name });
        }
        foreach (var num in aByNum.Keys.Except(bByNum.Keys))
        {
            var s = aByNum[num];
            diff.Removed.Add(new RemovedItem { Id = s.ViewId, Key = $"sheet:{num}", Name = s.Name });
        }
        foreach (var num in aByNum.Keys.Intersect(bByNum.Keys))
        {
            var sa = aByNum[num];
            var sb = bByNum[num];
            if (SheetChanged(sa, sb, sinceMode))
            {
                diff.Modified.Add(new ModifiedItem
                {
                    Id = sb.ViewId,
                    Key = $"sheet:{num}",
                    OldHash = sinceMode == SinceMode.Content ? sa.ContentHash : sa.MetaHash,
                    NewHash = sinceMode == SinceMode.Content ? sb.ContentHash : sb.MetaHash,
                    Changed = DiffParameters(sa.Parameters, sb.Parameters)
                });
            }
        }
        return diff;
    }

    private static bool SheetChanged(SnapshotSheet a, SnapshotSheet b, SinceMode sinceMode)
    {
        if (sinceMode == SinceMode.Meta) return a.MetaHash != b.MetaHash;

        // Content mode: use ContentHash if both sides populated; otherwise fall back to MetaHash.
        // A P1 baseline (empty ContentHash on both) effectively runs Meta-mode automatically.
        if (string.IsNullOrEmpty(a.ContentHash) || string.IsNullOrEmpty(b.ContentHash))
            return a.MetaHash != b.MetaHash;

        return a.ContentHash != b.ContentHash;
    }

    private static CategoryDiff DiffSchedules(List<SnapshotSchedule> a, List<SnapshotSchedule> b)
    {
        // Defensively dedupe by Id (first occurrence wins), matching sheet dedup behavior.
        var aById = a.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var bById = b.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var diff = new CategoryDiff();

        foreach (var id in bById.Keys.Except(aById.Keys))
            diff.Added.Add(new AddedItem { Id = id, Key = $"schedule:{bById[id].Name}", Name = bById[id].Name });
        foreach (var id in aById.Keys.Except(bById.Keys))
            diff.Removed.Add(new RemovedItem { Id = id, Key = $"schedule:{aById[id].Name}", Name = aById[id].Name });
        foreach (var id in aById.Keys.Intersect(bById.Keys))
        {
            var sa = aById[id];
            var sb = bById[id];
            if (sa.Hash != sb.Hash)
            {
                // Changed stays empty — schedules carry only a rollup Hash, not per-field
                // parameter detail. Deferred to a future phase if needed.
                diff.Modified.Add(new ModifiedItem
                {
                    Id = id,
                    Key = $"schedule:{sb.Name}",
                    OldHash = sa.Hash, NewHash = sb.Hash
                });
            }
        }
        return diff;
    }

    private static DiffSummary BuildSummary(SnapshotDiff d)
    {
        var s = new DiffSummary();
        foreach (var kv in d.Categories)
        {
            s.PerCategory[kv.Key] = new CategoryCount
            {
                Added = kv.Value.Added.Count,
                Removed = kv.Value.Removed.Count,
                Modified = kv.Value.Modified.Count
            };
        }
        s.Sheets = new CategoryCount
        {
            Added = d.Sheets.Added.Count,
            Removed = d.Sheets.Removed.Count,
            Modified = d.Sheets.Modified.Count
        };
        s.Schedules = new CategoryCount
        {
            Added = d.Schedules.Added.Count,
            Removed = d.Schedules.Removed.Count,
            Modified = d.Schedules.Modified.Count
        };
        return s;
    }
}
