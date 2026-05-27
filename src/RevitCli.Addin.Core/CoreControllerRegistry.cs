using RevitCli.Addin.Handlers;

namespace RevitCli.Addin.Core;

public static class CoreControllerRegistry
{
    public static IReadOnlyList<Type> ControllerTypes { get; } = new[]
    {
        typeof(StatusController),
        typeof(ElementsController),
        typeof(ExportController),
        typeof(SetController),
        typeof(AuditController),
        typeof(ScheduleController),
        typeof(ViewsController),
        typeof(LinksController),
        typeof(ModelMapController),
        typeof(SnapshotController),
        typeof(FamiliesController)
    };
}
