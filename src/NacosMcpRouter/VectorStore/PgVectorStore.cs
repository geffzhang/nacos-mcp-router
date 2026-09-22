using Npgsql;
using Pgvector;

namespace NacosMcpRouter.VectorStore;

/// <summary>
/// PostgreSQL-backed vector store using the pgvector extension. Shared by every router
/// instance so search results are identical across the cluster (no per-instance state).
/// </summary>
public sealed class PgVectorStore : IVectorStore, IDisposable
{
    private const string TableName = "mcp_servers";
    private const string IndexName = "idx_mcp_embedding";

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _dimensions;
    private int _schemaEnsured;

    public PgVectorStore(string connectionString, int dimensions = 384)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (dimensions < 1) throw new ArgumentOutOfRangeException(nameof(dimensions));
        // UseVector registers the pgvector type handler; the data source must be
        // built via NpgsqlDataSourceBuilder (NpgsqlDataSource.Create would not map
        // Pgvector.Vector parameters to the vector pgtype).
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _dimensions = dimensions;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _schemaEnsured, 1) != 0) return;

        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                name text PRIMARY KEY,
                description text NOT NULL,
                protocol text NOT NULL,
                mcp_id text NOT NULL,
                version text NOT NULL,
                embedding vector({_dimensions}) NOT NULL
            );
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            $"CREATE INDEX IF NOT EXISTS {IndexName} ON {TableName} USING hnsw (embedding vector_cosine_ops);",
            cancellationToken).ConfigureAwait(false);
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
        await ExecuteAsync($"""
            INSERT INTO {TableName} (name, description, protocol, mcp_id, version, embedding)
            VALUES (@name, @description, @protocol, @mcp_id, @version, @embedding)
            ON CONFLICT (name) DO UPDATE SET
                description = EXCLUDED.description,
                protocol = EXCLUDED.protocol,
                mcp_id = EXCLUDED.mcp_id,
                version = EXCLUDED.version,
                embedding = EXCLUDED.embedding;
            """, cancellationToken,
            ("@name", (object)document.Name),
            ("@description", (object)document.Description),
            ("@protocol", (object)document.Protocol),
            ("@mcp_id", (object)document.McpId),
            ("@version", (object)document.Version),
            ("@embedding", new Vector(embedding))).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync($"DELETE FROM {TableName} WHERE name = @name;", cancellationToken, ("@name", (object)name)).ConfigureAwait(false);
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

        await using var cmd = _dataSource.CreateCommand(
            $"SELECT name, description, protocol, mcp_id, version, embedding <=> @query AS distance " +
            $"FROM {TableName} ORDER BY distance LIMIT @k;");
        cmd.Parameters.AddWithValue("@query", new Vector(queryVector));
        cmd.Parameters.AddWithValue("@k", k);

        var hits = new List<McpServerHit>(k);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var doc = new McpServerDocument(
                Name: reader.GetString(0),
                Description: reader.GetString(1),
                Protocol: reader.GetString(2),
                McpId: reader.GetString(3),
                Version: reader.GetString(4));
            var distance = reader.IsDBNull(5) ? double.NaN : reader.GetDouble(5);
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
        await using var cmd = _dataSource.CreateCommand($"SELECT count(*) FROM {TableName};");
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    public void Dispose() => _dataSource.Dispose();

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
