# nacos-mcp-router

A .NET 10 MCP server that searches, installs, and proxies MCP servers registered in Nacos.

[![Model Context Protocol](https://img.shields.io/badge/Model%20Context%20Protocol-purple)](https://modelcontextprotocol.org)

<p>
<a href="./README.md">English</a> | <a href="./README_cn.md">简体中文</a>
</p>

## Overview

[Nacos](https://nacos.io) provides service discovery, configuration management, and service management for cloud-native applications. nacos-mcp-router uses Nacos to discover MCP servers and exposes tools for searching, connecting to, and proxying them.

The supported implementation is .NET 10. The router accepts MCP connections over `streamable_http` and can proxy downstream `stdio` or `mcp-streamable` servers.

## Quick Start

### stdio

```bash
export NACOS_ADDR=127.0.0.1:8848
export NACOS_USERNAME=nacos
export NACOS_PASSWORD=nacos
bash src/scripts/download-model.sh  # Defaults to ModelScope (CN mirror); override with HF_ENDPOINT
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

The MCP endpoint is available at `http://localhost:8000/mcp`.

Cluster deployment notes:

- `/mcp` only accepts MCP 2026-07-28+ clients (older clients get 400 / -32022).
- Upstream MCP servers may use `stdio` or `mcp-streamable`; the router itself accepts inbound connections over `streamable_http` only.
- `mcp-streamable` services may define OAuth under `agentConfig.mcpServers.<name>.oauth`. `client_credentials` acquires tokens automatically; `authorization_code` and `device_code` require completing user authorization externally and then setting `authorizationCode` or `deviceCode`. While authorization is pending, `use_tool` returns verification instructions instead of blocking indefinitely.
- For services created from Nacos registration details, set `MCP_OAUTH_CONFIG` to a JSON object keyed by MCP server name. Keep client secrets out of source control and logs.
- Vector storage defaults to SonnetDB. For a cluster, set `VECTOR_STORE=postgres` and point every instance at the same PostgreSQL database with pgvector.
- If an old `.data` directory contains stdio server indexes, delete it and restart to rebuild the indexes.
- `GET /health` returns `200 {"status":"healthy"}`.

### Docker

```bash
docker build -t nacos-mcp-router-dotnet src
docker run -i --rm --network host \
  -e NACOS_ADDR=$NACOS_ADDR \
  -e NACOS_USERNAME=$NACOS_USERNAME \
  -e NACOS_PASSWORD=$NACOS_PASSWORD \
  nacos-mcp-router-dotnet
```

For `streamable_http`, add `-p 8000:8000` and set `TRANSPORT_TYPE=streamable_http` and `PORT=8000`.

On first start the container downloads the ONNX model (about 86 MB) from ModelScope. Mount a pre-populated model directory to skip the download:

```bash
docker run -i --rm --network host \
  -v "$PWD/models:/app/models" \
  -e NACOS_ADDR=$NACOS_ADDR \
  -e NACOS_USERNAME=$NACOS_USERNAME \
  -e NACOS_PASSWORD=$NACOS_PASSWORD \
  nacos-mcp-router-dotnet
```

## Tools

- `search_mcp_server(task_description, key_words)`
- `add_mcp_server(mcp_server_name)`
- `use_tool(mcp_server_name, mcp_tool_name, params)`

See [the MCP Router tools workflow Skill](docs/skills/mcp-router-tools/SKILL.md) for the complete discovery, installation, and invocation flow.

## Environment Variables

| Variable | Description | Default |
|---|---|---|
| `NACOS_ADDR` | Nacos server address | `127.0.0.1:8848` |
| `NACOS_USERNAME` | Nacos username | `nacos` |
| `NACOS_PASSWORD` | Nacos password | empty |
| `NACOS_NAMESPACE` | Nacos namespace ID | empty (`public`) |
| `ACCESS_KEY_ID` / `ACCESS_KEY_SECRET` | Optional Nacos AK/SK | empty |
| `TRANSPORT_TYPE` | `streamable_http` | `streamable_http` |
| `PORT` | HTTP listening port | `8000` |
| `EMBEDDING_MODEL_DIR` | ONNX embedding model directory | `./models/all-MiniLM-L6-v2` |
| `SONNETDB_DATA_DIR` | SonnetDB data directory | `./.data/mcp-router` |
| `VECTOR_STORE` | `sonnetdb` or `postgres` | `sonnetdb` |
| `POSTGRES_CONNECTION_STRING` | PostgreSQL connection string for cluster mode | `Host=localhost;Port=5432;Database=mcp_router;Username=postgres;Password=postgres` |
| `UPDATE_INTERVAL` | Nacos refresh interval in seconds (minimum 10) | `60` |
| `SEARCH_MIN_SIMILARITY` | Vector search threshold from 0 to 1 | `0.2` |
| `SEARCH_RESULT_LIMIT` | Maximum number of search results | `10` |

The model download scripts also read:

| Variable | Description | Default |
|---|---|---|
| `HF_ENDPOINT` | Model mirror endpoint | `https://www.modelscope.cn` |
| `HF_REPO` | Model repository | `sentence-transformers/all-MiniLM-L6-v2` |
| `HF_BRANCH` | Repository branch | `master` for ModelScope, `main` for Hugging Face |

## Architecture

See [the .NET router design](docs/superpowers/specs/2026-09-14-dotnet-mcp-router-design.md).

## License

nacos-mcp-router is licensed under the Apache 2.0 License. See [LICENSE](LICENSE) for details.

## Publishing

The project is packaged as a [.NET global tool](https://www.nuget.org/packages/NacosMcpRouter)
and published to NuGet.org and GitHub Packages through a GitHub Actions
release workflow. See [docs/publish-to-nuget.md](docs/publish-to-nuget.md) for the
full pipeline, required configuration, and troubleshooting.
