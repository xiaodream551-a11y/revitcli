using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitCli.Addin.Bridge;

/// <summary>
/// Abstraction over ExternalEvent for testability.
/// </summary>
public interface IExternalEventSource : IDisposable
{
    /// <summary>
    /// Signal Revit to process queued work.
    /// Returns true if accepted or already pending; false if rejected.
    /// </summary>
    bool Raise();
}

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
    private interface IQueuedRequest
    {
        void Execute(UIApplication app);
        void Fail(Exception ex);
    }

    private sealed class QueuedRequest<T> : IQueuedRequest
    {
        private readonly Func<UIApplication, T> _work;
        private readonly TaskCompletionSource<T> _tcs;

        public QueuedRequest(Func<UIApplication, T> work)
        {
            _work = work;
            _tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<T> Task => _tcs.Task;

        public void Execute(UIApplication app)
        {
            if (_tcs.Task.IsCompleted)
                return;

            try
            {
                var result = _work(app);
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    private readonly List<IQueuedRequest> _requests = new();
    private readonly IExternalEventSource _eventSource;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Production constructor — creates a real ExternalEvent.
    /// </summary>
    public RevitBridge()
    {
        _eventSource = new RevitExternalEvent(this);
    }

    /// <summary>
    /// Test constructor — accepts a fake event source.
    /// </summary>
    internal RevitBridge(IExternalEventSource eventSource)
    {
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
    }

    /// <summary>
    /// Schedule work on the Revit main thread and await the result.
    /// Never throws synchronously — always returns a Task (possibly faulted).
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<UIApplication, T> work)
    {
        if (work == null)
            return Task.FromException<T>(new ArgumentNullException(nameof(work)));

        var request = new QueuedRequest<T>(work);

        lock (_lock)
        {
            if (_disposed)
            {
                request.Fail(new ObjectDisposedException(nameof(RevitBridge)));
                return request.Task;
            }

            _requests.Add(request);

            try
            {
                if (!_eventSource.Raise())
                {
                    _requests.Remove(request);
                    request.Fail(new InvalidOperationException(
                        "ExternalEvent.Raise() was rejected by Revit."));
                }
            }
            catch (Exception ex)
            {
                _requests.Remove(request);
                request.Fail(ex);
            }
        }

        return request.Task;
    }

    /// <summary>
    /// Called by Revit on the main thread. Drains the entire queue.
    /// Single-request failures do not interrupt processing of subsequent requests.
    /// </summary>
    public void Execute(UIApplication app)
    {
        var batch = TakeAll();
        if (batch.Length == 0) return;

        if (app == null)
        {
            var ex = new InvalidOperationException("UIApplication is null.");
            foreach (var request in batch)
                request.Fail(ex);
            return;
        }

        foreach (var request in batch)
            request.Execute(app);
    }

    /// <summary>
    /// Test-only entry point. Processes queued requests without a real UIApplication.
    /// Work functions receive null — only use when test lambdas don't access the app.
    /// </summary>
    internal void ProcessQueueForTesting()
    {
        var batch = TakeAll();
        foreach (var request in batch)
            request.Execute(null!);
    }

    public string GetName() => "RevitCli Main Thread Bridge";

    public void Dispose()
    {
        IQueuedRequest[] remaining;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            remaining = _requests.ToArray();
            _requests.Clear();
        }

        var disposedException = new ObjectDisposedException(nameof(RevitBridge));
        foreach (var request in remaining)
            request.Fail(disposedException);

        _eventSource.Dispose();
    }

    private IQueuedRequest[] TakeAll()
    {
        lock (_lock)
        {
            if (_requests.Count == 0)
                return Array.Empty<IQueuedRequest>();

            var batch = _requests.ToArray();
            _requests.Clear();
            return batch;
        }
    }
}
