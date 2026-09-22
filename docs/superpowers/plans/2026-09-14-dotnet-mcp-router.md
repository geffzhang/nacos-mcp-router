# .NET-only Repository Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删除 Python 和 TypeScript 实现及其现行文档入口，使仓库只保留并宣传 .NET 10 实现。

**Architecture:** 保留 `src/dotnet/` 及根目录共享模型 `models/all-MiniLM-L6-v2/`，删除 `src/python/` 和 `src/typescript/`。根 README 与中文 README 只描述 .NET 10；设计和计划文档只描述当前 .NET 架构，不改动 .NET 业务代码。

**Tech Stack:** .NET 10、PowerShell、Git；验证使用 `dotnet build`、`dotnet test` 和 PowerShell 文本扫描。

## Global Constraints

- 仅删除 `src/python/` 和 `src/typescript/`，保留 `src/dotnet/` 和根目录 `models/`。
- 不修改 .NET 源代码、项目文件、测试代码或模型文件。
- 根 README、中文 README 和 superpowers 文档不得再提供已删除实现的当前使用入口。
- 不修改 Git 历史；本次提交只包含当前工作区文件变更。
- 使用 PowerShell 原生命令完成 Windows 环境验证；仓库没有 `rg` 时不得把扫描失败误判为代码失败。

---

### Task 1: Remove legacy implementations

**Files:**
- Delete: `src/python/`
- Delete: `src/typescript/`

**Interfaces:**
- Consumes: 当前仓库文件系统。
- Produces: 只剩 `src/dotnet/` 作为语言实现目录。

- [ ] **Step 1: Record the pre-change state**

Run:

```powershell
git status --short
Test-Path src/python
Test-Path src/typescript
Test-Path src/dotnet
Test-Path models/all-MiniLM-L6-v2
```

Expected: 工作区无未预期修改，前两个路径为 `True`，后两个路径为 `True`。

- [ ] **Step 2: Delete the two legacy directories**

Use the editor file operation or the repository's approved deletion workflow to remove exactly `src/python/` and `src/typescript/`. Do not remove `src/dotnet/` or `models/`.

- [ ] **Step 3: Verify the deletion boundary**

Run:

```powershell
Test-Path src/python
Test-Path src/typescript
Test-Path src/dotnet
Test-Path models/all-MiniLM-L6-v2
```

Expected: the first two values are `False`; the last two values are `True`.

- [ ] **Step 4: Commit the deletion**

```powershell
git add -A src/python src/typescript
git commit -m "chore: remove legacy python and typescript implementations"
```

---

### Task 2: Make current documentation .NET-only

**Files:**
- Modify: `README.md`
- Modify: `README_cn.md`
- Modify: `docs/superpowers/plans/2026-09-14-dotnet-mcp-router.md`
- Verify: `docs/superpowers/specs/2026-09-14-dotnet-mcp-router-design.md`

**Interfaces:**
- Consumes: the existing .NET quick start, Docker commands, environment variables, architecture, and test instructions.
- Produces: documentation that presents only .NET 10 as the supported implementation and contains no links to deleted paths.

- [ ] **Step 1: Remove obsolete root README sections**

In both root README files, remove the Python and TypeScript sections, including their package commands (`uvx`, `pip`, `python`, `npx`) and links into deleted directories. Keep and promote the existing `.NET 10` section, its Docker example, and the license section.

- [ ] **Step 2: Normalize .NET-only wording**

Replace wording that says the .NET implementation is an additional implementation or that configuration is shared with Python/TypeScript. Keep .NET-specific environment variables and deployment notes unchanged unless they reference a deleted path.

- [ ] **Step 3: Verify the design and plan documents**

The design document must retain the .NET architecture and must not describe deleted implementations as current. The plan document must describe this cleanup only and must contain complete executable steps.

- [ ] **Step 4: Check documentation links and legacy references**

Run:

```powershell
$patterns = 'src/python|src/typescript|uvx|pip install|python -m|npx'
$files = @('README.md', 'README_cn.md', 'docs/superpowers/specs/2026-09-14-dotnet-mcp-router-design.md')
Select-String -Path $files -Pattern $patterns -CaseSensitive:$false
```

Expected: no output for deleted implementation paths or obsolete launch commands. The cleanup plan itself may name the paths it removes.

- [ ] **Step 5: Commit documentation cleanup**

```powershell
git add README.md README_cn.md docs/superpowers/specs/2026-09-14-dotnet-mcp-router-design.md docs/superpowers/plans/2026-09-14-dotnet-mcp-router.md
git commit -m "docs: make repository documentation dotnet-only"
```

---

### Task 3: Build, test, and final repository scan

**Files:**
- Verify: `src/dotnet/NacosMcpRouter.sln`
- Verify: `src/dotnet/tests/NacosMcpRouter.Tests/`

**Interfaces:**
- Consumes: the preserved .NET solution and tests.
- Produces: evidence that the .NET implementation remains buildable and testable after the cleanup.

- [ ] **Step 1: Build the .NET solution**

Run:

```powershell
dotnet build src/dotnet/NacosMcpRouter.sln --no-restore
```

Expected: exit code 0 and no new compile errors caused by this cleanup.

- [ ] **Step 2: Run the .NET tests**

Run:

```powershell
dotnet test src/dotnet/NacosMcpRouter.sln --no-restore --no-build
```

Expected: exit code 0 and all existing tests pass.

- [ ] **Step 3: Confirm no deleted paths remain in tracked files**

Run:

```powershell
$tracked = git ls-files
$matches = $tracked | Select-String -Pattern '(^|/)(src/python|src/typescript)(/|$)'
if ($null -ne $matches) { $matches; exit 1 }
if (Test-Path src/python) { throw 'src/python still exists' }
if (Test-Path src/typescript) { throw 'src/typescript still exists' }
if (-not (Test-Path src/dotnet)) { throw 'src/dotnet is missing' }
```

Expected: no output and exit code 0.

- [ ] **Step 4: Review the final diff**

```powershell
git diff HEAD~2..HEAD --check
git status --short
git log -2 --oneline
```

Expected: no whitespace errors; only the intended deletion, documentation, and planning commits are present; the working tree is clean.
