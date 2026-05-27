#if !REVIT2024
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using RevitCli.Addin.Contracts;

namespace RevitCli.Addin.Core;

internal sealed class AddinCoreLoader
{
    private static readonly HashSet<string> HostAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RevitCli.Addin.Contracts",
        "RevitCli.Shared",
        "EmbedIO",
        "RevitAPI",
        "RevitAPIUI"
    };

    private readonly string _corePath;

    public AddinCoreLoader(string corePath)
    {
        _corePath = string.IsNullOrWhiteSpace(corePath)
            ? throw new ArgumentException("Core path is required.", nameof(corePath))
            : corePath;
    }

    public AddinCoreHandle Load(IRevitBridge bridge)
    {
        if (!File.Exists(_corePath))
            throw new FileNotFoundException("Reloadable add-in core was not found.", _corePath);

        var loadContext = new CoreLoadContext(_corePath, HostAssemblyNames);
        try
        {
            var assembly = LoadCoreAssembly(loadContext, _corePath);
            var coreType = FindCoreType(assembly);
            var core = (IAddinCore)Activator.CreateInstance(coreType)!;
            core.Initialize(bridge);
            return new AddinCoreHandle(core, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static Assembly LoadCoreAssembly(AssemblyLoadContext loadContext, string corePath)
    {
        var assemblyBytes = File.ReadAllBytes(corePath);
        var pdbPath = Path.ChangeExtension(corePath, ".pdb");
        using var assemblyStream = new MemoryStream(assemblyBytes);

        if (!File.Exists(pdbPath))
            return loadContext.LoadFromStream(assemblyStream);

        using var symbolsStream = new MemoryStream(File.ReadAllBytes(pdbPath));
        return loadContext.LoadFromStream(assemblyStream, symbolsStream);
    }

    private static Type FindCoreType(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var details = string.Join(Environment.NewLine,
                ex.LoaderExceptions.Select(loaderException => loaderException?.Message)
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            throw new InvalidOperationException(
                $"Failed to inspect add-in core types from '{assembly.FullName}'. {details}",
                ex);
        }

        var matches = types
            .Where(type => !type.IsAbstract && typeof(IAddinCore).IsAssignableFrom(type))
            .ToArray();

        if (matches.Length == 1)
            return matches[0];

        throw new InvalidOperationException(
            $"Expected exactly one concrete {nameof(IAddinCore)} implementation in '{assembly.FullName}', found {matches.Length}.");
    }

    private sealed class CoreLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly HashSet<string> _hostAssemblyNames;

        public CoreLoadContext(string corePath, HashSet<string> hostAssemblyNames)
            : base($"RevitCli.Addin.Core.{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(corePath);
            _hostAssemblyNames = hostAssemblyNames;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && _hostAssemblyNames.Contains(assemblyName.Name))
                return null;

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
#endif
