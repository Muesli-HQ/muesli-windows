using System.IO;

namespace Muesli.Windows.Services;

public enum NativeAsrModelKind
{
    Parakeet,
    ParakeetTransducer,
    Whisper,
    SenseVoice,
    Qwen3Asr,
    CohereTranscribe
}

/// <summary>
/// The quality contract is deliberately separate from the engine's advertised
/// language metadata. A model may be runnable and have a valid pinned archive
/// without being qualified for release claims on Windows.
/// </summary>
public static class TranscriptionQualificationContract
{
    public const string InventoryStatus = "inventory-not-qualified";
    public const string RequiredProvider = "cpu";
    public const double MaxWordErrorRate = 0.15;
    public const double MaxCharacterErrorRate = 0.08;
    public const double MaxRealtimeFactor = 0.20;
    public const string RequiredEvidenceSummary =
        "Requires human-reviewed speech references for each advertised language claim, CPU real-audio inference, WER ≤0.15, CER ≤0.08, RTF ≤0.20, deterministic output, and model reuse.";

    public static IReadOnlyList<string> RequiredCorpusCategories { get; } =
    [
        "short-command",
        "paragraph",
        "dictionary",
        "numbers-punctuation",
        "accent",
        "silence",
        "background-noise"
    ];
}

public sealed record TranscriptionModelDefinition(
    string Id,
    string DisplayName,
    string Summary,
    string Languages,
    string SizeLabel,
    NativeAsrModelKind Kind,
    string DirectoryName,
    string ArchiveUrl,
    string ArchiveSha256,
    IReadOnlyDictionary<string, string> RequiredFileSha256,
    string Language = "auto",
    string QualificationStatus = TranscriptionQualificationContract.InventoryStatus,
    string QualificationPrerequisites = TranscriptionQualificationContract.RequiredEvidenceSummary)
{
    public IReadOnlyList<string> RequiredFiles => RequiredFileSha256.Keys.ToArray();
    public string PickerLabel => $"{DisplayName} · {SizeLabel}";
    public bool IsQualificationComplete =>
        string.Equals(QualificationStatus, "qualified", StringComparison.OrdinalIgnoreCase);
    public string ModelPath => Kind == NativeAsrModelKind.Parakeet
        ? NativeParakeetClient.ModelPath
        : Path.Combine(TranscriptionModelCatalog.ModelCacheDirectory, DirectoryName);
    public bool IsCached => RequiredFiles.All(relativePath => File.Exists(Path.Combine(ModelPath, relativePath)));
    public override string ToString() => PickerLabel;
}

public static class TranscriptionModelCatalog
{
    public const string DefaultModelId = "parakeet-v3";

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_ASR_CACHE") ??
        Path.Combine(MuesliPathService.UserProfileDirectory, ".cache", "muesli", "native-asr");

    public static IReadOnlyList<TranscriptionModelDefinition> Models { get; } =
    [
        new(
            DefaultModelId,
            "Parakeet v3",
            "Fast multilingual transcription with token timestamps. Recommended for everyday dictation and meetings.",
            "25 European languages · automatic",
            "~600 MB",
            NativeAsrModelKind.Parakeet,
            "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            NativeParakeetClient.ModelArchiveSha256,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["encoder.int8.onnx"] = "ACFC2B4456377E15D04F0243AF540B7FE7C992F8D898D751CF134C3A55FD2247",
                ["decoder.int8.onnx"] = "179E50C43D1A9DE79C8A24149A2F9BAC6EB5981823F2A2ED88D655B24248DB4E",
                ["joiner.int8.onnx"] = "3164C13FC2821009440D20FCB5FDC78BFF28B4DB2F8D0F0B329101719C0948B3",
                ["tokens.txt"] = "D58544679EA4BC6AC563D1F545EB7D474BD6CFA467F0A6E2C1DC1C7D37E3C35D"
            }),
        new(
            "parakeet-unified-en-int8",
            "Parakeet Unified English INT8",
            "Newest Parakeet architecture for accurate English dictation, matching the current macOS recommendation.",
            "English",
            "478 MB download · ~633 MB installed",
            NativeAsrModelKind.ParakeetTransducer,
            "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming.tar.bz2",
            "99F63605B3A85A54C250C0869670A687B7D6598A47BF2421515E1F839A76E150",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["encoder.int8.onnx"] = "6716910B7A0833997FEC7A410494C995D70124001A0E9B66D6370D6ACED577E0",
                ["decoder.int8.onnx"] = "A5E223392C90E75F8144CDB5EB95AF7625DB389E39EDEF2BD1A9C872B3298FE6",
                ["joiner.int8.onnx"] = "869F43F7D24595C55581AD3BF249A935FB8A71389FBDAA7504B9F46F93140F8A",
                ["tokens.txt"] = "DC0B4584AB2E4DDBF888425C076C61B736E7356A015250DB7D307E6F1A8188FF"
            },
            "en"),
        new(
            "parakeet-v2-en-int8",
            "Parakeet v2 English INT8",
            "Older, dependable English-only Parakeet option retained by the current macOS catalog.",
            "English",
            "460 MB download · ~631 MB installed",
            NativeAsrModelKind.ParakeetTransducer,
            "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
            "157C157BC51155E03E37D2466522A3A737DD9C72BB25F36EB18912964161E1AD",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["encoder.int8.onnx"] = "A32B12D17BBBC309D0686FBBCC2987B5E9B8333A7DA83FA6B089F0A2ACD651AB",
                ["decoder.int8.onnx"] = "B6BB64963457237B900E496EE9994B59294526439FBCC1FECF705B31A15C6B4E",
                ["joiner.int8.onnx"] = "7946164367946E7F9F29A122407C3252B680DBAE9A51343EB2488D057C3C43D2",
                ["tokens.txt"] = "EC182B70DD42113AFF6C5372C75CAC58C952443EB22322F57BBD7F53977D497D"
            },
            "en"),
        new(
            "whisper-tiny-multilingual",
            "Whisper Tiny Multilingual",
            "Lightest multilingual Whisper option with automatic language detection.",
            "Multilingual · automatic",
            "111 MB download · ~100 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-tiny",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-tiny.tar.bz2",
            "C46116994E539AA165266D96B325252728429C12535EB9D8B6A2B10F129E66B1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tiny-encoder.int8.onnx"] = "D24FB083AE3B1041FC24E97971D60E280C9342201FBB67B0AB428A8B4A51A434",
                ["tiny-decoder.int8.onnx"] = "D2FECE8DD42771F1DF975C6C0445770D0C292BF7547C2CAE04A6C0CC57540925",
                ["tiny-tokens.txt"] = "B34B360DBB493E781E479794586D661700670D65564001F23024971D1F2FA126"
            },
            ""),
        new(
            "whisper-tiny-en",
            "Whisper Tiny English",
            "Smallest download and lowest memory use. Best for quick English dictation on slower PCs.",
            "English",
            "113 MB download · ~100 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-tiny.en",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-tiny.en.tar.bz2",
            "2BD6CF965C8BB3E068EF9FA2191387EE63A9DFA2A4E37582A8109641C20005DD",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tiny.en-encoder.int8.onnx"] = "0CE578B827C94A961AACB8FA14B02F096504B337E5C94BE37C36238CBE3E8BC6",
                ["tiny.en-decoder.int8.onnx"] = "06C0E6FF6348D427E51839219D1C886C18CFDF411E629E33F5E1679BFF9C1527",
                ["tiny.en-tokens.txt"] = "306CD27F03C1A714ECA7108E03D66B7DC042ABE8C258B44C199A7ED9838DD930"
            },
            "en"),
        new(
            "whisper-small-multilingual",
            "Whisper Small Multilingual",
            "Balanced multilingual Whisper model with stronger accent and noise handling than Tiny.",
            "Multilingual · automatic",
            "610 MB download · ~359 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-small",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-small.tar.bz2",
            "486A46AFBB7BA798507190FFE02FEA2DD726049AF212E774537EFAC6AFB210A6",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["small-encoder.int8.onnx"] = "4CBE7B22FA9026B843B60A68640C747DE05BAFB1A11B57EDC0E66C232D9F33A9",
                ["small-decoder.int8.onnx"] = "ACAD50B5C782696E91B55914CC5AB4F756F1532F76E22AA6FC615F39FB69A8EE",
                ["small-tokens.txt"] = "B34B360DBB493E781E479794586D661700670D65564001F23024971D1F2FA126"
            },
            ""),
        new(
            "whisper-small-en",
            "Whisper Small English",
            "Balanced English Whisper model with stronger accuracy than Tiny.",
            "English",
            "606 MB download · ~360 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-small.en",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-small.en.tar.bz2",
            "0CDBA2B8AAAB69E04847F3427CC9709574112E67913A1A84B7FEC3A8729FAA9A",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["small.en-encoder.int8.onnx"] = "8BDAC288F369AA94EE2194059238C465ED82EA9D47EE8FA4A8C0A891873E462F",
                ["small.en-decoder.int8.onnx"] = "710CCF890E10F3FAA15F51EC346081A2723C9F3ADB6E4DA81C6573A5A6F877FB",
                ["small.en-tokens.txt"] = "306CD27F03C1A714ECA7108E03D66B7DC042ABE8C258B44C199A7ED9838DD930"
            },
            "en"),
        new(
            "whisper-medium-en",
            "Whisper Medium English",
            "Higher-accuracy English Whisper model for imported recordings and difficult audio.",
            "English",
            "1.8 GB download · ~905 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-medium.en",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-medium.en.tar.bz2",
            "73D95C169A410B5F23A79F8901374B26E0A16A09EA7F02B5E1DB983F4CDFDD67",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["medium.en-encoder.int8.onnx"] = "5A8E3A36619E0B67DB9320EEF3152DB59D4B440F5CE0212D2C162A61B750BF80",
                ["medium.en-decoder.int8.onnx"] = "7303BE339ED4E51F4FFB7AE84F3803B10CF8E67E1DCF8A98CB4D843F0DEA0141",
                ["medium.en-tokens.txt"] = "306CD27F03C1A714ECA7108E03D66B7DC042ABE8C258B44C199A7ED9838DD930"
            },
            "en"),
        new(
            "whisper-large-turbo-multilingual",
            "Whisper Large Turbo Multilingual",
            "Strongest multilingual Whisper option in the current macOS catalog, optimized for difficult audio.",
            "Multilingual · automatic",
            "538 MB download · ~989 MB installed",
            NativeAsrModelKind.Whisper,
            "sherpa-onnx-whisper-turbo",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-turbo.tar.bz2",
            "B11ACBBCD660B44A8E0DF33724FEB5AAA709CF65668F2823D59F656312544F22",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["turbo-encoder.int8.onnx"] = "B02DCDF54F348741E93FE732B67D933C8DCB6735655F710640143081DB38878B",
                ["turbo-decoder.int8.onnx"] = "20ACCD02388482EB3A46BD615631ADFDC85E1EB2C7DB9EA3F02A40FFE6B81547",
                ["turbo-tokens.txt"] = "B34B360DBB493E781E479794586D661700670D65564001F23024971D1F2FA126"
            },
            ""),
        new(
            "sensevoice-small-int8",
            "SenseVoice Small INT8",
            "Fast multilingual speech recognition with punctuation and inverse text normalization.",
            "Chinese, English, Japanese, Korean, Cantonese · automatic",
            "156 MB download · ~230 MB installed",
            NativeAsrModelKind.SenseVoice,
            "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2",
            "7D1EFA2138A65B0B488DF37F8B89E3D91A60676E416F515B952358D83DFD347E",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["model.int8.onnx"] = "C71F0CE00BEC95B07744E116345E33D8CBBE08CEF896382CF907BF4B51A2CD51",
                ["tokens.txt"] = "F449EB28DC567533D7FA59BE34E2ABCA8784F771850C78A47FB731A31429A1DC"
            }),
        new(
            "qwen3-asr-0.6b-int8",
            "Qwen3-ASR 0.6B INT8",
            "Modern multilingual recognizer with broad language and accent coverage.",
            "30 languages plus Chinese dialects · automatic",
            "838 MB download · ~954 MB installed",
            NativeAsrModelKind.Qwen3Asr,
            "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2",
            "393F8A14E2F5FB96746AAAB342997A40641001FBD5BF9592A080A8329178EE96",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["conv_frontend.onnx"] = "D22DC4423E0940E49884E903D2EA2F7E5567C14FC1AED97E4E26D6B8F208EF9E",
                ["encoder.int8.onnx"] = "60748D3E6744A57C9C91E1B17424A6C2990567E8ADCEB0783940C03ED98FA9D9",
                ["decoder.int8.onnx"] = "4F6885BE5959AE26AF3089D38EE7972C5FAFBEEB1CF8D5E76EAB6D8B61CA5771",
                ["tokenizer/merges.txt"] = "8831E4F1A044471340F7C0A83D7BD71306A5B867E95FD870F74D0C5308A904D5",
                ["tokenizer/vocab.json"] = "CA10D7E9FB3ED18575DD1E277A2579C16D108E32F27439684AFA0E10B1440910"
            }),
        new(
            "cohere-transcribe-int8-en",
            "Cohere Transcribe INT8",
            "Large multilingual model configured for punctuated English transcription.",
            "English selected (model supports 14 languages)",
            "1.6 GB download · ~2.7 GB installed",
            NativeAsrModelKind.CohereTranscribe,
            "sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01.tar.bz2",
            "BD582588D50685A795DCD2807AB77E11361B8312D96C53884682DEF45AB4206D",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["encoder.int8.onnx"] = "CF704F8CFA90E3F0A76F9FFC05998BDF00BA9AE983192C14A85A3A5EB008B367",
                ["encoder.int8.onnx.data"] = "BCF1B7148C8518AE52DF1AD2D2FC2B4E89261EA23E6C874EEF1D9F55BCBAA4A3",
                ["decoder.int8.onnx"] = "8372CA6C8FF4DB8B916CA3592F5C757A715E691B9EDEC751BA19B29FC854BAF9",
                ["tokens.txt"] = "013EDE043AE2480E3A9205CC34550D9686100CC682BACC90F702FACDFBB93035"
            },
            "en")
    ];

    public static TranscriptionModelDefinition Get(string? modelId)
    {
        return Models.FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)) ??
               Models[0];
    }

    public static bool TryGet(string? modelId, out TranscriptionModelDefinition model)
    {
        model = Models.FirstOrDefault(candidate =>
            candidate.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))!;
        return model is not null;
    }

    public static TranscriptionModelDefinition GetRequired(string? modelId)
    {
        return TryGet(modelId, out var model)
            ? model
            : throw new ArgumentOutOfRangeException(nameof(modelId), modelId, "Unknown Windows transcription model ID.");
    }

    public static string NormalizeId(string? modelId) => Get(modelId).Id;

    public static long CacheSizeBytes()
    {
        var genericBytes = Directory.Exists(ModelCacheDirectory)
            ? Directory.EnumerateFiles(ModelCacheDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;
        return genericBytes + NativeParakeetClient.ModelCacheSizeBytes();
    }

    public static void OpenCacheDirectory()
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ModelCacheDirectory,
            UseShellExecute = true
        });
    }
}
