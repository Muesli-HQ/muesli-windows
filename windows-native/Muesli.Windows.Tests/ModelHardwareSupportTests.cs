using System.Linq;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-36: Windows model compatibility is hardware; only CUDA-only Bodhan Pro can be unsupported.</summary>
public sealed class ModelHardwareSupportTests
{
    [Fact]
    public void Only_cuda_only_models_depend_on_the_gpu()
    {
        var gpu = ExecutionProviderService.PreferredProviders(ExecutionProviderRole.OfflineTranscription, NativeAsrModelKind.Bodhan)
            .Any(request => request.IsGpu);
        foreach (var model in TranscriptionModelCatalog.Models)
        {
            var expected = !(model.Kind == NativeAsrModelKind.Bodhan && model.Id.EndsWith("-fp16")) || gpu;
            Assert.Equal(expected, TranscriptionModelCatalog.IsSupportedOnThisPc(model));
        }
        Assert.True(TranscriptionModelCatalog.IsSupportedOnThisPc(TranscriptionModelCatalog.GetRequired(TranscriptionModelCatalog.DefaultModelId)));
    }
}
