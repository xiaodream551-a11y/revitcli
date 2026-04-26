using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitCli.Addin.Bridge;

/// <summary>
/// Production wrapper around Revit's ExternalEvent.
/// </summary>
internal sealed class RevitExternalEvent : IExternalEventSource
{
    private readonly ExternalEvent _event;

    public RevitExternalEvent(IExternalEventHandler handler)
    {
        _event = ExternalEvent.Create(handler);
    }

    public bool Raise()
    {
        var result = _event.Raise();
        return result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending;
    }

    public void Dispose() => _event.Dispose();
}

/// <summary>
/// Bridges HTTP server threads to the Revit main thread via ExternalEvent.
/// All Revit API calls must go through <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class RevitBridge : IExternalEventHandler, IDisposable
{
    private readonly RevitRequestQueue<UIApplication> _requests;

    /// <summary>
    /// Production constructor — creates a real ExternalEvent.
    /// </summary>
    public RevitBridge()
    {
        _requests = new RevitRequestQueue<UIApplication>(
            new RevitExternalEvent(this),
            nameof(RevitBridge));
    }

    /// <summary>
    /// Test constructor — accepts a fake event source.
    /// </summary>
    internal RevitBridge(IExternalEventSource eventSource)
    {
        _requests = new RevitRequestQueue<UIApplication>(
            eventSource ?? throw new ArgumentNullException(nameof(eventSource)),
            nameof(RevitBridge));
    }

    /// <summary>
    /// Schedule work on the Revit main thread and await the result.
    /// Never throws synchronously — always returns a Task (possibly faulted).
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work) => _requests.InvokeAsync(work);

    /// <summary>
    /// Called by Revit on the main thread. Drains the entire queue.
    /// Single-request failures do not interrupt processing of subsequent requests.
    /// </summary>
    public void Execute(UIApplication app)
    {
        if (app == null)
        {
            _requests.FailAll(new InvalidOperationException("UIApplication is null."));
            return;
        }

        _requests.Process(app);
    }

    public string GetName() => "RevitCli Main Thread Bridge";

    public void Dispose() => _requests.Dispose();
}
