namespace NacosMcpRouter.VectorStore;

public sealed class SchemaInitializer
{
    private readonly IVectorStore _store;
    public SchemaInitializer(IVectorStore store) => _store = store;

    public Task EnsureAsync(CancellationToken cancellationToken = default) =>
        _store.EnsureSchemaAsync(cancellationToken);
}
