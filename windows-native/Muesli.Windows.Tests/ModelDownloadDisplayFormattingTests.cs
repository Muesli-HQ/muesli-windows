using System;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

public sealed class ModelDownloadDisplayFormattingTests
{
    [Fact]
    public void Formats_match_macos()
    {
        Assert.Equal("1.3 GB", ModelDownloadDisplayFormatting.Bytes(1_270_000_000));
        Assert.Equal("512 B", ModelDownloadDisplayFormatting.Bytes(512));
        Assert.Equal("45s", ModelDownloadDisplayFormatting.Eta(45));
        Assert.Equal("1m 05s", ModelDownloadDisplayFormatting.Eta(65));
        Assert.Equal("2h 03m", ModelDownloadDisplayFormatting.Eta(7380));
        Assert.Null(ModelDownloadDisplayFormatting.Eta(double.PositiveInfinity));
        Assert.Null(ModelDownloadDisplayFormatting.Eta(1e300));
        Assert.Equal("12.5 MB/s", ModelDownloadDisplayFormatting.Rate(12_500_000));
        Assert.Equal("", ModelDownloadDisplayFormatting.Rate(0));
    }

    [Fact]
    public void Meter_reports_speed_and_time_left_excluding_resumed_bytes()
    {
        var now = DateTimeOffset.UnixEpoch;
        var meter = new ModelTransferMeter(() => now);
        // Resumed at 500 MB of 2.5 GB: only bytes moved since then count toward speed.
        Assert.Equal("Downloading Bodhan Core Pro 20% · 500.0 MB / 2.5 GB",
            meter.Describe(new("Downloading Bodhan Core Pro", 500_000_000, 2_500_000_000)));
        now = now.AddSeconds(10);
        Assert.Equal("Downloading Bodhan Core Pro 24% · 600.0 MB / 2.5 GB · 10.0 MB/s · ~3m 10s left",
            meter.Describe(new("Downloading Bodhan Core Pro", 600_000_000, 2_500_000_000)));
        // Extraction is a phase without bytes; finishing shows no speed.
        Assert.Equal("Extracting Bodhan Core Pro", meter.Describe(new("Extracting Bodhan Core Pro", 0, null)));
        Assert.Equal("Bodhan Core Pro verified 100%", meter.Describe(new("Bodhan Core Pro verified", 1, 1)));
    }

    [Fact]
    public void Bodhan_tiers_are_named_pro_and_lite_with_unchanged_ids()
    {
        Assert.Equal("Bodhan Core Lite", TranscriptionModelCatalog.GetRequired("bodhan-core-int8").DisplayName);
        Assert.Equal("Bodhan Core Pro", TranscriptionModelCatalog.GetRequired("bodhan-core-fp16").DisplayName);
        Assert.Equal("Bodhan Flex Lite", TranscriptionModelCatalog.GetRequired("bodhan-flex-int8").DisplayName);
        Assert.Equal("Bodhan Flex Pro", TranscriptionModelCatalog.GetRequired("bodhan-flex-fp16").DisplayName);
    }
}
