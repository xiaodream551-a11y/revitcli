using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RevitCli.Addin.Bridge;

/// <summary>
/// Bridges HTTP server threads to the Revit main thread via ExternalEvent.
///
/// In the real Revit environment, this implements IExternalEventHandler
/// and uses ExternalEvent.Raise() to schedule work on the main thread.
///
/// For development/testing without Revit, it executes callbacks directly.
/// </summary>
public class RevitBridge
{
    private readonly ConcurrentQueue<(Action<Action<object?>> work, TaskCompletionSource<object?> tcs)> _queue = new();

    /// <summary>
    /// Schedule work to run on the Revit main thread.
    /// The action receives a callback to set the result.
    /// </summary>
    public Task<object?> InvokeOnMainThreadAsync(Action<Action<object?>> work)
    {
        var tcs = new TaskCompletionSource<object?>();
        _queue.Enqueue((work, tcs));

        // In real Revit environment: _externalEvent.Raise();
        // For development: process immediately
        ProcessQueue();

        return tcs.Task;
    }

    /// <summary>
    /// Called by ExternalEvent on the Revit main thread.
    /// In development mode, called directly by InvokeOnMainThreadAsync.
    /// </summary>
    public void ProcessQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.work(result => item.tcs.TrySetResult(result));
            }
            catch (Exception ex)
            {
                item.tcs.TrySetException(ex);
            }
        }
    }
}
