using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitCli.Addin.Contracts;
using RevitCli.Addin.Bridge;
using RevitCli.Addin.Core;
using RevitCli.Addin.Server;
using RevitCli.Shared;

namespace RevitCli.Addin;

public sealed class RevitCliApp : IExternalApplication, IReloadService
{
    private ApiServer? _server;
    private RevitBridge? _bridge;
    private IAddinCore? _core;
    private const int DefaultPort = ServerInfo.DefaultPort;
#if !REVIT2024
    private readonly object _reloadLock = new();
    private AddinCoreLoader? _coreLoader;
    private AddinCoreHandle? _coreHandle;
    private string _revitVersion = "";
    private int _reloadPending;
    private int _isShuttingDown;
#endif

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            _bridge = new RevitBridge();
            var revitVersion = application.ControlledApplication.VersionNumber ?? "";
#if REVIT2024
            _core = new RevitCli.Addin.Core.AddinCore();
            _core.Initialize(_bridge);
            _server = new ApiServer(
                DefaultPort,
                _core.Operations,
                _core.ControllerTypes,
                revitVersion,
                addinDirectory: ResolveLoaderDirectory());
            _server.Start();
#else
            _revitVersion = revitVersion;
            _coreLoader = new AddinCoreLoader(ResolveCorePath());
            StartReloadableServer();
#endif
            return Result.Succeeded;
        }
        catch
        {
            CleanupServerAndCore();
            CleanupBridge();
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
#if !REVIT2024
        Volatile.Write(ref _isShuttingDown, 1);
#endif
        CleanupServerAndCore();
        CleanupBridge();
        return Result.Succeeded;
    }

    public Task<ReloadResponse> RequestReloadAsync()
    {
#if REVIT2024
        return Task.FromResult(new ReloadResponse
        {
            Status = "unsupported",
            Message = "Reload is only supported for Revit 2025 and 2026."
        });
#else
        if (Volatile.Read(ref _isShuttingDown) == 1)
        {
            return Task.FromResult(new ReloadResponse
            {
                Status = "shutting-down",
                Message = "Reload is not available while Revit is shutting down."
            });
        }

        if (Interlocked.Exchange(ref _reloadPending, 1) == 1)
        {
            return Task.FromResult(new ReloadResponse
            {
                Status = "already-running",
                Message = "A reload is already queued or running."
            });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (Volatile.Read(ref _isShuttingDown) == 0)
                    ReloadCore();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RevitCli] Add-in reload failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _reloadPending, 0);
            }
        });

        return Task.FromResult(new ReloadResponse
        {
            Status = "accepted",
            Message = "Reload queued."
        });
#endif
    }

#if !REVIT2024
    private void ReloadCore()
    {
        lock (_reloadLock)
        {
            StopServer();
            UnloadCore();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            StartReloadableServer();
        }
    }

    private void StartReloadableServer()
    {
        if (_bridge == null)
            throw new InvalidOperationException("Revit bridge is not initialized.");
        if (_coreLoader == null)
            throw new InvalidOperationException("Add-in core loader is not initialized.");

        _coreHandle = _coreLoader.Load(_bridge);
        _core = _coreHandle.Core;
        _server = new ApiServer(
            DefaultPort,
            _core.Operations,
            _core.ControllerTypes,
            _revitVersion,
            reloadService: this,
            addinDirectory: ResolveLoaderDirectory());
        _server.Start();
    }

    private static string ResolveCorePath()
    {
        return Path.Combine(ResolveLoaderDirectory(), "RevitCli.Addin.Core.dll");
    }
#endif

    private static string ResolveLoaderDirectory()
    {
        return Path.GetDirectoryName(typeof(RevitCliApp).Assembly.Location)
            ?? AppContext.BaseDirectory;
    }

    private void CleanupServerAndCore()
    {
#if REVIT2024
        StopServer();
        UnloadCore();
#else
        lock (_reloadLock)
        {
            StopServer();
            UnloadCore();
        }
#endif
    }

    private void StopServer()
    {
        try { _server?.Stop(); }
        catch { /* Server cleanup failure must not block bridge disposal. */ }
        finally { _server = null; }
    }

    private void UnloadCore()
    {
#if REVIT2024
        try { _core?.Shutdown(); }
        catch { /* Core cleanup failure must not crash Revit on exit. */ }
        finally
        {
            _core?.Dispose();
            _core = null;
        }
#else
        try { _coreHandle?.Dispose(); }
        catch { /* Core cleanup failure must not crash Revit on exit. */ }
        finally
        {
            _coreHandle = null;
            _core = null;
        }
#endif
    }

    private void CleanupBridge()
    {
        try { _bridge?.Dispose(); }
        catch { /* Bridge cleanup failure must not crash Revit on exit. */ }
        finally { _bridge = null; }
    }
}
