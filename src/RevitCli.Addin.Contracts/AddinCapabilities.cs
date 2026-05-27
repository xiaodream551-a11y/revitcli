namespace RevitCli.Addin.Contracts;

public static class AddinCapabilities
{
    public static List<string> Build(int revitYear)
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

        if (revitYear >= 2022)
            caps.Add("export.pdf");

        caps.AddRange(new[]
        {
            "schedule",
            "schedule.list",
            "schedule.export",
            "schedule.create",
            "schedule.create.dry-run",
            "schedules",
            "schedules.ensure.dry-run",
            "schedules.batch-export",
            "views",
            "views.audit",
            "views.template-apply.dry-run",
            "views.clone-set.dry-run",
            "links",
            "links.audit",
            "links.repair",
            "links.repair.dry-run",
            "links.repair.apply",
            "model.map",
            "model.map.check",
            "model.map.fix",
            "model.map.fix.dry-run",
            "model.map.fix.apply",
            "snapshot",
            "snapshot.capture",
            "family",
            "family.list",
            "family.validate",
            "family.purge.dry-run",
            "family.purge.apply",
            "family.export.dry-run",
            "family.export.apply"
        });

        return caps;
    }
}
