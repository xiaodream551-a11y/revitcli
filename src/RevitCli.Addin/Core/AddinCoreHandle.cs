#if !REVIT2024
using System;
using System.Runtime.Loader;
using RevitCli.Addin.Contracts;

namespace RevitCli.Addin.Core;

internal sealed class AddinCoreHandle : IDisposable
{
    private AssemblyLoadContext? _loadContext;

    public AddinCoreHandle(IAddinCore core, AssemblyLoadContext loadContext)
    {
        Core = core;
        _loadContext = loadContext;
        LoadContextReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public IAddinCore Core { get; private set; }

    public WeakReference LoadContextReference { get; }

    public void Dispose()
    {
        try { Core.Shutdown(); }
        finally
        {
            Core.Dispose();
            _loadContext?.Unload();
            _loadContext = null;
        }
    }
}
#endif
