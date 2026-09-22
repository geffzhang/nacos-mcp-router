using FluentAssertions;
using NacosMcpRouter.Embeddings;

namespace NacosMcpRouter.Tests.Embeddings;

public sealed class OnnxEmbeddingGeneratorE2ETests
{
    [Fact]
    public async Task GenerateAsync_RealModel_Produces384DimVector()
    {
        var dir = Environment.GetEnvironmentVariable("EMBEDDING_MODEL_DIR") ?? "./models/all-MiniLM-L6-v2";
        using var gen = new OnnxEmbeddingGenerator(new ModelPaths(dir));
        var v = await gen.GenerateAsync("hello world");
        v.Should().HaveCount(384);
    }
}
