using System;
using System.Runtime.InteropServices;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>Priority 4: pure, testable UI exception containment policy.</summary>
public sealed class UiExceptionPolicyTests
{
    [Fact]
    public void KnownTransientFailuresAreRecoverable()
    {
        Assert.Equal(UiExceptionDisposition.Recoverable, UiExceptionClassifier.Classify(new OperationCanceledException()).Disposition);
        Assert.Equal(UiExceptionDisposition.Recoverable, UiExceptionClassifier.Classify(new TimeoutException()).Disposition);
        Assert.Equal(UiExceptionDisposition.Recoverable, UiExceptionClassifier.Classify(new UnauthorizedAccessException()).Disposition);
        Assert.Equal(UiExceptionDisposition.Recoverable,
            UiExceptionClassifier.Classify(new COMException("clipboard busy", unchecked((int)0x800401D0))).Disposition);
        Assert.Equal(UiExceptionDisposition.Recoverable,
            UiExceptionClassifier.Classify(new COMException("device", unchecked((int)0x8007048F))).Disposition);
    }

    [Fact]
    public void UnknownAndCorruptStateFailuresAreFatal()
    {
        Assert.Equal(UiExceptionDisposition.Fatal, UiExceptionClassifier.Classify(new NullReferenceException()).Disposition);
        Assert.Equal(UiExceptionDisposition.Fatal, UiExceptionClassifier.Classify(new InvalidOperationException()).Disposition);
        Assert.Equal(UiExceptionDisposition.Fatal,
            UiExceptionClassifier.Classify(new COMException("unknown", unchecked((int)0x8000FFFF))).Disposition);
        Assert.Equal(UiExceptionDisposition.Fatal, UiExceptionClassifier.Classify(null).Disposition);
    }

    [Fact]
    public void RecoverableFailureIsContainedWithoutShutdown()
    {
        var recoverable = 0;
        var fatal = 0;
        var shutdown = 0;
        var policy = new UiExceptionPolicy(_ => recoverable++, _ => fatal++, () => shutdown++);

        var disposition = policy.Handle(new TimeoutException());

        Assert.Equal(UiExceptionDisposition.Recoverable, disposition);
        Assert.Equal(1, recoverable);
        Assert.Equal(0, fatal);
        Assert.Equal(0, shutdown);
        Assert.False(policy.FatalHandled);
    }

    [Fact]
    public void FatalFailureLogsAndShutsDownExactlyOnce()
    {
        var recoverable = 0;
        var fatalMessages = new System.Collections.Generic.List<string>();
        var shutdown = 0;
        var policy = new UiExceptionPolicy(_ => recoverable++, fatalMessages.Add, () => shutdown++);

        Assert.Equal(UiExceptionDisposition.Fatal, policy.Handle(new NullReferenceException("boom")));
        Assert.Equal(UiExceptionDisposition.Fatal, policy.Handle(new InvalidOperationException("again")));

        Assert.Equal(0, recoverable);
        Assert.Single(fatalMessages);
        Assert.Equal(1, shutdown);
        Assert.True(policy.FatalHandled);
    }

    [Fact]
    public void FatalDiagnosticNeverContainsTheExceptionMessage()
    {
        const string secret = "transcript secret text";
        var fatalMessages = new System.Collections.Generic.List<string>();
        var policy = new UiExceptionPolicy(_ => { }, fatalMessages.Add, () => { });

        policy.Handle(new NullReferenceException(secret));

        var message = Assert.Single(fatalMessages);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.Contains("NullReferenceException", message, StringComparison.Ordinal);
    }
}
