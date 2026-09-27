using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Phase E/F contracts: release reports carry explicit legal/product gates, and the one-command
/// qualification bundle refuses placeholder reviewers and never promotes an unperformed domain to
/// a pass.
/// </summary>
public sealed class ReleaseLegalAndQualificationGateTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(TestRepositoryLayout.Combine(parts));

    [Fact]
    public void ReleaseCommonDeclaresTheLegalProductGatesWithoutDecidingThem()
    {
        var common = Read("scripts", "release-common.ps1");
        Assert.Contains("function Get-MuesliReleaseLegalProductGates", common, StringComparison.Ordinal);
        foreach (var id in new[] { "EXP-01", "SIGN-02", "IDENT-01", "UPD-01", "SUP-01", "AUD-03" })
        {
            Assert.Contains(id, common, StringComparison.Ordinal);
        }

        Assert.Contains("BlockedUntilReleaseOwnerDecision", common, StringComparison.Ordinal);
        Assert.Contains("must not be described as launch-ready", common, StringComparison.Ordinal);
    }

    [Fact]
    public void RehearsalAndReleaseQualificationReportsIncludeTheLegalGates()
    {
        Assert.Contains("legalProductGates", Read("scripts", "rehearse-windows-release.ps1"), StringComparison.Ordinal);
        Assert.Contains("legalProductGates", Read("scripts", "qualify-windows-release.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void PdfExportIsDisabledUntilEligibilityIsApproved()
    {
        var writer = Read("windows-native", "Muesli.Windows.Platform", "Services", "MeetingDocumentWriter.cs");
        var viewModel = Read("windows-native", "Muesli.Windows.WinUI", "ViewModels", "MeetingDetailViewModel.cs");

        Assert.Contains("PdfExportApproved", writer, StringComparison.Ordinal);
        Assert.Contains("MUESLI_PDF_EXPORT_APPROVED", writer, StringComparison.Ordinal);
        Assert.Contains("ApprovedExportExtensions", writer, StringComparison.Ordinal);
        Assert.Contains("PDF export is disabled until QuestPDF", writer, StringComparison.Ordinal);
        Assert.Contains("MeetingDocumentWriter.ApprovedExportExtensions", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationBundleNamesEveryDomainAndRefusesPlaceholderReviewers()
    {
        var script = Read("scripts", "qualify-windows-v1.ps1");
        Assert.Contains("Test-PlaceholderReviewer", script, StringComparison.Ordinal);
        Assert.Contains("NotPerformed", script, StringComparison.Ordinal);
        Assert.Contains("evidenceKind", script, StringComparison.Ordinal);
        foreach (var domain in new[] { "dictation", "audio", "meetings", "displayA11y", "cleanMachine", "automation" })
        {
            Assert.Contains(domain, script, StringComparison.Ordinal);
        }

        Assert.Contains("Get-MachineEvidence", script, StringComparison.Ordinal);
        Assert.Contains("prerequisite", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave3DpiValidationNoLongerCallsTheRetiredWpfScript()
    {
        var wave3 = Read("scripts", "qualify-wave3-automatable.ps1");
        Assert.DoesNotContain("verify-phase12-ui.ps1", wave3, StringComparison.Ordinal);
        Assert.Contains("PerMonitorV2", wave3, StringComparison.Ordinal);
    }
}
