using System;
using System.Threading.Tasks;
using RevitCli.Addin.Bridge;
using Xunit;

namespace RevitCli.Addin.Tests.Bridge;

internal sealed class FakeExternalEvent : IExternalEventSource
{
    public bool RaiseResult { get; set; } = true;
    public bool RaiseThrows { get; set; }
    public int RaiseCount { get; private set; }

    public bool Raise()
    {
        RaiseCount++;
        if (RaiseThrows)
            throw new InvalidOperationException("Raise failed");
        return RaiseResult;
    }

    public void Dispose() { }
}

public class RevitBridgeTests
{
    private static RevitRequestQueue<object?> CreateQueue(out FakeExternalEvent fake)
    {
        fake = new FakeExternalEvent();
        return new RevitRequestQueue<object?>(fake, "TestQueue");
    }

    [Fact]
    public async Task InvokeAsync_EnqueuesRequest_AndCallsRaise()
    {
        var queue = CreateQueue(out var fake);

        var task = queue.InvokeAsync(app => 42);
        queue.Process(null);

        Assert.Equal(42, await task);
        Assert.Equal(1, fake.RaiseCount);
    }

    [Fact]
    public async Task Execute_CompletesQueuedRequest_WithResult()
    {
        var queue = CreateQueue(out _);

        var task = queue.InvokeAsync(app => "hello");
        Assert.False(task.IsCompleted);

        queue.Process(null);

        Assert.Equal("hello", await task);
    }

    [Fact]
    public async Task Execute_PropagatesWorkException_ToReturnedTask()
    {
        var queue = CreateQueue(out _);

        var task = queue.InvokeAsync<string>(app =>
            throw new InvalidOperationException("test error"));
        queue.Process(null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("test error", ex.Message);
    }

    [Fact]
    public async Task Dispose_FailsPendingRequests_WithObjectDisposedException()
    {
        var queue = CreateQueue(out _);

        var task = queue.InvokeAsync(app => "never");
        queue.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    [Fact]
    public async Task MultipleRequests_AreProcessedInQueueOrder()
    {
        var queue = CreateQueue(out _);

        var t1 = queue.InvokeAsync(app => 1);
        var t2 = queue.InvokeAsync(app => 2);
        var t3 = queue.InvokeAsync(app => 3);
        queue.Process(null);

        Assert.Equal(1, await t1);
        Assert.Equal(2, await t2);
        Assert.Equal(3, await t3);
    }

    [Fact]
    public async Task RaiseRejected_FaultsReturnedTask_AndRemovesFromQueue()
    {
        var fake = new FakeExternalEvent { RaiseResult = false };
        var queue = new RevitRequestQueue<object?>(fake, "TestQueue");

        var task = queue.InvokeAsync(app => "rejected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task RaiseThrows_ReturnsFaultedTask_AndRemovesFromQueue()
    {
        var fake = new FakeExternalEvent { RaiseThrows = true };
        var queue = new RevitRequestQueue<object?>(fake, "TestQueue");

        var task = queue.InvokeAsync(app => "boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("Raise failed", ex.Message);
    }

    [Fact]
    public async Task InvokeAfterDispose_ReturnsFaultedTask()
    {
        var queue = CreateQueue(out _);
        queue.Dispose();

        var task = queue.InvokeAsync(app => 0);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    [Fact]
    public async Task Execute_WithNullApp_FailsAllRequests()
    {
        var queue = CreateQueue(out _);

        var t1 = queue.InvokeAsync(app => "first");
        var t2 = queue.InvokeAsync(app => "second");

        queue.FailAll(new InvalidOperationException("UIApplication is null."));

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => t1);
        Assert.Contains("UIApplication is null", ex1.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => t2);
        Assert.Contains("UIApplication is null", ex2.Message);
    }

    [Fact]
    public async Task InvokeAsync_NullWork_ReturnsFaultedTask()
    {
        var queue = CreateQueue(out var fake);

        var task = queue.InvokeAsync<string>(null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() => task);
        Assert.Equal(0, fake.RaiseCount);
    }
}
