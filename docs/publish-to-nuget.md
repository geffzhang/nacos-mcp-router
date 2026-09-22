# Publishing `NacosMcpRouter` to NuGet

This document describes how `NacosMcpRouter` is packaged as a .NET global tool and
published to **GitHub Packages** and **NuGet.org** through a single GitHub Actions
release workflow.

## Overview

`NacosMcpRouter` is a .NET 10 executable (MCP server). Publishing it as a
**.NET global tool** lets users install and start the server with a single
command:

```bash
dotnet tool install -g NacosMcpRouter
nacos-mcp-router
```

The package is published simultaneously to two feeds:

| Feed | Purpose | Authentication |
|---|---|---|
| [nuget.org](https://www.nuget.org/packages/NacosMcpRouter) | Public discovery and consumption | `NUGET_API_KEY` secret |
| `nuget.pkg.github.com/geffzhang` | Mirrored artifact inside the GitHub org | `GH_PACKAGES_TOKEN` (PAT) |

## Project changes

### `src/NacosMcpRouter/NacosMcpRouter.csproj`

Added a `<PropertyGroup>` block with NuGet metadata and a `<ItemGroup>` that
links the repository `README.md` into the package root.

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
  <!-- The README lives in the repository root; link it into the package root. -->
  <None Include="..\..\README.md" Link="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

Key settings:

- `IsPackable=true` + `PackAsTool=true` → produces a `.NET` tool package
- `ToolCommandName` → installed command name (`nacos-mcp-router`)
- `PackageReadmeFile=README.md` → README shown on the NuGet gallery page

### Verifying locally

```bash
dotnet pack src/NacosMcpRouter/NacosMcpRouter.csproj \
  -c Release \
  -o artifacts \
  -p:PackageVersion=1.0.0-test
```

The output is `artifacts/NacosMcpRouter.1.0.0-test.nupkg`. Inspect the
metadata with:

```bash
unzip -p artifacts/NacosMcpRouter.1.0.0-test.nupkg NacosMcpRouter.nuspec
```

A correctly configured package shows `<packageType name="DotnetTool" />`,
the linked `README.md`, and all transitive runtime natives under
`tools/net10.0/any/runtimes/`.

## Release workflow (`.github/workflows/release.yml`)

The existing `release.yml` workflow already packs the project on every tag
push and creates a GitHub Release. Two new steps push the produced `.nupkg`
to the feeds.

```yaml
name: Publish .NET package

on:
  push:
    tags:
      - "*"

permissions:
  contents: write
  # NOTE: do NOT add `packages: write` — the GitHub Packages NuGet feed
  # requires a PAT even when the workflow has packages:write. See below.

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

Each `Push` step uses an `env:` block to surface a clear error if the
secret is missing, and `--skip-duplicate` so a re-run after a partial
failure does not error.

## Required GitHub repository secrets

Configure both secrets at
**Settings → Secrets and variables → Actions → New repository secret**.

### `GH_PACKAGES_TOKEN` — Personal Access Token (Classic)

The `GITHUB_TOKEN` issued to workflows only has **read access** to the
GitHub Packages NuGet feed by default — even when the workflow has
`packages: write`. The feed will return `Your request could not be
authenticated` with a misleading 403. Use a Classic PAT instead.

1. **https://github.com/settings/tokens → Generate new token (classic)**
2. Note: `nacos-mcp-router-publish`
3. Expiration: 365 days
4. Scopes:
   - `write:packages`
   - `read:packages`
5. Generate, copy the value, save as `GH_PACKAGES_TOKEN`.

### `NUGET_API_KEY` — NuGet.org API key

1. Sign in to **https://www.nuget.org/**
2. **Account → API Keys → Create**
3. Name: `nacos-mcp-router-publish`
4. **Glob Pattern**: `*`  *(or `geffzhang/*` to scope to your packages)*
5. **Select Scopes**: `Push new packages and package versions`
6. Create, paste the key as `NUGET_API_KEY`.

## Triggering a release

```bash
git tag v1.0.0
git push origin v1.0.0
```

The workflow will:

1. Check out the tag
2. Restore + `dotnet pack` → `NacosMcpRouter.<version>.nupkg` and `.snupkg`
3. Upload both as workflow artifacts
4. Push the `.nupkg` to GitHub Packages (using `GH_PACKAGES_TOKEN`)
5. Push the `.nupkg` to NuGet.org (using `NUGET_API_KEY`)
6. Create a GitHub Release with the artifacts attached

`PackageVersion` is derived from the tag (with the leading `v` stripped),
so `v1.0.3` produces `NacosMcpRouter.1.0.3`.

## Installing and running the published tool

```bash
dotnet tool install -g NacosMcpRouter --version 1.0.0
export NACOS_ADDR=127.0.0.1:8848
nacos-mcp-router   # starts the MCP server
```

Updating:

```bash
dotnet tool update -g NacosMcpRouter
```

Uninstalling:

```bash
dotnet tool uninstall -g NacosMcpRouter
```

## Troubleshooting

### `Push to GitHub Packages` fails with 403 "could not be authenticated"

Cause: the workflow is using `secrets.GITHUB_TOKEN`, which has read-only
access to the GitHub Packages NuGet feed.

Fix: rotate `GH_PACKAGES_TOKEN` to a **Classic PAT** with
`write:packages` and `read:packages` scopes. Do **not** set
`packages: write` on the workflow — it is not sufficient.

### `Push to NuGet.org` step exits 0 but the package does not appear

`dotnet nuget push` returns 0 on a successful HTTP 2xx response from the
NuGet push endpoint. NuGet.org can accept the upload and then reject the
package asynchronously (auto-moderation, validation, etc.) without making
the client fail.

If the workflow log shows no error but `https://api.nuget.org/v3-flatcontainer/<id-lowercased>/index.json`
still returns `404`:

1. Wait 5–15 minutes — NuGet.org indexing is asynchronous.
2. Check the package status as the NuGet.org account owner:
   https://www.nuget.org/packages/<id>/ — look for a *Deleted* or
   *Unlisted* state set by automated moderation.
3. Re-run the push from a local machine using the same API key:
   ```bash
   dotnet nuget push artifacts/*.nupkg \
     --source https://api.nuget.org/v3/index.json \
     --api-key "$NUGET_API_KEY"
   ```
   This surfaces the actual server-side error (license metadata,
   validation message, etc.).

### `dotnet pack` complains `NU5039: 包中不存在自述文件`

The `PackageReadmeFile` path must resolve relative to the `.csproj`
directory AND the file must be declared in the project (with
`Pack="true"`). The project uses:

```xml
<PackageReadmeFile>README.md</PackageReadmeFile>
<ItemGroup>
  <None Include="..\..\README.md" Link="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

If the README moves, update both the `Include` path and the
`PackageReadmeFile` value.

### Workflow fires on tag but no release is created

The last `Create GitHub Release` step uses `gh release create` and is
skipped if any prior step fails. Open the failed run in the Actions tab,
inspect the `Push to NuGet.org` step output, fix the cause, then
re-trigger by deleting and re-pushing the tag:

```bash
git tag -d v1.0.0
git push origin :refs/tags/v1.0.0
git tag v1.0.0
git push origin v1.0.0
```

## Reference

- [.NET global tool packaging](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-publish)
- [`dotnet pack` reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-pack)
- [`dotnet nuget push` reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push)
- [Working with the GitHub Packages NuGet registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [NuGet.org API keys](https://learn.microsoft.com/en-us/nuget/nuget-org/nuget-org-api-keys)