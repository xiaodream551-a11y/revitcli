using EmbedIO.WebApi;
using RevitCli.Addin.Core;
using Xunit;

namespace RevitCli.Addin.Tests.Integration;

public sealed class CoreControllerRegistryTests
{
    [Fact]
    public void ControllerTypes_DiscoversCoreWebApiControllersAndKeepsExistingOrder()
    {
        var discovered = typeof(CoreControllerRegistry).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(WebApiController).IsAssignableFrom(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var registered = CoreControllerRegistry.ControllerTypes
            .Select(type => type.Name)
            .ToArray();

        Assert.Equal(discovered, registered.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[]
        {
            "StatusController",
            "ElementsController",
            "ExportController",
            "SetController",
            "AuditController",
            "ScheduleController",
            "ViewsController",
            "LinksController",
            "ModelMapController",
            "SnapshotController",
            "FamiliesController"
        }, registered);
    }
}
