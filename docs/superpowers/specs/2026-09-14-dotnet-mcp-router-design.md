# .NET 10 Nacos MCP Router 设计

**日期**: 2026-09-14
**作者**: brainstorming session
**状态**: 待用户审阅

## 背景与目标

`nacos-mcp-router` 是一个通过 Nacos 做服务发现并代理其他 MCP server 的 MCP (Model Context Protocol) Server。本设计文档描述仓库唯一保留的 **.NET 10 实现**, 让 .NET 生态 (Claude Desktop、Visual Studio、Rider 等 MCP 客户端) 可以直接接入。

### 目标

1. 在 `src/dotnet/` 下交付一个 `net10.0` 控制台应用, 暴露三个工具 (`search_mcp_server`, `add_mcp_server`, `use_tool`) 与 router 模式。
2. 通过 NuGet 引用 `RedNb.Nacos.All` 2.0.0+ 与 Nacos 3.2.4 通信, 通过 `Microsoft.Extensions.VectorData` 兼容包 `SonnetDB.Data` 持久化向量。
3. 嵌入式 ONNX (`all-MiniLM-L6-v2`, 384 维) 生成本地 embedding, 无外部 API 依赖。
4. 入站仅支持 `streamable_http`; router 模式下出站连接下游 MCP server 支持 `stdio` 与 `mcp-streamable`。

### 非目标 (v1)

- SSE 传输 (上游已标记 deprecated, 推迟到 v2 再讨论)。
- Proxy 模式 (即把另一个 MCP server 转换成 streamable_http 的反向代理场景)。
- 工具元数据回写到 Nacos (`update_mcp_tools` 流程) — 推迟到 v2。
- Nacos AI Embedding Provider — 等待 Nacos 3.x 服务端能力成熟。
- 远程 embedding API。
- `nacos-mcp-router` 名字的 NuGet 公开发布 — v1 仅本地打包, 不 push 到 nuget.org。

## 仓库结构

```
nacos-mcp-router/
├── src/
│   └── dotnet/                (唯一实现)
│       ├── NacosMcpRouter.sln
│       ├── src/
│       │   └── NacosMcpRouter/
│       │       ├── NacosMcpRouter.csproj
│       │       ├── Program.cs
│       │       ├── Configuration/
│       │       │   ├── RouterOptions.cs
│       │       │   └── EnvConfigurator.cs
│       │       ├── Nacos/
│       │       │   ├── NacosMcpRegistry.cs
│       │       │   └── McpServerEntry.cs
│       │       ├── Embeddings/
│       │       │   ├── IEmbeddingGenerator.cs
│       │       │   ├── OnnxEmbeddingGenerator.cs
│       │       │   └── ModelPaths.cs
│       │       ├── VectorStore/
│       │       │   ├── ISonnetDbVectorStore.cs
│       │       │   ├── SonnetDbVectorStore.cs
│       │       │   ├── McpServerDocument.cs
│       │       │   └── SchemaInitializer.cs
│       │       ├── Mcp/
│       │       │   ├── McpRouterTools.cs
│       │       │   ├── McpProxyClientFactory.cs
│       │       │   ├── McpProxyRegistry.cs
│       │       │   └── ToolFilter.cs
│       │       ├── Hosting/
│       │       │   └── McpRouterHostedService.cs
│       │       └── Logging/
│       │           └── RouterLogger.cs
│       ├── tests/
│       │   └── NacosMcpRouter.Tests/
│       │       ├── NacosMcpRouter.Tests.csproj
│       │       ├── Tools/SearchToolTests.cs
│       │       ├── Tools/AddMcpServerToolTests.cs
│       │       ├── Tools/UseToolToolTests.cs
│       │       ├── VectorStore/SonnetDbVectorStoreTests.cs
│       │       ├── Embeddings/OnnxEmbeddingGeneratorTests.cs
│       │       ├── Configuration/EnvConfiguratorTests.cs
│       │       └── TestHelpers/
│       ├── scripts/
│       │   └── download-model.sh   (PowerShell 版本: download-model.ps1)
│       ├── Dockerfile
│       ├── README.md
│       └── README_cn.md
├── docs/
│   └── superpowers/
│       └── specs/
│           └── 2026-09-14-dotnet-mcp-router-design.md  (本文档)
├── README.md
└── README_cn.md
```

主仓 `README.md` 只提供 .NET 10 的运行命令与 Docker 镜像说明。

## 架构

### 分层 (单程序集内部, 靠文件夹与命名空间划分)

| 层 | 命名空间 | 职责 |
| --- | --- | --- |
| 入口 | `NacosMcpRouter` | `Program.cs` 读 env, 选 stdio 或 streamable_http, 启动 host |
| 配置 | `NacosMcpRouter.Configuration` | `RouterOptions`, `NacosOptions`, env → options 映射 |
| MCP 服务 | `NacosMcpRouter.Mcp` | `McpRouterTools` (三个 `[McpServerTool]`), `McpProxyRegistry`, `McpProxyClientFactory`, `ToolFilter` |
| Nacos | `NacosMcpRouter.Nacos` | `NacosMcpRegistry` (包装 `IAiService`), `McpServerEntry` |
| 向量 | `NacosMcpRouter.VectorStore` | `SonnetDbVectorStore` (Document Collection CRUD + `vector_search`), `McpServerDocument`, `SchemaInitializer` |
| Embedding | `NacosMcpRouter.Embeddings` | `OnnxEmbeddingGenerator` (ONNX all-MiniLM-L6-v2, 384 维) |
| 后台 | `NacosMcpRouter.Hosting` | `McpRouterHostedService` 负责启动顺序与优雅停止 |
| 日志 | `NacosMcpRouter.Logging` | `RouterLogger` 包装 `ILogger<T>`, 统一日志格式 |

### 启动流程

```
Program.Main(args)
  ↓ EnvConfigurator.LoadFromEnvironment() → RouterOptions
  ↓ 校正 UpdateIntervalSeconds (>= 10), SearchMinSimilarity ([0,1])
  ↓ Host.CreateApplicationBuilder(args)
  ↓ 注册 DI (见 §组件契约)
  ↓ builder.Build()
  ↓ await host.RunAsync()
       └─ McpRouterHostedService.StartAsync:
            1. SonnetDbVectorStore.EnsureSchemaAsync
               - CREATE VECTOR INDEX idx_mcp_embedding ON mcp_servers ('$.embedding')
                 WITH (dimensions=384, metric='cosine', m=16, ef_construction=200, ef_search=64)
            2. 若 SonnetDbVectorStore.IsEmptyAsync() → BackfillIfEmptyAsync
               - 拉取 Nacos 全量 MCP server, 过滤 enabled && protocol∈{stdio,mcp-streamable}
               - 对每个生成 embedding 并 UpsertAsync
            3. NacosMcpRegistry.StartAsync → 启动 RefreshLoop (PeriodicTimer)
       └─ McpServerHost:
            - 注册三个 [McpServerTool]
            - 根据 RouterOptions.Transport:
                Stdio          → WithStdioServerTransport().RunAsync()
                StreamableHttp → WithHttpTransport().RunAsync() (ASP.NET Core, listen PORT)
```

### 数据流

#### `search_mcp_server(task_description, key_words)`

1. 拆分 `key_words` 为关键词数组 `keywords[]` (按逗号, 去空白)。
2. 并发 `NacosMcpRegistry.SearchByKeywordAsync(kw)` 收集结果 `R1`。
3. 若 `R1.Count < 5`:
   - `IEmbeddingGenerator.GenerateAsync(task_description)` → `queryVector` (384 维)。
   - `ISonnetDbVectorStore.SearchAsync(queryVector, k=5-R1.Count)` → `R2`, 过滤 `distance <= 1 - SearchMinSimilarity`。
4. `R = (R1 ∪ R2).DistinctBy(name).Take(SearchResultLimit)`。
5. 返回结构稳定的 Markdown 文本, 供 MCP 客户端直接展示。

#### `add_mcp_server(mcp_server_name)`

1. `NacosMcpRegistry.GetByNameAsync(name)` → `entry`。
   - `null` → 返回 `"{name} is not found, use search_mcp_server to get mcp servers"`。
2. 读取 `entry.McpConfigDetail.ToolSpec.ToolsMeta`, 构造 `disabledTools` 集合。
3. `McpProxyRegistry.EnsureConnectedAsync(entry)`:
   - 缓存命中且 `IMcpClient.PingAsync()` 成功 → 复用。
   - 否则 `McpProxyClientFactory.CreateAsync(entry.AgentConfig["mcpServers"][name])`:
     - `protocol=stdio` → `McpClient.CreateAsync(new StdioClientTransport { Command, Arguments, EnvironmentVariables })`
     - `protocol=mcp-streamable` → `McpClient.CreateAsync(new HttpClientTransport(new HttpClient { BaseAddress = new Uri(url) }, new HttpClientTransportOptions { TransportMode = HttpTransportMode.StreamableHttp }))`
     - `protocol=mcp-sse` → 抛 `NotSupportedException("sse protocol not supported in v1")` (返回 `{name} 安装失败: ...`)
4. `client.ListToolsAsync()` → 原始工具列表。
5. `ToolFilter.Apply(tools, disabledTools, entry.McpConfigDetail.ToolSpec.ToolsDict)` → 过滤并覆盖 description / inputSchema。
6. 返回 Markdown:
   `"1. {name}安装完成, tool 列表为: {json.dumps(filteredTools)}\n2. {name}的工具需要通过nacos-mcp-router的use_tool工具代理使用"`。

#### `use_tool(mcp_server_name, mcp_tool_name, params)`

1. `McpProxyRegistry.TryGet(name)` → 若 null 返回 `"mcp server not found, use search_mcp_server..."`。
2. `params` 是 JSON 字符串, 用 `JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(params)` 转回参数; 若解析失败返回 `"Error: use_tool params must be a JSON object"`。
3. `client.CallToolAsync(mcpToolName, arguments)` → `CallToolResult`。
4. 把 `result.Content` 中的每个 `TextContentBlock` 的 `Text` 拼成 `\n` 分隔的字符串返回; 非 text 类型 (image / audio / embedded resource) 暂降级为占位符 `[unsupported content type: {type}]`。

### 后台刷新循环

```
NacosMcpRegistry.RefreshLoop:
  PeriodicTimer(Interval = max(UpdateIntervalSeconds, 10)s)
  循环:
    try:
      1. ListMcpServersAsync(search='blur', pageNo=1, pageSize=100) 全量分页拉取
      2. 过滤 enabled=true 且 protocol∈{stdio,mcp-streamable}
      3. 对每个 server 调用 GetMcpServerAsync(name) 拿详情
      4. 与本地缓存 diff:
         - 新增 / description 变化 → 生成 embedding → UpsertAsync
         - 删除 → DeleteAsync
         - 变化但 description 未变 → 仅更新元数据
      5. 记录 (新增 N, 删除 M, 更新 K)
    catch (Exception ex):
      logger.LogWarning(ex, "refresh loop iteration failed")
      继续 (不停循环)
```

### 错误处理

| 场景 | 行为 |
| --- | --- |
| 启动时 Nacos 连不上 | `McpRouterHostedService.StartAsync` 抛异常, host 退出码非零 |
| 启动时 SonnetDB 路径不可写 | 抛异常, host 退出码非零 |
| 启动时 ONNX 模型文件缺失 | `OnnxEmbeddingGenerator` 构造器抛 `FileNotFoundException` 并打印 `EMBEDDING_MODEL_DIR` 路径, 提示运行 `scripts/download-model.sh` |
| `search_mcp_server` 中向量搜索失败 | 降级到仅 keyword 结果, `logger.LogWarning` |
| `add_mcp_server` 中 MCP 子进程启动失败 | 返回 `"{name} 安装失败: {error}"`, 不抛 |
| `add_mcp_server` 收到 `mcp-sse` 协议 | 返回 `"{name} 安装失败: sse protocol not supported in v1"` |
| `use_tool` 中目标 server 未缓存 | 返回 `"mcp server not found, use search_mcp_server..."` |
| 任意工具调用未捕获异常 | 返回 `"Error: {message}"`, 记录 warning, 不污染其他请求 |

## 组件契约

### `RouterOptions`

```csharp
public sealed class RouterOptions
{
    public const string SectionName = "NacosMcpRouter";
    public NacosOptions Nacos { get; init; } = new();
    public McpTransport Transport { get; init; } = McpTransport.StreamableHttp;
    public string EmbeddingModelDir { get; init; } = "./models/all-MiniLM-L6-v2";
    public string SonnetDbDataDir { get; init; } = "./.data/mcp-router";
    public int UpdateIntervalSeconds { get; init; } = 60;        // 下限 10
    public double SearchMinSimilarity { get; init; } = 0.5;      // 0.0-1.0
    public int SearchResultLimit { get; init; } = 10;
    public int HttpPort { get; init; } = 8000;
}

public enum McpTransport { Stdio, StreamableHttp }
```

env 变量名:

| env | 默认 | 说明 |
| --- | --- | --- |
| `NACOS_ADDR` | `127.0.0.1:8848` | 必填 (router 模式) |
| `NACOS_USERNAME` | `nacos` | 必填 (router 模式) |
| `NACOS_PASSWORD` | 空 | 必填 (router 模式) |
| `NACOS_NAMESPACE` | 空 | 可选 |
| `ACCESS_KEY_ID` | 空 | 可选 (阿里云 RAM) |
| `ACCESS_KEY_SECRET` | 空 | 可选 |
| `TRANSPORT_TYPE` | `streamable_http` | `streamable_http` |
| `MODE` | `router` | v1 仅 router |
| `PORT` | `8000` | streamable_http 端口 |
| `EMBEDDING_MODEL_DIR` | `./models/all-MiniLM-L6-v2` | ONNX 模型目录 |
| `SONNETDB_DATA_DIR` | `./.data/mcp-router` | SonnetDB 数据目录 |
| `UPDATE_INTERVAL` | `60` | 下限 10 (秒) |
| `SEARCH_MIN_SIMILARITY` | `0.5` | 0.0-1.0 |
| `SEARCH_RESULT_LIMIT` | `10` | |

### `INacosMcpRegistry`

```csharp
public interface INacosMcpRegistry
{
    Task<IReadOnlyList<McpServerEntry>> SearchByKeywordAsync(string keyword, CancellationToken ct);
    Task<McpServerEntry?> GetByNameAsync(string name, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

实现要点:
- 通过 `RedNb.Nacos.DependencyInjection.AddNacos` 注册的 `IAiService` 调用 `ListMcpServersAsync`, `GetMcpServerAsync`。
- `SearchByKeywordAsync` 使用 `search: "accurate"`。
- 启动时通过 `IHostedService.StartAsync` 触发后台循环。
- `McpServerEntry` 字段映射:
  - `Name: string`
  - `Description: string`
  - `AgentConfig: Dictionary<string, object>` — 形如 `{"mcpServers": { "<name>": { "command": "...", "args": [...], "env": {...}, "headers": {...}, "protocol": "stdio|mcp-streamable|mcp-sse" } }}`, 由 `McpProxyClientFactory` 解析后构造 `IMcpClient`。
  - `McpConfigDetail: McpConfigDetail` (内部 record) — 含 `Protocol: string`, `ToolSpec: ToolSpec?` (`Tools: List<McpTool>`, `ToolsMeta: Dictionary<string, ToolMeta>`, `ToolsDict: Dictionary<string, ToolInfo>`), `RemoteServerConfig: RemoteServerConfig?`, `BackendEndpoints: List<BackendEndpoint>`。
  - `Id: string` (空时为 mcp_name 查询)
  - `Version: string` (默认 `"0.0.0"`)

### `ISonnetDbVectorStore`

```csharp
public interface ISonnetDbVectorStore
{
    Task EnsureSchemaAsync(CancellationToken ct);
    Task UpsertAsync(McpServerDocument doc, float[] embedding, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<(McpServerDocument Doc, double Distance)>> SearchAsync(
        float[] queryVector, int k, CancellationToken ct);
    Task<bool> IsEmptyAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
```

实现要点:
- 嵌入式: `new SndbConnection($"Data Source={RouterOptions.SonnetDbDataDir}")`。
- `EnsureSchemaAsync` 通过 ADO.NET 执行 `CREATE VECTOR INDEX ...`; 失败时 `McpRouterHostedService` 重试 3 次后退出。
- 所有 SQL 使用 `SonnetDB.Data` 参数绑定 (`?` 或 `@name`), 禁止字符串拼接。
- `McpServerDocument` 序列化为 JSON 写入 Document Collection 字段; name 作为 `$id`。
- `SearchAsync` SQL:
  ```sql
  SELECT id,
         json_value(document, '$.name') AS name,
         json_value(document, '$.description') AS description,
         json_value(document, '$.protocol') AS protocol,
         json_value(document, '$.mcp_id') AS mcp_id,
         vector_distance() AS distance
  FROM vector_search(
      source => mcp_servers,
      vector_field => '$.embedding',
      vector => @q,
      k => @k,
      metric => 'cosine'
  )
  ORDER BY distance;
  ```

### `IEmbeddingGenerator`

```csharp
public interface IEmbeddingGenerator
{
    int Dimensions { get; }   // 固定 384
    Task<float[]> GenerateAsync(string text, CancellationToken ct);
    Task<float[][]> GenerateBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
```

实现要点 (`OnnxEmbeddingGenerator`):

参照 `E:\GitHub\openclaw.net\src\OpenClaw.Routing.Onnx\LocalOnnxEmbeddingGenerator.cs` 的成熟模式, 拆成两层:

- **公开类 `OnnxEmbeddingGenerator`** (`IEmbeddingGenerator`, `IDisposable`):
  - 构造时校验 `EmbeddingModelDir` 下存在 `model.onnx` 与 `tokenizer.json`; 缺文件抛 `FileNotFoundException` 并打印 `EMBEDDING_MODEL_DIR` 路径, 提示运行 `scripts/download-model.sh`。
  - 持有一个内部 `IEmbeddingModelRunner` (`OnnxEmbeddingModelRunner` 实现) 与 `Tokenizer`。
  - 暴露 `GenerateAsync(text, ct)`: 用 `Task.Run` 包裹推理避免阻塞 IO 线程。
  - 实现 `Dispose`: 释放 runner, 清理 tokenizer 临时工作目录。

- **内部类 `OnnxEmbeddingModelRunner`** (`IDisposable`):
  - 包装 `Microsoft.ML.OnnxRuntime.InferenceSession`, 启动时缓存 `InputMetadata.Keys` 与 `OutputMetadata.Keys`。
  - `Run(inputs, attentionMask, ct)`: 通过 `RunOptions.Terminate = true` 监听 `CancellationToken`, 捕获 `OnnxRuntimeException` 后转换为 `OperationCanceledException`。
  - 输出解析 (`ExtractEmbedding`) 优先取直接 embedding (rank 1 或 rank-2 `[1, dim]`), 否则对 rank-3 hidden states 用 attention mask 做 mean-pooling, 最后 `NormalizeDimensions` 校验维度与配置一致。
  - mean-pooling: 累加 `attention_mask=1` 的 token, 再除以 token 数, 处理 `tokenCount <= 0` 退化为 1。

- **tokenizer 加载** (`HuggingFaceTokenizerLoader` 内部静态类, 仿 OpenClaw):
  - 直接读 `tokenizer.json`, 解析 `model.type` 决定走 BPE 还是 WordPiece 路径。
  - BPE 路径: 提取 `model.vocab`, `model.merges`, `model.unk_token`, `model.continuing_subword_prefix`, `model.end_of_word_suffix`; 写临时 `vocab.json` + `merges.txt` 给 `BpeTokenizer.Create(...)`, pre-tokenizer 根据 `pre_tokenizer.type` (`ByteLevel`/`Roberta`/`WhiteSpace`/`BertPreTokenizer`/`Sequence`) 选择 `RobertaPreTokenizer.Instance` 或 `PreTokenizer.CreateWhiteSpace(...)`。
  - WordPiece 路径: 把 `model.vocab` JSON object 展平成 `vocab.txt` 行文件, 用 `BertTokenizer.Create(vocabPath)`。
  - 临时目录放在 `%TEMP%/nacos-mcp-router-tokenizers/{guid}`, 失败时清理。

- **输入 tensor 名动态探测** (`FindRequiredInputName` / `FindOptionalInputName`):
  - 必选: `input_ids` / `inputIds` / `ids` / `input` / `tokens` / `token_ids`。
  - 可选: `attention_mask` / `attentionMask` / `mask`, `token_type_ids` / `tokenTypeIds`。
  - 大小写不敏感, 取首个匹配。

- **行为约束**:
  - `tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true)`.
  - 截断到 512 token 上限 (BERT 类硬限制)。
  - token 列表为空时返回零向量 (避免用 id=0 PAD token 的不可控 embedding)。
  - `InferenceSession` 单例, 并发复用 (ONNX Runtime 线程安全)。

### `IMcpProxyRegistry` + `McpProxyClientFactory`

```csharp
public interface IMcpProxyRegistry
{
    Task<McpProxyEntry> EnsureConnectedAsync(McpServerEntry entry, CancellationToken ct);
    McpProxyEntry? TryGet(string name);
    Task DisposeAllAsync();
}

public sealed record McpProxyEntry(
    string Name,
    IMcpClient Client,
    IReadOnlyList<McpToolDescriptor> Tools,
    string Version);
```

实现要点:
- 内部 `ConcurrentDictionary<string, McpProxyEntry>`。
- `EnsureConnectedAsync`:
  - 缓存命中 → `client.PingAsync()`, 失败则 `DisposeAsync()` + 重建。
  - 缓存未命中 → `McpProxyClientFactory.CreateAsync(serverConfig)` → `client.InitializeAsync()` → 存 entry。
- `DisposeAllAsync`: 在 `IHostedService.StopAsync` 中调用, 终止 stdio 子进程, 释放 `HttpClient`。

### `McpRouterTools`

```csharp
public sealed class McpRouterTools
{
    [McpServerTool(Name = "search_mcp_server", Description = "...")]
    public async Task<string> SearchMcpServer(
        [Description("用户中文和英文任务描述...")] string task_description,
        [Description("英文逗号分隔的关键词, 最多4个")] string key_words,
        CancellationToken ct);

    [McpServerTool(Name = "add_mcp_server", Description = "...")]
    public async Task<string> AddMcpServer(
        [Description("MCP Server 名称")] string mcp_server_name,
        CancellationToken ct);

    [McpServerTool(Name = "use_tool", Description = "...")]
    public async Task<string> UseTool(
        [Description("目标 MCP Server 名称")] string mcp_server_name,
        [Description("目标工具名")] string mcp_tool_name,
        [Description("工具参数 JSON 字符串")] string @params,
        CancellationToken ct);
}
```

### DI 注册

```csharp
builder.Services
    .Configure<RouterOptions>(o => EnvConfigurator.Apply(o))
    .AddSingleton<NacosOptions>(sp => sp.GetRequiredService<IOptions<RouterOptions>>().Value.Nacos)
    .AddNacos(o =>
    {
        var n = builder.Configuration.GetSection("NacosMcpRouter:Nacos").Get<NacosOptions>()!;
        o.ServerAddresses = n.ServerAddresses;
        o.Username = n.Username;
        o.Password = n.Password;
        o.Namespace = n.Namespace;
        o.AccessKeyId = n.AccessKeyId;
        o.AccessKeySecret = n.AccessKeySecret;
    })
    .AddSingleton<IEmbeddingGenerator, OnnxEmbeddingGenerator>()
    .AddSingleton<ISonnetDbVectorStore, SonnetDbVectorStore>()
    .AddSingleton<INacosMcpRegistry, NacosMcpRegistry>()
    .AddSingleton<IMcpProxyRegistry, McpProxyRegistry>()
    .AddSingleton<McpProxyClientFactory>()
    .AddHostedService<McpRouterHostedService>();
```

## 依赖项 (NuGet, 锁定主要次版本)

| 包 | 版本 | 用途 |
| --- | --- | --- |
| `ModelContextProtocol` | `2.2.0+` | MCP server + client 核心 |
| `ModelContextProtocol.AspNetCore` | `2.2.0+` | streamable_http 传输 (`WithHttpTransport()` 等 ASP.NET Core 集成) |
| `RedNb.Nacos.All` | `2.0.0+` | Nacos 客户端 + DI |
| `SonnetDB` | `3.1.0` | ADO.NET 驱动 |
| `Microsoft.Extensions.AI` | `10.7.0` | Embedding 抽象 (与 OpenClaw 对齐) |
| `Microsoft.ML.OnnxRuntime` | `1.27.0` | ONNX Runtime 原生推理 (`InferenceSession` 加载 `model.onnx`) |
| `Microsoft.ML.Tokenizers` | `2.0.0` | 加载 Hugging Face `tokenizer.json` (经内部 `HuggingFaceTokenizerLoader` 适配 BPE / WordPiece + pre-tokenizer) |
| `Microsoft.Extensions.Hosting` | `10.0.12` | 通用 Host |
| `Microsoft.AspNetCore.App` | framework reference (`net10.0`) | ASP.NET Core 运行时, 由 `ModelContextProtocol.AspNetCore` 隐式拉入 |

> 注: `Microsoft.AspNetCore.App` 不通过 `<PackageReference>` 显式添加, 而是在 csproj 中以 `<FrameworkReference Include="Microsoft.AspNetCore.App" />` 引用, 确保运行时与 SDK 版本一致。`ModelContextProtocol.AspNetCore` 会拉入相同版本的 MVC / SignalR / Kestrel 等组件, 不必单独声明。

dev/test:

| 包 | 用途 |
| --- | --- |
| `xunit`, `xunit.runner.visualstudio` | 单元测试 |
| `FluentAssertions` | 断言 |
| `Moq` 或 `NSubstitute` | mocking |
| `Testcontainers` | Nacos / SonnetDB 容器 |
| `coverlet.collector` | 覆盖率 |

## 测试策略

### 单元测试 (xUnit + FluentAssertions)

- `SonnetDbVectorStoreTests`: 临时目录嵌入式连接, 每次清理。覆盖 Upsert → Search 命中, 排序正确, Delete 后不再命中, 空 collection Search 返回空。
- `OnnxEmbeddingGeneratorTests`: 用 `SKIP_MODEL_TESTS=1` 在 CI 默认跳过, 本地完整跑。断言 `Dimensions == 384`, 同一文本两次 embedding 完全相同, 不同文本 cosine 距离 > 0。
- `McpProxyRegistryTests`: 用 `Substitute.For<IMcpClient>` 验证缓存命中 / 重建 / Dispose 路径。
- `McpRouterToolsTests`: 三个工具方法的 happy path + 失败回退。
- `EnvConfiguratorTests`: 边界值 (空字符串, 负数 interval, 相似度 > 1) 校正行为。

### 集成测试 (Testcontainers)

- `NacosIntegrationTests`: 拉起 `nacos/nacos-server:v3.2.4` 容器, 通过 `IAiService` 预置若干 MCP server, 启动路由器 in-process, 断言 `search_mcp_server` 命中。
- `SonnetDbIntegrationTests`: 嵌入式跑 1000 条向量的写入 + `vector_search` 性能 / 召回。

### E2E (Playwright + MCP Inspector)

参考 TS 实现的 `scripts/run-mcp-inspector-e2e.sh`:
- 启动 `dotnet run --project src/dotnet/src/NacosMcpRouter` (stdio 模式)。
- 使用 MCP Inspector CLI 通过 stdio 调三个工具。
- Playwright 断言: 工具列表正确, `search_mcp_server` 返回 Markdown, `add_mcp_server` 后 `use_tool` 能 ping 通。

### 模型下载脚本

`src/dotnet/scripts/download-model.sh` (Linux/macOS) + `download-model.ps1` (Windows):
- 从 Hugging Face `sentence-transformers/all-MiniLM-L6-v2` 下载 `model.onnx`, `tokenizer.json`, `vocab.txt` 到 `models/all-MiniLM-L6-v2/`。
- README 标注: 首次运行需先执行。

### 覆盖率门槛

`coverlet.collector` 配 `threshold: line=70, branch=60`, `McpRouterTools` / `NacosMcpRegistry.RefreshLoop` 强制 ≥ 85%。

## 打包与发布

### NuGet

- `dotnet pack -c Release`, 包名 `NacosMcpRouter`, 目标 `net10.0`, 生成 `nupkg` + `snupkg`。
- v1 不 push 到 nuget.org; 需要时手动 `dotnet nuget push`。

### Docker

多阶段 `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/dotnet/src/NacosMcpRouter -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish ./
COPY models ./models
ENV EMBEDDING_MODEL_DIR=/app/models/all-MiniLM-L6-v2
ENV SONNETDB_DATA_DIR=/app/.data/mcp-router
ENV TRANSPORT_TYPE=streamable_http
ENTRYPOINT ["dotnet", "NacosMcpRouter.dll"]
EXPOSE 8000
```

镜像名预留: `nacos/nacos-mcp-router-dotnet:latest` (实际发布前再确认)。

### 文档

- `src/dotnet/README.md` + `src/dotnet/README_cn.md`。
- 主仓 `README.md` / `README_cn.md` 追加 .NET 章节。

## 风险与未决事项

1. **`ModelContextProtocol` 2.2.0 接口**: 锁定到 `ModelContextProtocol` 2.2.0+ 与 `ModelContextProtocol.AspNetCore` 2.2.0+; 该包仍在迭代, 后续 minor 升级需回归测试三个 tool 方法 + 客户端出站连接。
2. **`SonnetDB` 3.1.0 API 稳定性**: 锁到 `SonnetDB` 3.1.0。本设计直接走 ADO.NET (`SndbConnection` + `vector_search` SQL), 不依赖 `Microsoft.Extensions.VectorData` adapter, 避免 SonnetDB M35 多模态补完过程中的 API 漂移; 但若 3.1.x minor 升级改动 `vector_search` 函数签名 (参数名 / 标量返回类型), 需跟着调整 `SonnetDbVectorStore.SearchAsync`。
3. **Nacos AK/SK 鉴权**: RedNb.Nacos 2.x 显式拒绝纯 AK/SK 登录, 但保留了 username/password 兜底, 因此同时保留两组 env 变量。
4. **大文档集性能**: 1000+ MCP server 场景下 vector_search 召回与延迟需要集成测试验证; SonnetDB 文档承诺 HNSW + 精确回退, 暂信任。
5. **ONNX embedding 复现**: ONNX 实现直接复刻 `OpenClaw.Routing.Onnx.LocalOnnxEmbeddingGenerator` 的成熟模式 (`E:\GitHub\openclaw.net\src\OpenClaw.Routing.Onnx`), 实施时直接对照改写, 不重新设计 tokenizer 加载路径。

## 验收标准

1. `dotnet build` 在仓库根与 `src/dotnet/` 下零警告通过。
2. `dotnet test` 在 `src/dotnet/tests/` 下通过, 覆盖率门槛达成。
3. `dotnet run --project src/dotnet/src/NacosMcpRouter` 在本地 Nacos + 预置 MCP server 环境下能完整跑通三个工具。
4. Docker 镜像构建成功, 容器内 `stdio` 模式可被 Claude Desktop / MCP Inspector 调用。
5. README / README_cn 文档完整, 主仓 README 包含 .NET 章节。

## 实施步骤 (高层)

详细实施计划由后续 `superpowers:writing-plans` skill 输出; 高层步骤:

1. 初始化 `src/dotnet/` 解决方案与项目, 配 csproj 与 NuGet 依赖。
2. 落 `Configuration.RouterOptions` + `EnvConfigurator` + 单测。
3. 落 `Embeddings.OnnxEmbeddingGenerator` + `models/` 下载脚本。
4. 落 `VectorStore.SonnetDbVectorStore` + `McpServerDocument` + `SchemaInitializer` + 集成测试。
5. 落 `Nacos.NacosMcpRegistry` + `McpServerEntry` + RefreshLoop。
6. 落 `Mcp.McpRouterTools` + `McpProxyRegistry` + `McpProxyClientFactory` + `ToolFilter`。
7. 落 `Hosting.McpRouterHostedService` + `Program.cs` 入口, 串联 stdio 与 streamable_http。
8. 编写 Dockerfile + README (中英)。
9. E2E (Playwright + MCP Inspector)。
10. 提交 commit, 更新主仓 README。
