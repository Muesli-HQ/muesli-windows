using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class PinnedModelFileDownloaderTests
{
    private const string Revision = "https://huggingface.co/owner/model/resolve/0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(Revision, true)]
    [InlineData(Revision + "/", true)]
    [InlineData("https://huggingface.co/owner/model/resolve/main", false)]
    [InlineData("https://huggingface.co/owner/model/resolve/v1.0", false)]
    [InlineData("https://huggingface.co/owner/model/resolve/0123456", false)]
    [InlineData("http://huggingface.co/owner/model/resolve/0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/x.tar.bz2", false)]
    [InlineData(null, false)]
    public void Only_commit_pinned_hugging_face_revisions_are_immutable(string? url, bool expected) =>
        Assert.Equal(expected, PinnedModelFileDownloader.IsImmutableSource(url));

    [Fact]
    public async Task Downloads_and_verifies_every_pinned_file()
    {
        using var directory = new TestDirectory();
        var files = new Dictionary<string, byte[]>
        {
            ["encoder.int8.onnx"] = Encoding.UTF8.GetBytes("encoder-bytes"),
            ["tokens.txt"] = Encoding.UTF8.GetBytes("a 0\nb 1\n")
        };
        using var client = new HttpClient(new FakeHub(files));
        var progress = new List<ModelDownloadProgress>();

        await PinnedModelFileDownloader.DownloadAsync(
            client, Revision, Pins(files), directory.Path, "Test model", new SyncProgress(progress.Add), CancellationToken.None);

        foreach (var (name, bytes) in files)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, name)));
        }
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial", SearchOption.AllDirectories));
        var last = progress[^1];
        Assert.Equal(files.Values.Sum(bytes => (long)bytes.Length), last.BytesReceived);
        Assert.Equal(last.BytesReceived, last.TotalBytes);
    }

    [Fact]
    public async Task A_hash_mismatch_fails_and_leaves_no_file_behind()
    {
        using var directory = new TestDirectory();
        var files = new Dictionary<string, byte[]> { ["encoder.int8.onnx"] = Encoding.UTF8.GetBytes("replaced upstream") };
        var pins = new Dictionary<string, string> { ["encoder.int8.onnx"] = new string('A', 64) };
        using var client = new HttpClient(new FakeHub(files));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => PinnedModelFileDownloader.DownloadAsync(
            client, Revision, pins, directory.Path, "Test model", null, CancellationToken.None));

        Assert.Equal(ModelDownloadFailure.ChecksumMismatch, error.Failure);
        Assert.Contains("encoder.int8.onnx", error.Message);
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_mutable_source_is_refused_before_any_request()
    {
        using var directory = new TestDirectory();
        var hub = new FakeHub(new Dictionary<string, byte[]>());
        using var client = new HttpClient(hub);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PinnedModelFileDownloader.DownloadAsync(
            client,
            "https://huggingface.co/owner/model/resolve/main",
            new Dictionary<string, string> { ["tokens.txt"] = new string('A', 64) },
            directory.Path,
            "Test model",
            null,
            CancellationToken.None));

        Assert.Equal(0, hub.Requests);
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("nested/../../escape.onnx")]
    public async Task A_pinned_path_cannot_escape_the_model_directory(string relativePath)
    {
        using var directory = new TestDirectory();
        var files = new Dictionary<string, byte[]> { [relativePath] = Encoding.UTF8.GetBytes("x") };
        using var client = new HttpClient(new FakeHub(files));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => PinnedModelFileDownloader.DownloadAsync(
            client, Revision, Pins(files), Path.Combine(directory.Path, "model"), "Test model", null, CancellationToken.None));
        Assert.Equal(ModelDownloadFailure.InvalidPath, error.Failure);
    }

    [Fact]
    public void Every_catalog_model_with_a_pinned_source_uses_an_immutable_revision()
    {
        var pinned = TranscriptionModelCatalog.Models.Where(model => model.PinnedFileBaseUrl is not null).ToList();

        // The two Parakeet TDT archives sherpa-onnx replaced on 2026-11-07 must not regress to them.
        Assert.Contains(pinned, model => model.Id == TranscriptionModelCatalog.DefaultModelId);
        Assert.Contains(pinned, model => model.Id == "parakeet-v2-en-int8");
        Assert.All(pinned, model => Assert.True(
            PinnedModelFileDownloader.IsImmutableSource(model.PinnedFileBaseUrl),
            $"{model.Id} pins a mutable source: {model.PinnedFileBaseUrl}"));
        Assert.All(pinned, model => Assert.NotEmpty(model.RequiredFileSha256));
    }

    private static Dictionary<string, string> Pins(Dictionary<string, byte[]> files) =>
        files.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)));

    private sealed class SyncProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    /// <summary>Serves files at <c>{Revision}/{relativePath}</c>, answering HEAD with the length.</summary>
    private sealed class FakeHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var prefix = Revision + "/";
            var url = request.RequestUri!.AbsoluteUri;
            var name = url.StartsWith(prefix, StringComparison.Ordinal) ? Uri.UnescapeDataString(url[prefix.Length..]) : null;
            if (name is null || !files.TryGetValue(name, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
