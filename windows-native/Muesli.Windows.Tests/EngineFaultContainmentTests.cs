using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-19 (macOS 09f318ea, 25d7148e): native engine teardown faults are contained, not propagated.</summary>
public sealed class EngineFaultContainmentTests
{
    [Fact]
    public async Task AThrowingEngineCallIsReturnedNotThrown()
    {
        var fault = new System.Runtime.InteropServices.COMException("device removed");
        var contained = await AudioCaptureService.ContainEngineCallAsync(() => throw fault);
        Assert.Same(fault, contained);
    }

    [Fact]
    public async Task AFaultingAsyncEngineCallIsContained()
    {
        var contained = await AudioCaptureService.ContainEngineCallAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("stop failed");
        });
        Assert.IsType<InvalidOperationException>(contained);
    }

    [Fact]
    public async Task ASuccessfulEngineCallReportsNoFault()
    {
        var ran = false;
        Assert.Null(await AudioCaptureService.ContainEngineCallAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.True(ran);
    }
}
