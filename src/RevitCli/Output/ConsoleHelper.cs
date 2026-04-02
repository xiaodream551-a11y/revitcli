using System;

namespace RevitCli.Output;

public static class ConsoleHelper
{
    public static bool IsInteractive => !Console.IsOutputRedirected;
}
