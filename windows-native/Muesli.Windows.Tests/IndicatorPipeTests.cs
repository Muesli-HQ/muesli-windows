using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class IndicatorPipeTests
{
    [Fact]
    public async Task Snapshot_flows_down_and_command_flows_up()
    {
        var instanceId = Guid.NewGuid().ToString("N");

        var commandTcs = new TaskCompletionSource<IndicatorCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new IndicatorPipeServer(instanceId, command => commandTcs.TrySetResult(command));
        server.Start();

        var snapshotTcs = new TaskCompletionSource<IndicatorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new IndicatorPipeClient(instanceId, snapshot => snapshotTcs.TrySetResult(snapshot));
        client.Start();

        await WaitUntilAsync(() => server.IsConnected, TimeSpan.FromSeconds(5));

        server.Publish(new IndicatorSnapshot { SessionId = 11, State = "recording", Visible = true, Owner = IndicatorOwnerKind.Dictation, Amplitude = 0.4f });
        var received = await snapshotTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("recording", received.State);
        Assert.Equal(11, received.SessionId);
        Assert.Equal(IndicatorOwnerKind.Dictation, received.Owner);
        Assert.Equal(0.4f, received.Amplitude);

        client.SendCommand(new IndicatorCommand { SessionId = 11, Type = IndicatorCommandType.Stop });
        var command = await commandTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IndicatorCommandType.Stop, command.Type);
        Assert.Equal(11, command.SessionId);
    }

    [Fact]
    public async Task Disconnect_then_reconnect_replays_full_snapshot()
    {
        var latest = new IndicatorSnapshot { SessionId = 99, State = "transcribing", Visible = true };
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstInstance = Guid.NewGuid().ToString("N");
        using var server1 = new IndicatorPipeServer(firstInstance, _ => { });
        server1.Connected += (_, _) => server1.Publish(latest);
        server1.Disconnected += (_, _) => disconnected.TrySetResult();
        server1.Start();

        var firstTcs = new TaskCompletionSource<IndicatorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstClient = new IndicatorPipeClient(firstInstance, snapshot => firstTcs.TrySetResult(snapshot));
        firstClient.Start();
        Assert.Equal(99, (await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(5))).SessionId);

        // The companion dropping the command pipe must be observed by the owner.
        firstClient.Dispose();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The host recreates the owner for a fresh session; a new companion reconnects and
        // receives the full, current snapshot.
        var secondInstance = Guid.NewGuid().ToString("N");
        using var server2 = new IndicatorPipeServer(secondInstance, _ => { });
        server2.Connected += (_, _) => server2.Publish(latest);
        server2.Start();

        var secondTcs = new TaskCompletionSource<IndicatorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondClient = new IndicatorPipeClient(secondInstance, snapshot => secondTcs.TrySetResult(snapshot));
        secondClient.Start();
        Assert.Equal("transcribing", (await secondTcs.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }
}
