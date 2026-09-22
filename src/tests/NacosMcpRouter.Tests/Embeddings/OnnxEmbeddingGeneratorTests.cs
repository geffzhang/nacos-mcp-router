using FluentAssertions;
using NacosMcpRouter.Embeddings;

namespace NacosMcpRouter.Tests.Embeddings;

public sealed class OnnxEmbeddingGeneratorTests
{
    [Fact]
    public void Constructor_MissingModelFile_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nacos-mcp-router-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new ModelPaths(dir);
            var act = () => new OnnxEmbeddingGenerator(paths);
            act.Should().Throw<FileNotFoundException>().WithMessage("*model.onnx*");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Dimensions_ReturnsConfiguredValue()
    {
        var gen = new FakeEmbeddingGenerator(384);
        gen.Dimensions.Should().Be(384);
    }
}
