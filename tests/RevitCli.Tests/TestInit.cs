using System.Runtime.CompilerServices;
using System.Text;

namespace RevitCli.Tests;

internal static class TestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
