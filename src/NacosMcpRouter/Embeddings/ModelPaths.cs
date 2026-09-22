namespace NacosMcpRouter.Embeddings;

public sealed class ModelPaths
{
    public ModelPaths(string modelDir) => ModelDir = modelDir;

    public string ModelDir { get; }

    public string ModelPath => Path.Combine(ModelDir, "model.onnx");

    public string TokenizerPath => Path.Combine(ModelDir, "tokenizer.json");
}
