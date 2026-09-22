---
name: mcp-router-tools
description: 使用 nacos-mcp-router 的三个 MCP 工具完成服务发现、安装和工具调用。适用于需要先搜索 Nacos 中的 MCP Server，再建立代理连接并调用其下游工具的场景。
---

# Nacos MCP Router 工具协作 Skill

## 适用场景

当需要使用 Nacos 中注册的 MCP Server 时，按以下顺序协作调用：

1. `search_mcp_server`：根据任务描述和关键词查找候选 MCP Server。
2. `add_mcp_server`：选择候选服务并建立代理会话，获取可用工具列表。
3. `use_tool`：通过 Router 调用已安装服务中的具体工具。

不要直接调用 `use_tool` 代替搜索和安装。Router 的代理会话通常只有在 `add_mcp_server` 成功后才存在。

## 工具契约

### 1. 搜索服务

```text
search_mcp_server(
  task_description: string,
  key_words: string
)
```

- `task_description`：完整描述当前任务和期望结果，例如“查询上海未来三天的天气”。
- `key_words`：用于缩小候选范围的关键词，可填写服务名称、领域或能力，例如“天气 forecast”。
- 先阅读返回结果中的服务名称和描述，再决定安装哪个服务。
- 搜索结果可能同时来自关键词匹配和向量匹配，重复服务应按名称去重。

### 2. 安装并建立代理

```text
add_mcp_server(
  mcp_server_name: string
)
```

- `mcp_server_name` 必须使用搜索结果中的准确服务名称。
- 成功后会返回该服务的工具列表。
- 记录工具的准确名称、用途和参数结构，后续调用必须使用这些信息。
- 如果服务不存在，返回内容会提示重新使用 `search_mcp_server`。
- 如果下游服务启动失败或协议不支持，不要重复盲目调用；检查服务名称、注册配置和协议后再搜索或换用其他候选。
- 如果服务要求 OAuth，先确认其配置包含 `oauth.flow`、token endpoint 和必要的 client 信息。`client_credentials` 会在建立连接时自动获取并缓存 token；`authorization_code` / `device_code` 返回授权地址或用户码时，先完成外部授权并回填 code，再重试 `add_mcp_server`。
- 对通过 Nacos 注册详情发现的服务，OAuth 配置由部署环境的 `MCP_OAUTH_CONFIG` 注入，顶层 key 使用 MCP Server 名称；不要把 secret 写入 Nacos 工具参数、Skill 或日志。

### 3. 调用下游工具

```text
use_tool(
  mcp_server_name: string,
  mcp_tool_name: string,
  params: string
)
```

- `mcp_server_name` 使用已成功安装的服务名称。
- `mcp_tool_name` 使用 `add_mcp_server` 返回的工具名称。
- `params` 必须是 JSON 对象字符串，而不是 JSON 数组、自然语言或裸值。
- 没有参数时使用 `{}`。
- 参数值必须符合下游工具声明的类型；不要把 JSON 对象额外包成字符串。

示例：

```text
use_tool(
  mcp_server_name: "weather-server",
  mcp_tool_name: "get_forecast",
  params: "{\"city\":\"Shanghai\",\"days\":3}"
)
```

## 标准工作流

### 服务发现

```text
search_mcp_server(
  task_description="查询上海未来三天的天气",
  key_words="天气 forecast"
)
```

从结果中选择最匹配的服务，例如 `weather-server`。

### 建立连接

```text
add_mcp_server(
  mcp_server_name="weather-server"
)
```

读取返回的工具列表，例如发现工具 `get_forecast(city, days)`。

### 执行调用

```text
use_tool(
  mcp_server_name="weather-server",
  mcp_tool_name="get_forecast",
  params="{\"city\":\"Shanghai\",\"days\":3}"
)
```

根据结果向用户汇报；如果返回业务错误，保留原始错误含义，不要把业务错误误判成搜索失败。

## 决策规则

- 任务目标不明确时，先补全 `task_description`，再搜索。
- 搜索结果为空时，扩大或改写关键词后最多重试一次；仍为空则说明没有找到合适服务。
- 搜索到多个服务时，优先选择描述与任务最匹配、协议受支持且工具列表覆盖需求的服务。
- `add_mcp_server` 成功后，如果工具列表中没有所需能力，换候选服务，不要对错误工具名反复调用。
- `use_tool` 返回参数解析错误时，检查 `params` 是否为合法 JSON 对象字符串。
- `use_tool` 返回“server not found”时，重新执行 `search_mcp_server`，然后再次 `add_mcp_server`。
- 不要把 `clientSecret`、`accessToken` 或 `deviceCode` 放入 `params`；OAuth 属于服务配置，不属于单次工具调用参数。
- 同一服务已经安装时，可以直接调用其已知工具；不需要每次重复安装。

## 快速检查清单

- [ ] 已明确任务目标和搜索关键词。
- [ ] 已通过 `search_mcp_server` 找到候选服务。
- [ ] 已通过 `add_mcp_server` 成功建立代理并读取工具列表。
- [ ] `use_tool` 的服务名和工具名与返回结果完全一致。
- [ ] `params` 是合法 JSON 对象字符串；无参数时为 `{}`。
