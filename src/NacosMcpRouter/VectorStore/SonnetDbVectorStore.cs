using System.Globalization;
using System.Text.Json;
using SonnetDB.Data;

namespace NacosMcpRouter.VectorStore;

public sealed class SonnetDbVectorStore : IVectorStore, IDisposable
{
    private const string CollectionName = "mcp_servers";
    private const string EmbeddingPath = "$.embedding";
    private const string IndexName = "idx_mcp_embedding";

    private readonly SndbConnection _connection;
    private readonly int _dimensions;
    private int _schemaEnsured;

    public SonnetDbVectorStore(string dataDir, int dimensions = 384)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir required", nameof(dataDir));
        if (dimensions < 1) throw new ArgumentOutOfRangeException(nameof(dimensions));

        Directory.CreateDirectory(dataDir);
        _connection = new SndbConnection(new SndbConnectionStringBuilder
        {
            DataSource = dataDir,
            Mode = SndbProviderMode.Embedded,
        }.ConnectionString);
        _connection.Open();
        _dimensions = dimensions;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _schemaEnsured, 1) != 0) return;

        // CREATE DOCUMENT COLLECTION ignores IF NOT EXISTS — SonnetDB throws if it already exists.
        // Try a no-op first; only CREATE if it errors.
        if (!await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync($"CREATE DOCUMENT COLLECTION {CollectionName};", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                $"CREATE VECTOR INDEX {IndexName} ON {CollectionName} ('{EmbeddingPath}') WITH (dimensions={_dimensions}, metric='cosine', m=16, ef_construction=200, ef_search=64);",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpsertAsync(McpServerDocument document, float[] embedding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length != _dimensions)
        {
            throw new ArgumentException($"embedding length {embedding.Length} != configured {_dimensions}", nameof(embedding));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var docJson = JsonSerializer.Serialize(new
        {
            name = document.Name,
            description = document.Description,
            protocol = document.Protocol,
            mcp_id = document.McpId,
            version = document.Version,
            embedding,
        });

        await ExecuteAsync(
            $"DELETE FROM {CollectionName} WHERE id = @id;",
            cancellationToken,
            ("@id", document.Name)).ConfigureAwait(false);

        await ExecuteAsync(
            $"INSERT INTO {CollectionName} (id, document) VALUES (@id, @doc);",
            cancellationToken,
            ("@id", document.Name),
            ("@doc", docJson)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            $"DELETE FROM {CollectionName} WHERE id = @id;",
            cancellationToken,
            ("@id", name)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpServerHit>> SearchAsync(float[] queryVector, int k, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (queryVector.Length != _dimensions)
        {
            throw new ArgumentException($"query length {queryVector.Length} != configured {_dimensions}", nameof(queryVector));
        }
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var vectorLiteral = FormatVectorLiteral(queryVector);
        // SonnetDB vector_search requires the vector as an unquoted literal; embed it directly.
        // Floats are formatted with G9 to avoid any locale-specific decimal separators.
        var sql =
            $"SELECT id, " +
            $"json_value(document, '$.name') AS name, " +
            $"json_value(document, '$.description') AS description, " +
            $"json_value(document, '$.protocol') AS protocol, " +
            $"json_value(document, '$.mcp_id') AS mcp_id, " +
            $"json_value(document, '$.version') AS version, " +
            $"vector_distance() AS distance " +
            $"FROM vector_search(source=>{CollectionName}, vector_field=>'{EmbeddingPath}', vector=>{vectorLiteral}, k=>{k}, metric=>'cosine') " +
            $"ORDER BY distance;";

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        var hits = new List<McpServerHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var doc = new McpServerDocument(
                Name: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Description: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Protocol: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                McpId: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Version: reader.IsDBNull(5) ? string.Empty : reader.GetString(5));
            var distance = reader.IsDBNull(6) ? double.NaN : reader.GetDouble(6);
            hits.Add(new McpServerHit(doc, distance));
        }
        return hits;
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await CountAsync(cancellationToken).ConfigureAwait(false) == 0;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {CollectionName};";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {CollectionName};";
        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            _ = Convert.ToInt32(result ?? 0);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SndbServerException)
        {
            return false;
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatVectorLiteral(float[] vector)
    {
        var sb = new System.Text.StringBuilder(vector.Length * 12);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var v = vector[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new ArgumentException("vector contains non-finite values");
            sb.Append(v.ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
