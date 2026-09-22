using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace NacosMcpRouter.Embeddings;

internal static class HuggingFaceTokenizerLoader
{
    public static (Tokenizer Tokenizer, string WorkingDirectory) Load(string tokenizerPath)
    {
        using var stream = File.OpenRead(tokenizerPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        if (!root.TryGetProperty("model", out var modelElement))
        {
            throw new InvalidOperationException($"Tokenizer file '{tokenizerPath}' is missing the required 'model' section.");
        }
        if (!modelElement.TryGetProperty("type", out var typeElement))
        {
            throw new InvalidOperationException($"Tokenizer file '{tokenizerPath}' does not specify a model type under 'model.type'.");
        }
        var modelType = typeElement.GetString();

        var tempDir = Path.Join(Path.GetTempPath(), "nacos-mcp-router-tokenizers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            return modelType?.Trim().ToUpperInvariant() switch
            {
                "BPE" => LoadBpe(root, modelElement, tempDir),
                "WORDPIECE" => LoadWordPiece(modelElement, tempDir),
                _ => throw new NotSupportedException($"Tokenizer model type '{modelType}' is not supported yet."),
            };
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort */ }
            throw;
        }
    }

    private static (Tokenizer Tokenizer, string WorkingDirectory) LoadBpe(JsonElement root, JsonElement modelElement, string tempDir)
    {
        var vocabPath = Path.Join(tempDir, "vocab.json");
        var mergesPath = Path.Join(tempDir, "merges.txt");

        if (!modelElement.TryGetProperty("vocab", out var vocabElement))
        {
            throw new InvalidOperationException("BPE tokenizer model is missing the required 'vocab' field.");
        }
        File.WriteAllText(vocabPath, vocabElement.GetRawText());

        if (!modelElement.TryGetProperty("merges", out var mergesElement))
        {
            throw new InvalidOperationException("BPE tokenizer model is missing the required 'merges' field.");
        }
        var mergeLines = mergesElement
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : string.Join(" ", item.EnumerateArray().Select(static part => part.GetString() ?? string.Empty)))
            .Where(static line => !string.IsNullOrWhiteSpace(line));
        File.WriteAllLines(mergesPath, ["#version: 0.2", .. mergeLines]);

        var unknownToken = modelElement.TryGetProperty("unk_token", out var unkTokenElement)
            ? unkTokenElement.GetString()
            : "[UNK]";
        var continuingSubwordPrefix = modelElement.TryGetProperty("continuing_subword_prefix", out var prefixElement)
            ? prefixElement.GetString()
            : null;
        var endOfWordSuffix = modelElement.TryGetProperty("end_of_word_suffix", out var suffixElement)
            ? suffixElement.GetString()
            : null;

        var preTokenizer = ResolvePreTokenizer(root, out var byteLevel);

        var tokenizer = BpeTokenizer.Create(new BpeOptions(vocabPath, mergesPath)
        {
            PreTokenizer = preTokenizer,
            SpecialTokens = new Dictionary<string, int>(),
            UnknownToken = unknownToken ?? "[UNK]",
            ContinuingSubwordPrefix = continuingSubwordPrefix ?? string.Empty,
            EndOfWordSuffix = endOfWordSuffix ?? string.Empty,
            FuseUnknownTokens = false,
            ByteLevel = byteLevel,
        });
        return (tokenizer, tempDir);
    }

    private static (Tokenizer Tokenizer, string WorkingDirectory) LoadWordPiece(JsonElement modelElement, string tempDir)
    {
        var vocabPath = Path.Join(tempDir, "vocab.txt");
        if (!modelElement.TryGetProperty("vocab", out var vocabElement))
        {
            throw new InvalidOperationException("WordPiece tokenizer model is missing the required 'vocab' field.");
        }

        var unknownToken = modelElement.TryGetProperty("unk_token", out var unkTokenElement)
            ? unkTokenElement.GetString()
            : "[UNK]";

        if (vocabElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("WordPiece tokenizer 'vocab' must be a JSON object mapping token to id.");
        }

        var pairs = vocabElement
            .EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var id)
                && id >= 0)
            .Select(property => (Token: property.Name, Id: property.Value.GetInt32()))
            .ToList();

        if (pairs.Count == 0)
        {
            throw new InvalidOperationException("WordPiece tokenizer vocab does not contain any valid token-id entries.");
        }

        var maxId = pairs.Max(p => p.Id);
        var tokens = Enumerable.Repeat(unknownToken ?? "[UNK]", maxId + 1).ToArray();
        foreach (var (token, id) in pairs)
        {
            tokens[id] = token;
        }
        File.WriteAllLines(vocabPath, tokens);

        var tokenizer = BertTokenizer.Create(vocabPath);
        return (tokenizer, tempDir);
    }

    private static PreTokenizer ResolvePreTokenizer(JsonElement root, out bool byteLevel)
    {
        if (!root.TryGetProperty("pre_tokenizer", out var preTokenizerElement))
        {
            byteLevel = false;
            return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
        }
        return ResolvePreTokenizerElement(preTokenizerElement, out byteLevel);
    }

    private static PreTokenizer ResolvePreTokenizerElement(JsonElement preTokenizerElement, out bool byteLevel)
    {
        if (!preTokenizerElement.TryGetProperty("type", out var typeElement))
        {
            throw new InvalidOperationException("Tokenizer pre_tokenizer section is missing the required 'type' field.");
        }
        var preTokenizerType = typeElement.GetString();
        switch (preTokenizerType?.Trim().ToUpperInvariant())
        {
            case "BYTELEVEL":
            case "ROBERTA":
                byteLevel = true;
                return RobertaPreTokenizer.Instance;
            case "WHITESPACE":
            case "WHITESPACESPLIT":
                byteLevel = false;
                return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
            case "BERTPRETOKENIZER":
                byteLevel = false;
                return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
            case "SEQUENCE":
                return ResolveSequencePreTokenizer(preTokenizerElement, out byteLevel);
            default:
                throw new NotSupportedException($"Tokenizer pre-tokenizer type '{preTokenizerType}' is not supported yet.");
        }
    }

    private static PreTokenizer ResolveSequencePreTokenizer(JsonElement preTokenizerElement, out bool byteLevel)
    {
        if (!TryGetPreTokenizerSequence(preTokenizerElement, out var preTokenizers))
        {
            throw new InvalidOperationException("Tokenizer pre_tokenizer sequence is missing its pretokenizers array.");
        }

        byteLevel = false;
        foreach (var item in preTokenizers.EnumerateArray())
        {
            _ = ResolvePreTokenizerElement(item, out var itemByteLevel);
            byteLevel |= itemByteLevel;
        }

        if (byteLevel)
        {
            return RobertaPreTokenizer.Instance;
        }

        return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
    }

    private static bool TryGetPreTokenizerSequence(JsonElement preTokenizerElement, out JsonElement preTokenizers) =>
        preTokenizerElement.TryGetProperty("pretokenizers", out preTokenizers)
        || preTokenizerElement.TryGetProperty("pre_tokenizers", out preTokenizers);


    private static bool IsExpectedCleanupException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
