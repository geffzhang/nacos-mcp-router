# 将 `NacosMcpRouter` 发布到 NuGet

本文档介绍如何将 `NacosMcpRouter` 打包为 .NET 全局工具，并通过单一 GitHub Actions release 工作流同时发布到 **GitHub Packages** 和 **NuGet.org**。

## 概述

`NacosMcpRouter` 是一个 .NET 10 可执行程序（MCP server）。将其作为 **.NET 全局工具（global tool）** 发布后，用户可使用一条命令完成安装并启动服务：

```bash
dotnet tool install -g NacosMcpRouter
nacos-mcp-router
```

包同时推送到两个 feed：

| Feed | 用途 | 鉴权方式 |
|---|---|---|
| [nuget.org](https://www.nuget.org/packages/NacosMcpRouter) | 公开分发与发现 | `NUGET_API_KEY` secret |
| `nuget.pkg.github.com/geffzhang` | GitHub 组织内镜像 | `GH_PACKAGES_TOKEN`（Classic PAT） |

## 项目改动

### `src/NacosMcpRouter/NacosMcpRouter.csproj`

新增一段 NuGet 元数据 `<PropertyGroup>` 与一个将仓库根 `README.md` 链接进包根目录的 `<ItemGroup>`。

```xml
<PropertyGroup>
  <!-- existing settings ... -->

  <!-- NuGet packaging metadata -->
  <IsPackable>true</IsPackable>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>nacos-mcp-router</ToolCommandName>
  <PackageId>NacosMcpRouter</PackageId>
  <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <Authors>nacos-mcp-router contributors</Authors>
  <Description>A .NET 10 MCP server that searches, installs, and proxies
    MCP servers registered in Nacos. Connects to Nacos, discovers downstream
    MCP servers, and exposes them through Model Context Protocol tools.</Description>
  <PackageTags>mcp;model-context-protocol;nacos;router;dotnet;agent</PackageTags>
  <RepositoryUrl>https://github.com/geffzhang/nacos-mcp-router</RepositoryUrl>
  <RepositoryType>git</RepositoryType>
  <IncludeBuildOutput>true</IncludeBuildOutput>
</PropertyGroup>

<ItemGroup>
  <!-- README 位于仓库根目录，通过 Link 引入包根 -->
  <None Include="..\..\README.md" Link="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

关键设置说明：

- `IsPackable=true` + `PackAsTool=true` → 生成 .NET 工具包
- `ToolCommandName` → 安装后的可执行命令名（`nacos-mcp-router`）
- `PackageReadmeFile=README.md` → 在 NuGet gallery 页面展示 README

### 本地验证打包

```bash
dotnet pack src/NacosMcpRouter/NacosMcpRouter.csproj \
  -c Release \
  -o artifacts \
  -p:PackageVersion=1.0.0-test
```

产出 `artifacts/NacosMcpRouter.1.0.0-test.nupkg`。查看元数据：

```bash
unzip -p artifacts/NacosMcpRouter.1.0.0-test.nupkg NacosMcpRouter.nuspec
```

正确打包后应看到 `<packageType name="DotnetTool" />`、已链接的 `README.md`，以及所有传递依赖的原生库位于 `tools/net10.0/any/runtimes/` 下。

## Release 工作流（`.github/workflows/release.yml`）

现有 `release.yml` 已在 tag 推送时打包并创建 GitHub Release。新增两个步骤把生成的 `.nupkg` 推送到两个 feed。

```yaml
name: Publish .NET package

on:
  push:
    tags:
      - "*"

permissions:
  contents: write
  # 注意：不要设置 packages: write —— 即使设置了，GITHUB_TOKEN
  # 仍然对 GitHub Packages NuGet feed 只有读权限（详见下文）。

jobs:
  package:
    name: Package and release
    runs-on: ubuntu-latest
    steps:
      - name: Check out tag
        uses: actions/checkout@v4

      - name: Set up .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Restore dependencies
        run: dotnet restore src/NacosMcpRouter.sln

      - name: Pack
        shell: bash
        run: |
          VERSION="${GITHUB_REF_NAME#v}"
          dotnet pack src/NacosMcpRouter/NacosMcpRouter.csproj \
            --configuration Release \
            --no-restore \
            --output artifacts \
            -p:PackageVersion="$VERSION" \
            -p:IncludeSymbols=true \
            -p:SymbolPackageFormat=snupkg

      - name: Upload package artifacts
        uses: actions/upload-artifact@v4
        with:
          name: NacosMcpRouter-${{ github.ref_name }}
          path: artifacts/*.*nupkg
          if-no-files-found: error

      - name: Push to GitHub Packages
        env:
          GH_PACKAGES_TOKEN: ${{ secrets.GH_PACKAGES_TOKEN }}
        run: |
          if [ -z "${GH_PACKAGES_TOKEN}" ]; then
            echo "::error::GH_PACKAGES_TOKEN secret is not set."
            exit 1
          fi
          echo "Token length: ${#GH_PACKAGES_TOKEN}"
          dotnet nuget push "artifacts/*.nupkg" \
            --source "https://nuget.pkg.github.com/geffzhang/index.json" \
            --api-key "${GH_PACKAGES_TOKEN}" \
            --skip-duplicate

      - name: Push to NuGet.org
        env:
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
        run: |
          if [ -z "${NUGET_API_KEY}" ]; then
            echo "::error::NUGET_API_KEY secret is not set."
            exit 1
          fi
          echo "Token length: ${#NUGET_API_KEY}"
          dotnet nuget push "artifacts/*.nupkg" \
            --source "https://api.nuget.org/v3/index.json" \
            --api-key "${NUGET_API_KEY}" \
            --skip-duplicate

      - name: Create GitHub Release
        env:
          GH_TOKEN: ${{ github.token }}
        run: gh release create "$GITHUB_REF_NAME" artifacts/*.*nupkg --generate-notes --verify-tag
```

每个 `Push` 步骤都使用 `env:` 块，在 secret 缺失时给出明确的错误信息；并使用 `--skip-duplicate`，使部分失败后的重跑不会因版本已发布而报错。

## 必需的 GitHub 仓库 Secrets

在仓库的 **Settings → Secrets and variables → Actions → New repository secret** 中配置以下两个 secret。

### `GH_PACKAGES_TOKEN` —— Classic Personal Access Token

Workflow 运行时默认的 `GITHUB_TOKEN` 对 GitHub Packages NuGet feed 仅有**只读访问权限** —— 即便工作流声明了 `packages: write` 也是如此。Feed 会返回 `Your request could not be authenticated`（伴随一个误导性的 403）。必须使用 Classic PAT 替代。

1. 打开 **https://github.com/settings/tokens → Generate new token (classic)**
2. Note：`nacos-mcp-router-publish`
3. Expiration：365 天
4. Scopes 勾选：
   - `write:packages`
   - `read:packages`
5. 点击生成，复制 token 值，保存为 `GH_PACKAGES_TOKEN`

### `NUGET_API_KEY` —— NuGet.org API Key

1. 登录 **https://www.nuget.org/**
2. 顶部 **Account → API Keys → Create**
3. Name：`nacos-mcp-router-publish`
4. **Glob Pattern**：`*`  *（或 `geffzhang/*` 限定到本账号的包）*
5. **Select Scopes**：`Push new packages and package versions`
6. Create，将生成的 key 保存为 `NUGET_API_KEY`

## 触发发布

```bash
git tag v1.0.0
git push origin v1.0.0
```

工作流会自动完成：

1. 检出 tag
2. 还原依赖并执行 `dotnet pack` → 产出 `NacosMcpRouter.<version>.nupkg` 与 `.snupkg`
3. 上传两个文件为 workflow artifacts
5. 将 `.nupkg` 推送到 GitHub Packages（使用 `GH_PACKAGES_TOKEN`）
6. 将 `.nupkg` 推送到 NuGet.org（使用 `NUGET_API_KEY`）
7. 创建 GitHub Release 并附带上述文件

`PackageVersion` 由 tag 自动推导（去除前导 `v`），所以 `v1.0.3` 会产生 `NacosMcpRouter.1.0.3`。

## 安装与使用已发布的工具

```bash
dotnet tool install -g NacosMcpRouter --version 1.0.0
export NACOS_ADDR=127.0.0.1:8848
nacos-mcp-router   # 启动 MCP server
```

升级：

```bash
dotnet tool update -g NacosMcpRouter
```

卸载：

```bash
dotnet tool uninstall -g NacosMcpRouter
```

## 故障排查

### `Push to GitHub Packages` 报 403 "could not be authenticated"

原因：工作流使用了 `secrets.GITHUB_TOKEN`，它对 GitHub Packages NuGet feed 仅只读。

解决：将 `GH_PACKAGES_TOKEN` 换为 **Classic PAT**，勾选 `write:packages` 和 `read:packages` 作用域。**不要**在 workflow 中设置 `packages: write` —— 那是无效的。

### `Push to NuGet.org` 步骤退出码为 0 但包未出现

`dotnet nuget push` 在收到 NuGet push 端点的 HTTP 2xx 响应时即返回 0。NuGet.org 可能接受上传后再异步拒绝该包（自动审核、校验失败等），而**不会**让客户端报错。

如果工作流日志没有任何错误，但
`https://api.nuget.org/v3-flatcontainer/<id-lowercased>/index.json`
仍然返回 `404`：

1. 等待 5–15 分钟 —— NuGet.org 索引是异步的。
2. 以 NuGet.org 账号身份查看包状态：
   https://www.nuget.org/packages/<id>/ —— 注意是否被自动审核设为 *Deleted* 或 *Unlisted*。
3. 在本地用同一个 API key 手动重推一次，能看到真实的服务器错误（license 元数据、校验信息等）：
   ```bash
   dotnet nuget push artifacts/*.nupkg \
     --source https://api.nuget.org/v3/index.json \
     --api-key "$NUGET_API_KEY"
   ```

### `dotnet pack` 报 `NU5039: 包中不存在自述文件`

`PackageReadmeFile` 路径需相对于 `.csproj` 目录解析，且该文件必须在项目中被声明（带 `Pack="true"`）。本项目使用：

```xml
<PackageReadmeFile>README.md</PackageReadmeFile>
<ItemGroup>
  <None Include="..\..\README.md" Link="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

如果 README 位置变化，需同时更新 `Include` 路径和 `PackageReadmeFile` 值。

### Tag 已推送但没有创建 Release

最后一个 `Create GitHub Release` 步骤使用 `gh release create`，任意前置步骤失败都会跳过它。在 Actions Tab 打开失败的那次运行，查看 `Push to NuGet.org` 步骤输出，修复后删除并重新推送 tag 即可重新触发：

```bash
git tag -d v1.0.0
git push origin :refs/tags/v1.0.0
git tag v1.0.0
git push origin v1.0.0
```

## 参考

- [.NET 全局工具打包](https://learn.microsoft.com/zh-cn/dotnet/core/tools/global-tools-how-to-publish)
- [`dotnet pack` 参考](https://learn.microsoft.com/zh-cn/dotnet/core/tools/dotnet-pack)
- [`dotnet nuget push` 参考](https://learn.microsoft.com/zh-cn/dotnet/core/tools/dotnet-nuget-push)
- [使用 GitHub Packages NuGet 注册表](https://docs.github.com/zh/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [NuGet.org API Keys](https://learn.microsoft.com/zh-cn/nuget/nuget-org/nuget-org-api-keys)