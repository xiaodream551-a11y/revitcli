using RevitCli.Addin.Bridge;
using RevitCli.Shared;

namespace RevitCli.Addin.Tests.Handlers;

public class HandlerIntegrationTests
{
    [Fact]
    public async Task StatusHandler_ReturnsStatusInfo()
    {
        var bridge = new RevitBridge();

        // Simulate what StatusController does
        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult(new StatusInfo
            {
                RevitVersion = "2025",
                DocumentName = "Test.rvt"
            });
        });

        var status = (StatusInfo)result!;
        Assert.Equal("2025", status.RevitVersion);
        Assert.Equal("Test.rvt", status.DocumentName);
    }

    [Fact]
    public async Task ElementsHandler_ReturnsElementArray()
    {
        var bridge = new RevitBridge();

        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult(new[] { new ElementInfo { Id = 1, Name = "Wall 1", Category = "Walls" } });
        });

        var elements = (ElementInfo[])result!;
        Assert.Single(elements);
        Assert.Equal("Wall 1", elements[0].Name);
    }

    [Fact]
    public async Task SetHandler_ReturnsDryRunResult()
    {
        var bridge = new RevitBridge();

        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult(new SetResult
            {
                Affected = 3,
                Preview = new List<SetPreviewItem>
                {
                    new() { Id = 100, Name = "Door 1", OldValue = "30min", NewValue = "60min" }
                }
            });
        });

        var setData = (SetResult)result!;
        Assert.Equal(3, setData.Affected);
        Assert.Single(setData.Preview);
    }

    [Fact]
    public async Task ExportHandler_ReturnsProgress()
    {
        var bridge = new RevitBridge();

        var result = await bridge.InvokeOnMainThreadAsync(setResult =>
        {
            setResult(new ExportProgress
            {
                TaskId = "test-123",
                Status = "completed",
                Progress = 100
            });
        });

        var progress = (ExportProgress)result!;
        Assert.Equal("test-123", progress.TaskId);
        Assert.Equal("completed", progress.Status);
    }
}
