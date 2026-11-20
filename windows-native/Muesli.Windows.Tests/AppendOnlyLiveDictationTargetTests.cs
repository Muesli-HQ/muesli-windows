using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class AppendOnlyLiveDictationTargetTests
{
    [Fact]
    public async Task TypesOnlyCommittedGrowthAndStopsOnRevision()
    {
        var sent = new List<string>();
        var foreground = true;
        var suspended = 0;
        var target = new AppendOnlyLiveDictationTarget(() => foreground, text => { sent.Add(text); return true; })
        {
            OnSuspended = () => suspended++
        };

        Assert.True(await target.UpdateAsync("hello ", "wor"));
        Assert.True(await target.UpdateAsync("hello world ", ""));
        Assert.Equal(["hello ", "world "], sent);

        Assert.False(await target.UpdateAsync("hello there ", ""));
        Assert.True(target.IsSuspended);
        Assert.False(await target.UpdateAsync("hello world again ", ""));
        Assert.Equal(1, suspended);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task FinishAppendsRemainderOnlyWhenPrefixMatchesAndForegroundHolds()
    {
        var sent = new List<string>();
        var foreground = true;
        var target = new AppendOnlyLiveDictationTarget(() => foreground, text => { sent.Add(text); return true; });
        Assert.True(await target.UpdateAsync("one ", ""));
        Assert.True(await target.FinishAsync("one two. "));
        Assert.Equal(["one ", "two. "], sent);

        var other = new AppendOnlyLiveDictationTarget(() => foreground, text => { sent.Add(text); return true; });
        Assert.True(await other.UpdateAsync("one ", ""));
        Assert.False(await other.FinishAsync("One two. "));
        foreground = false;
        var away = new AppendOnlyLiveDictationTarget(() => foreground, _ => true);
        Assert.False(await away.UpdateAsync("hi ", ""));
        Assert.False(away.HasAttemptedWrite);
        Assert.False(await target.UpdateAsync("one two. three", ""));
        Assert.False(await new AppendOnlyLiveDictationTarget(() => true, _ => true).UpdateAsync("a\r", ""));
    }
}
