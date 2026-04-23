using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class SnapshotDiffer
{
    public static SnapshotDiff Diff(ModelSnapshot from, ModelSnapshot to,
                                    string? fromLabel = null, string? toLabel = null)
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
        result.Sheets = DiffSheets(from.Sheets, to.Sheets);

        // Schedules — key on Id
        result.Schedules = DiffSchedules(from.Schedules, to.Schedules);

        // Summary
        result.Summary = BuildSummary(result);

        return result;
    }

    private static CategoryDiff DiffElementList(string categoryName,
        List<SnapshotElement> a, List<SnapshotElement> b)
    {
        var aById = a.ToDictionary(e => e.Id);
        var bById = b.ToDictionary(e => e.Id);
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

    private static CategoryDiff DiffSheets(List<SnapshotSheet> a, List<SnapshotSheet> b)
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
            if (sa.MetaHash != sb.MetaHash)
            {
                diff.Modified.Add(new ModifiedItem
                {
                    Id = sb.ViewId,
                    Key = $"sheet:{num}",
                    OldHash = sa.MetaHash, NewHash = sb.MetaHash,
                    Changed = DiffParameters(sa.Parameters, sb.Parameters)
                });
            }
        }
        return diff;
    }

    private static CategoryDiff DiffSchedules(List<SnapshotSchedule> a, List<SnapshotSchedule> b)
    {
        var aById = a.ToDictionary(s => s.Id);
        var bById = b.ToDictionary(s => s.Id);
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
