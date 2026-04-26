using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

internal sealed class RevitRequestQueue<TContext> : IDisposable
{
    private interface IQueuedRequest
    {
        void Execute(TContext context);
        void Fail(Exception ex);
    }

    private sealed class QueuedRequest<TResult> : IQueuedRequest
    {
        private readonly Func<TContext, TResult> _work;
        private readonly TaskCompletionSource<TResult> _tcs;

        public QueuedRequest(Func<TContext, TResult> work)
        {
            _work = work;
            _tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<TResult> Task => _tcs.Task;

        public void Execute(TContext context)
        {
            if (_tcs.Task.IsCompleted)
                return;

            try
            {
                var result = _work(context);
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    private readonly Queue<IQueuedRequest> _requests = new();
    private readonly IExternalEventSource _eventSource;
    private readonly string _ownerName;
    private readonly object _lock = new();
    private bool _disposed;

    public RevitRequestQueue(IExternalEventSource eventSource, string ownerName)
    {
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        _ownerName = string.IsNullOrWhiteSpace(ownerName) ? nameof(RevitRequestQueue<TContext>) : ownerName;
    }

    /// <summary>
    /// Schedule work for the external event thread and await the result.
    /// Never throws synchronously -- always returns a Task, possibly faulted.
    /// </summary>
    public Task<TResult> InvokeAsync<TResult>(Func<TContext, TResult> work)
    {
        if (work == null)
            return Task.FromException<TResult>(new ArgumentNullException(nameof(work)));

        var request = new QueuedRequest<TResult>(work);

        lock (_lock)
        {
            if (_disposed)
            {
                request.Fail(new ObjectDisposedException(_ownerName));
                return request.Task;
            }

            try
            {
                if (!_eventSource.Raise())
                {
                    request.Fail(new InvalidOperationException(
                        "ExternalEvent.Raise() was rejected by Revit."));
                    return request.Task;
                }
            }
            catch (Exception ex)
            {
                request.Fail(ex);
                return request.Task;
            }

            _requests.Enqueue(request);
        }

        return request.Task;
    }

    public void Process(TContext context)
    {
        var batch = TakeAll();
        foreach (var request in batch)
            request.Execute(context);
    }

    public void FailAll(Exception ex)
    {
        if (ex == null)
            throw new ArgumentNullException(nameof(ex));

        var batch = TakeAll();
        foreach (var request in batch)
            request.Fail(ex);
    }

    public void Dispose()
    {
        IQueuedRequest[] remaining;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            remaining = new IQueuedRequest[_requests.Count];
            for (var i = 0; i < remaining.Length; i++)
                remaining[i] = _requests.Dequeue();
        }

        var disposedException = new ObjectDisposedException(_ownerName);
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

            var batch = new IQueuedRequest[_requests.Count];
            for (var i = 0; i < batch.Length; i++)
                batch[i] = _requests.Dequeue();
            return batch;
        }
    }
}
