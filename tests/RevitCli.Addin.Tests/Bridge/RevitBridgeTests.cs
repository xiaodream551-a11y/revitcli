using RevitCli.Addin.Bridge;

namespace RevitCli.Addin.Tests.Bridge;

public class RevitBridgeTests
{
    [Fact]
    public async Task InvokeOnMainThreadAsync_ReturnsResult()
    {
        var bridge = new RevitBridge();
        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult("hello");
        });
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task InvokeOnMainThreadAsync_HandlesException()
    {
        var bridge = new RevitBridge();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await bridge.InvokeOnMainThreadAsync(_ =>
            {
                throw new InvalidOperationException("test error");
            });
        });
    }

    [Fact]
    public async Task InvokeOnMainThreadAsync_HandlesNull()
    {
        var bridge = new RevitBridge();
        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult(null);
        });
        Assert.Null(result);
    }

    [Fact]
    public async Task InvokeOnMainThreadAsync_MultipleCallsProcessInOrder()
    {
        var bridge = new RevitBridge();
        var results = new List<object?>();

        var t1 = bridge.InvokeOnMainThreadAsync(set => set(1));
        var t2 = bridge.InvokeOnMainThreadAsync(set => set(2));
        var t3 = bridge.InvokeOnMainThreadAsync(set => set(3));

        results.Add(await t1);
        results.Add(await t2);
        results.Add(await t3);

        Assert.Equal(new object[] { 1, 2, 3 }, results);
    }
}
