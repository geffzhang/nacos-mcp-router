# nacos-mcp-router

一个基于 .NET 10 的 MCP Server，用于搜索、安装和代理注册在 Nacos 中的 MCP Server。

[![Model Context Protocol](https://img.shields.io/badge/Model%20Context%20Protocol-purple)](https://modelcontextprotocol.org)

<p>
<a href="./README.md">English</a> | <a href="./README_cn.md">简体中文</a>
</p>

## 概述

[Nacos](https://nacos.io) 提供面向云原生应用的服务发现、配置管理和服务管理能力。nacos-mcp-router 使用 Nacos 发现 MCP Server，并提供搜索、连接和代理 MCP Server 的工具。

当前仓库仅保留 .NET 10 实现。Router 使用 `streamable_http` 接收入站 MCP 连接，并支持代理下游 `stdio` 或 `mcp-streamable` 服务。

## 快速开始

### stdio

```bash
export NACOS_ADDR=127.0.0.1:8848
export NACOS_USERNAME=nacos
export NACOS_PASSWORD=nacos
bash src/scripts/download-model.sh  # 默认从国内镜像 ModelScope 下载；可用 HF_ENDPOINT 覆盖
dotnet run --project src/NacosMcpRouter
```

### streamable_http

```bash
export NACOS_ADDR=127.0.0.1:8848
export NACOS_USERNAME=nacos
export NACOS_PASSWORD=nacos
export TRANSPORT_TYPE=streamable_http
export PORT=8000
dotnet run --project src/NacosMcpRouter
```

MCP 端点位于 `http://localhost:8000/mcp`。

集群部署说明：

- `/mcp` 端点仅接受 MCP 2026-07-28 及之后的客户端，旧版客户端返回 400 / -32022。
- 上游 MCP Server 支持 `stdio` 或 `mcp-streamable` 协议，Router 入站仅支持 `streamable_http`。
- `mcp-streamable` 服务可在 `agentConfig.mcpServers.<name>.oauth` 中配置 OAuth。`client_credentials` 会自动获取 token；`authorization_code` 和 `device_code` 需要先在外部完成用户授权，再设置 `authorizationCode` 或 `deviceCode`。
- 从 Nacos 注册详情生成的远端服务可通过 `MCP_OAUTH_CONFIG` 注入 OAuth 配置。不要将 client secret 或 access token 写入源码或日志。
- 向量库默认使用 SonnetDB。集群部署时设置 `VECTOR_STORE=postgres`，并让所有实例连接同一个启用 pgvector 的 PostgreSQL 数据库。
- 如果旧 `.data` 目录包含 stdio Server 索引，请删除该目录并重启以重建索引。
- `GET /health` 返回 `200 {"status":"healthy"}`。

### Docker

```bash
docker build -t nacos-mcp-router-dotnet src
docker run -i --rm --network host \
  -e NACOS_ADDR=$NACOS_ADDR \
  -e NACOS_USERNAME=$NACOS_USERNAME \
  -e NACOS_PASSWORD=$NACOS_PASSWORD \
  nacos-mcp-router-dotnet
```

使用 `streamable_http` 时，请添加 `-p 8000:8000`，并设置 `TRANSPORT_TYPE=streamable_http` 和 `PORT=8000`。

容器首次启动时会从 ModelScope 下载 ONNX 模型（约 86 MB）。可挂载已准备好的模型目录以跳过下载：

```bash
docker run -i --rm --network host \
  -v "$PWD/models:/app/models" \
  -e NACOS_ADDR=$NACOS_ADDR \
  -e NACOS_USERNAME=$NACOS_USERNAME \
  -e NACOS_PASSWORD=$NACOS_PASSWORD \
  nacos-mcp-router-dotnet
```

## 工具

- `search_mcp_server(task_description, key_words)`
- `add_mcp_server(mcp_server_name)`
- `use_tool(mcp_server_name, mcp_tool_name, params)`

三个工具的完整协作流程请参阅 [MCP Router 工具协作 Skill](docs/skills/mcp-router-tools/SKILL.md)。

### OAuth 配置

```json
{
  "protocol": "mcp-streamable",
  "url": "https://example.test/mcp",
  "oauth": {
    "flow": "client_credentials",
    "clientId": "router-client",
    "clientSecret": "<secret>",
    "tokenUrl": "https://idp.test/oauth/token",
    "scopes": ["mcp.read"]
  }
}
```

用户授权流程使用相同结构：将 `flow` 改为 `authorization_code` 或 `device_code`，并配置对应的 `authorizationUrl` / `deviceAuthorizationUrl`。完成外部授权后填入 `authorizationCode` / `deviceCode`。

通过环境变量注入配置时，JSON 顶层键必须是 MCP Server 名称：

```bash
export MCP_OAUTH_CONFIG='{"weather":{"flow":"client_credentials","clientId":"router-client","clientSecret":"$MCP_CLIENT_SECRET","tokenUrl":"https://idp.test/oauth/token","scopes":["mcp.read"]}}'
```

## 环境变量

| 变量 | 说明 | 默认值 |
|---|---|---|
| `NACOS_ADDR` | Nacos 服务器地址 | `127.0.0.1:8848` |
| `NACOS_USERNAME` | Nacos 用户名 | `nacos` |
| `NACOS_PASSWORD` | Nacos 密码 | 空 |
| `NACOS_NAMESPACE` | Nacos 命名空间 ID | 空（`public`） |
| `ACCESS_KEY_ID` / `ACCESS_KEY_SECRET` | 可选的 Nacos AK/SK | 空 |
| `TRANSPORT_TYPE` | `streamable_http` | `streamable_http` |
| `PORT` | HTTP 监听端口 | `8000` |
| `EMBEDDING_MODEL_DIR` | ONNX 嵌入模型目录 | `./models/all-MiniLM-L6-v2` |
| `SONNETDB_DATA_DIR` | SonnetDB 数据目录 | `./.data/mcp-router` |
| `VECTOR_STORE` | `sonnetdb` 或 `postgres` | `sonnetdb` |
| `POSTGRES_CONNECTION_STRING` | 集群模式的 PostgreSQL 连接串 | `Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres` |
| `UPDATE_INTERVAL` | Nacos 刷新间隔（秒，最小值 10） | `60` |
| `SEARCH_MIN_SIMILARITY` | 0 到 1 的向量检索阈值 | `0.2` |
| `SEARCH_RESULT_LIMIT` | 搜索结果最大数量 | `10` |

模型下载脚本还会读取以下变量：

| 变量 | 说明 | 默认值 |
|---|---|---|
| `HF_ENDPOINT` | 模型镜像地址 | `https://www.modelscope.cn` |
| `HF_REPO` | 模型仓库 | `sentence-transformers/all-MiniLM-L6-v2` |
| `HF_BRANCH` | 仓库分支 | ModelScope 使用 `master`，Hugging Face 使用 `main` |

## 架构

详见 [.NET Router 设计文档](docs/superpowers/specs/2026-09-14-dotnet-mcp-router-design.md)。

## 许可证

nacos-mcp-router 使用 Apache 2.0 许可证，详情请参阅 [LICENSE](LICENSE)。
