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
    private static RevitBridge CreateBridge(out FakeExternalEvent fake)
    {
        fake = new FakeExternalEvent();
        return new RevitBridge(fake);
    }

    [Fact]
    public async Task InvokeAsync_EnqueuesRequest_AndCallsRaise()
    {
        var bridge = CreateBridge(out var fake);

        var task = bridge.InvokeAsync(app => 42);
        bridge.ProcessQueueForTesting();

        Assert.Equal(42, await task);
        Assert.Equal(1, fake.RaiseCount);
    }

    [Fact]
    public async Task Execute_CompletesQueuedRequest_WithResult()
    {
        var bridge = CreateBridge(out _);

        var task = bridge.InvokeAsync(app => "hello");
        Assert.False(task.IsCompleted);

        bridge.ProcessQueueForTesting();

        Assert.Equal("hello", await task);
    }

    [Fact]
    public async Task Execute_PropagatesWorkException_ToReturnedTask()
    {
        var bridge = CreateBridge(out _);

        var task = bridge.InvokeAsync<string>(app =>
            throw new InvalidOperationException("test error"));
        bridge.ProcessQueueForTesting();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("test error", ex.Message);
    }

    [Fact]
    public async Task Dispose_FailsPendingRequests_WithObjectDisposedException()
    {
        var bridge = CreateBridge(out _);

        var task = bridge.InvokeAsync(app => "never");
        bridge.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    [Fact]
    public async Task MultipleRequests_AreProcessedInQueueOrder()
    {
        var bridge = CreateBridge(out _);

        var t1 = bridge.InvokeAsync(app => 1);
        var t2 = bridge.InvokeAsync(app => 2);
        var t3 = bridge.InvokeAsync(app => 3);
        bridge.ProcessQueueForTesting();

        Assert.Equal(1, await t1);
        Assert.Equal(2, await t2);
        Assert.Equal(3, await t3);
    }

    [Fact]
    public async Task RaiseRejected_FaultsReturnedTask_AndRemovesFromQueue()
    {
        var fake = new FakeExternalEvent { RaiseResult = false };
        var bridge = new RevitBridge(fake);

        var task = bridge.InvokeAsync(app => "rejected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task RaiseThrows_ReturnsFaultedTask_AndRemovesFromQueue()
    {
        var fake = new FakeExternalEvent { RaiseThrows = true };
        var bridge = new RevitBridge(fake);

        var task = bridge.InvokeAsync(app => "boom");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("Raise failed", ex.Message);
    }

    [Fact]
    public async Task InvokeAfterDispose_ReturnsFaultedTask()
    {
        var bridge = CreateBridge(out _);
        bridge.Dispose();

        var task = bridge.InvokeAsync(app => 0);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    [Fact]
    public async Task Execute_WithNullApp_FailsAllRequests()
    {
        var bridge = CreateBridge(out _);

        var t1 = bridge.InvokeAsync(app => "first");
        var t2 = bridge.InvokeAsync(app => "second");

        bridge.Execute(null!);

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => t1);
        Assert.Contains("UIApplication is null", ex1.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => t2);
        Assert.Contains("UIApplication is null", ex2.Message);
    }

    [Fact]
    public async Task InvokeAsync_NullWork_ReturnsFaultedTask()
    {
        var bridge = CreateBridge(out var fake);

        var task = bridge.InvokeAsync<string>(null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() => task);
        Assert.Equal(0, fake.RaiseCount);
    }
}
