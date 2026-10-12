using System.Runtime.InteropServices;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests
{
    public sealed class DiagnosticIncidentTests
    {
        [Fact]
        public void ReportKeepsOnlyAllowlistedFacts()
        {
            var secret = new FileNotFoundException(@"C:\Users\someone\secret meeting.wav not found: transcript text");
            var incident = DiagnosticIncident.Create(DiagnosticIncident.MeetingProcessingFailed, "meeting_stop_processing", "parakeet-v3", secret);

            Assert.Equal("FileNotFoundException", incident.Error.Domain);
            Assert.Equal("A required file was missing", incident.Error.Summary);
            Assert.Equal($"FileNotFoundException 0x{secret.HResult:X8}", incident.ErrorDisplay);
            Assert.Equal("[Diagnostic] Meeting processing failed", incident.IssueTitle);
            Assert.DoesNotContain("secret", incident.IssueBody);
            Assert.DoesNotContain("someone", incident.IssueUri.ToString());
            Assert.StartsWith("https://github.com/Muesli-HQ/muesli-windows/issues/new?title=", incident.IssueUri.AbsoluteUri);
            Assert.Contains("- Model: parakeet-v3", incident.IssueBody);
        }

        [Fact]
        public void FingerprintsAreSpecificAndHideForeignTypes()
        {
            Assert.Equal("A Windows component reported an error",
                DiagnosticErrorFingerprint.For(new COMException("x", unchecked((int)0x800401F0)), "k").Summary);
            Assert.Equal("COMException.0x800401F0", DiagnosticErrorFingerprint.For(new COMException("x", unchecked((int)0x800401F0)), "k").Signature);
            Assert.Equal("app_state.k", DiagnosticErrorFingerprint.For(null, "k").Signature);
            Assert.Equal("external", DiagnosticErrorFingerprint.For(new Foreign.SecretNamedException(), "k").Domain);
        }
    }
}

namespace Foreign
{
    public sealed class SecretNamedException : Exception;
}
