using Autodesk.Revit.UI;

namespace RevitCli.Addin.Contracts;

public interface IRevitBridge
{
    Task<T> InvokeAsync<T>(Func<UIApplication, T> work);
}
