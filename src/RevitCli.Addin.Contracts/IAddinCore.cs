using RevitCli.Shared;

namespace RevitCli.Addin.Contracts;

public interface IAddinCore : IDisposable
{
    IRevitOperations Operations { get; }
    IReadOnlyList<Type> ControllerTypes { get; }
    void Initialize(IRevitBridge bridge);
    void Shutdown();
}
