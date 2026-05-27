using RevitCli.Addin.Contracts;
using RevitCli.Addin.Services;
using RevitCli.Shared;

namespace RevitCli.Addin.Core;

public sealed class AddinCore : IAddinCore
{
    private RealRevitOperations? _operations;

    public IRevitOperations Operations =>
        _operations ?? throw new InvalidOperationException("Add-in core has not been initialized.");

    public IReadOnlyList<Type> ControllerTypes => CoreControllerRegistry.ControllerTypes;

    public void Initialize(IRevitBridge bridge)
    {
        _operations = new RealRevitOperations(bridge ?? throw new ArgumentNullException(nameof(bridge)));
    }

    public void Shutdown()
    {
        _operations = null;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
