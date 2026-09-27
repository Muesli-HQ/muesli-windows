using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// UPD-01: app-side update workflow. Fixture RSA keys stand in for the production signer (D5), so
/// the verification, downgrade, tamper, offline, cancellation, and rollback contracts are all
/// exercised without a real release certificate.
/// </summary>
public sealed class UpdateWorkflowTests
{
    [Fact]
    public void SignedFixtureManifestVerifiesAgainstThePinnedPublisherKey()
    {
        var fixture = Fixture("0.4.0");

        var result = SignedUpdateVerifier.Verify(fixture.Manifest, fixture.Signature, fixture.PublisherPin);

        Assert.True(result.IsVerified, result.FailureMessage);
        Assert.Equal("0.4.0", result.Manifest!.Version);
        Assert.Equal(fixture.PublisherPin, result.PublisherPublicKeySha256);
    }

    [Fact]
    public void TamperedManifestFailsSignatureVerification()
    {
        var fixture = Fixture("0.4.0");
        // Change the version inside the JSON without breaking it: the detached hash no longer matches.
        var text = Encoding.UTF8.GetString(fixture.Manifest);
        var index = text.IndexOf("0.4.0", StringComparison.Ordinal);
        Assert.True(index >= 0);
        var tampered = Encoding.UTF8.GetBytes(text[..index] + "0.9.9" + text[(index + 5)..]);

        var result = SignedUpdateVerifier.Verify(tampered, fixture.Signature, fixture.PublisherPin);

        Assert.False(result.IsVerified);
        Assert.Equal("manifest-hash-mismatch", result.FailureCode);
    }

    [Fact]
    public void AWellSignedManifestFromADifferentKeyIsRejected()
    {
        var fixture = Fixture("0.4.0");
        var other = Fixture("0.4.0");

        var result = SignedUpdateVerifier.Verify(fixture.Manifest, fixture.Signature, other.PublisherPin);

        Assert.False(result.IsVerified);
        Assert.Equal("publisher-pin-mismatch", result.FailureCode);
    }

    [Fact]
    public void UnsignedManifestNeverAuthorizesAnUpdate()
    {
        var fixture = Fixture("0.4.0", mode: "unsigned");

        var result = SignedUpdateVerifier.Verify(fixture.Manifest, fixture.Signature, fixture.PublisherPin);

        Assert.False(result.IsVerified);
        Assert.Equal("unsigned-manifest", result.FailureCode);
    }

    [Fact]
    public void MissingPublisherPinFailsClosed()
    {
        var fixture = Fixture("0.4.0");

        var result = SignedUpdateVerifier.Verify(fixture.Manifest, fixture.Signature, pinnedPublisherPublicKeySha256: "");

        Assert.False(result.IsVerified);
        Assert.Equal("missing-publisher-pin", result.FailureCode);
    }

    [Fact]
    public async Task NewerVersionIsOffered()
    {
        var fixture = Fixture("0.4.0");
        using var client = new HttpClient(new UpdateHandler(fixture));

        var result = await new SignedUpdateService(client).CheckAsync(
            "0.3.0", "https://example.test/update.json", "https://example.test/update.json.signature.json",
            fixture.PublisherPin);

        Assert.True(result.HasUpdate);
        Assert.Equal("0.4.0", result.Version);
    }

    [Fact]
    public async Task EqualVersionIsUpToDate()
    {
        var fixture = Fixture("0.3.0");
        using var client = new HttpClient(new UpdateHandler(fixture));

        var result = await new SignedUpdateService(client).CheckAsync(
            "0.3.0.0", "https://example.test/update.json", "https://example.test/sig.json", fixture.PublisherPin);

        Assert.Equal(UpdateAvailability.UpToDate, result.Availability);
    }

    [Fact]
    public async Task DowngradeIsRejected()
    {
        var fixture = Fixture("0.2.0");
        using var client = new HttpClient(new UpdateHandler(fixture));

        var result = await new SignedUpdateService(client).CheckAsync(
            "0.3.0", "https://example.test/update.json", "https://example.test/sig.json", fixture.PublisherPin);

        Assert.Equal(UpdateAvailability.DowngradeRejected, result.Availability);
        Assert.Equal("downgrade", result.FailureCode);
    }

    [Fact]
    public async Task ClientOlderThanMinimumSupportedIsRejected()
    {
        var fixture = Fixture("0.6.0", minimumSupportedVersion: "0.5.0");
        using var client = new HttpClient(new UpdateHandler(fixture));

        var result = await new SignedUpdateService(client).CheckAsync(
            "0.3.0", "https://example.test/update.json", "https://example.test/sig.json", fixture.PublisherPin);

        Assert.Equal(UpdateAvailability.UnsupportedClient, result.Availability);
        Assert.Equal("unsupported-client", result.FailureCode);
    }

    [Fact]
    public async Task OfflineCheckIsReportedWithoutFailingTheApp()
    {
        using var client = new HttpClient(new UpdateHandler(null));

        var result = await new SignedUpdateService(client).CheckAsync(
            "0.3.0", "https://example.test/update.json", "https://example.test/sig.json", new string('a', 64));

        Assert.Equal(UpdateAvailability.Offline, result.Availability);
        Assert.Equal("offline", result.FailureCode);
    }

    [Fact]
    public async Task UnconfiguredChannelDisablesTheCheck()
    {
        using var client = new HttpClient(new UpdateHandler(null));

        var result = await new SignedUpdateService(client).CheckAsync("0.3.0", "", "", "");

        Assert.Equal(UpdateAvailability.Disabled, result.Availability);
        Assert.Equal("not-configured", result.FailureCode);
    }

    [Fact]
    public async Task VerifiedPackageIsDownloadedToAStagingDirectory()
    {
        var fixture = Fixture("0.4.0");
        using var client = new HttpClient(new UpdateHandler(fixture));
        using var directory = new TestDirectory();
        var package = new UpdatePackage
        {
            Name = "portableZip",
            FileName = "Muesli.zip",
            Bytes = fixture.PackageBytes.Length,
            Sha256 = fixture.PackageSha256
        };

        var result = await new SignedUpdateService(client).DownloadAsync(
            package, "https://example.test/Muesli.zip", directory.Path);

        Assert.Equal(UpdateAvailability.UpdateAvailable, result.Availability);
        Assert.NotNull(result.StagedPackagePath);
        Assert.True(File.Exists(result.StagedPackagePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task TamperedPackageIsDiscardedAndNeverStaged()
    {
        var fixture = Fixture("0.4.0");
        using var client = new HttpClient(new UpdateHandler(fixture));
        using var directory = new TestDirectory();
        var package = new UpdatePackage
        {
            Name = "portableZip",
            FileName = "Muesli.zip",
            Bytes = fixture.PackageBytes.Length,
            Sha256 = new string('0', 64)
        };

        var result = await new SignedUpdateService(client).DownloadAsync(
            package, "https://example.test/Muesli.zip", directory.Path);

        Assert.Equal(UpdateAvailability.VerificationFailed, result.Availability);
        Assert.Equal("tamper", result.FailureCode);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancelledDownloadDoesNotStageAnything()
    {
        var fixture = Fixture("0.4.0");
        using var client = new HttpClient(new UpdateHandler(fixture));
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await new SignedUpdateService(client).DownloadAsync(
            new UpdatePackage { Name = "portableZip", FileName = "Muesli.zip", Bytes = 1, Sha256 = new string('a', 64) },
            "https://example.test/Muesli.zip",
            directory.Path,
            cancellation.Token);

        Assert.Equal(UpdateAvailability.Cancelled, result.Availability);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task PackageNameCannotEscapeTheStagingDirectory()
    {
        var fixture = Fixture("0.4.0");
        using var client = new HttpClient(new UpdateHandler(fixture));
        using var directory = new TestDirectory();

        var result = await new SignedUpdateService(client).DownloadAsync(
            new UpdatePackage { Name = "msix", FileName = @"..\outside.msix", Bytes = fixture.PackageBytes.Length, Sha256 = fixture.PackageSha256 },
            "https://example.test/Muesli.zip", directory.Path);

        Assert.Equal("invalid-package", result.FailureCode);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void RollbackJournalDiscardsAnInterruptedStagedPackage()
    {
        using var directory = new TestDirectory();
        var staged = Path.Combine(directory.Path, "Muesli.zip");
        File.WriteAllText(staged, "half-installed");
        var journal = new UpdateRollbackJournal(Path.Combine(directory.Path, "rollback.json"));
        journal.Begin(new UpdateRollbackEntry("0.3.0", staged, new string('a', 64), "staged", DateTimeOffset.UtcNow));

        var recovered = journal.TryRecover(out var entry);

        Assert.True(recovered);
        Assert.NotNull(entry);
        Assert.False(File.Exists(staged));
        Assert.Null(journal.Read());
    }

    [Fact]
    public void RollbackJournalNeverDeletesAnUnownedPath()
    {
        using var journalDirectory = new TestDirectory();
        using var otherDirectory = new TestDirectory();
        var otherFile = Path.Combine(otherDirectory.Path, "keep.msix");
        File.WriteAllText(otherFile, "keep");
        var journal = new UpdateRollbackJournal(Path.Combine(journalDirectory.Path, "rollback.json"));
        journal.Begin(new UpdateRollbackEntry("0.3.0", otherFile, new string('a', 64), "staged", DateTimeOffset.UtcNow));

        Assert.True(journal.TryRecover(out _));
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public async Task ProductionInstallerFailsClosedUntilSigningExists()
    {
        var result = await new WindowsProductionUpdateInstaller().InstallAsync(@"C:\staged\Muesli.msix");

        Assert.False(result.Succeeded);
        Assert.Equal(WindowsProductionUpdateInstaller.NotConfiguredCode, result.FailureCode);
    }

    private static UpdateFixture Fixture(
        string version,
        string? minimumSupportedVersion = null,
        string mode = "fixture")
    {
        using var rsa = RSA.Create(2048);
        var packageBytes = Encoding.UTF8.GetBytes("verified update package bytes");
        var packageSha = SignedUpdateVerifier.HashHex(packageBytes);
        var status = mode == "production" ? "Valid" : "FixtureOnly";

        var signing = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["status"] = status,
            ["algorithm"] = "RSA-SHA256",
            ["signaturePresent"] = mode != "unsigned",
            ["authenticodeRequired"] = mode == "production",
            ["authenticodeVerified"] = mode == "production",
            ["publisherPublicKeySha256"] = "",
            ["publisherCertificateThumbprint"] = "",
            ["publisherSubject"] = "",
            ["detachedSignatureFile"] = "muesli.update.json.signature.json"
        };

        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["contract"] = "muesli.windows.update-channel",
            ["product"] = "Muesli for Windows",
            ["channel"] = "v1",
            ["version"] = version,
            ["runtime"] = "win-x64",
            ["minimumSupportedVersion"] = minimumSupportedVersion,
            ["releaseNotes"] = "Fixture release notes.",
            ["signing"] = signing,
            ["packages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "portableZip",
                    ["fileName"] = "Muesli.zip",
                    ["bytes"] = packageBytes.Length,
                    ["sha256"] = packageSha
                }
            },
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o")
        };

        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var pin = SignedUpdateVerifier.PublisherKeyHash(publicKey)!;
        signing["publisherPublicKeySha256"] = pin;

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateManifestContract.Json);
        byte[] signatureBytes = mode == "unsigned"
            ? []
            : rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var detached = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["contract"] = "muesli.windows.update-channel.detached-signature",
            ["manifestFileName"] = "muesli.update.json",
            ["manifestSha256"] = SignedUpdateVerifier.HashHex(manifestBytes),
            ["mode"] = mode,
            ["status"] = status,
            ["algorithm"] = "RSA-SHA256",
            ["signatureBase64"] = Convert.ToBase64String(signatureBytes),
            ["publicKeySpkiBase64"] = Convert.ToBase64String(publicKey),
            ["publisherPublicKeySha256"] = pin,
            ["publisherCertificateThumbprint"] = "",
            ["publisherSubject"] = "",
            ["authenticode"] = mode == "production",
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o")
        };

        return new UpdateFixture(
            manifestBytes,
            JsonSerializer.SerializeToUtf8Bytes(detached, UpdateManifestContract.Json),
            pin,
            packageBytes,
            packageSha);
    }

    private sealed record UpdateFixture(
        byte[] Manifest,
        byte[] Signature,
        string PublisherPin,
        byte[] PackageBytes,
        string PackageSha256);

    /// <summary>Serves manifest/signature/package bytes by URL, or throws when simulating offline.</summary>
    private sealed class UpdateHandler(UpdateFixture? fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture is null)
            {
                throw new HttpRequestException("simulated offline");
            }

            var uri = request.RequestUri?.ToString() ?? "";
            var body = uri.EndsWith("sig.json", StringComparison.OrdinalIgnoreCase) ||
                       uri.Contains("signature", StringComparison.OrdinalIgnoreCase)
                ? fixture.Signature
                : uri.Contains("Muesli.zip", StringComparison.OrdinalIgnoreCase)
                    ? fixture.PackageBytes
                    : fixture.Manifest;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            });
        }
    }
}
