# Contributing to mcp-lsopenapi

Thanks for your interest. This document covers how to add a TR, add an MCP tool, and what the local development loop looks like.

## Repo layout

```
src/RedoxNet.LsOpenApi.Core/          SDK: auth, HTTP, catalog, indicators, models
src/RedoxNet.Mcp.LsOpenApi/           MCP server tools (one file per tool)
src/RedoxNet.LsOpenApi.Core.Catalog.Builder/   Dev tool that regenerates the embedded catalog (not shipped)
tests/RedoxNet.LsOpenApi.Core.Tests/  Unit + integration tests for the SDK
tests/RedoxNet.Mcp.LsOpenApi.Tests/   Unit + fixture tests for the MCP tools
docs/                                 ADR-001, TR inventory, catalog generation notes
scripts/                              live-smoke + publish scripts
```

## Adding a new TR to the catalog

1. Confirm the TR exists on LS's [API service](https://openapi.ls-sec.co.kr/apiservice). Capture the **InBlock** schema (required fields, lengths, descriptions) and at least one OutBlock.
2. Add the entry to `src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json`. Match the existing structure (`tr_code`, `name`, `category`, `path`, `description`, `in_blocks`, `out_blocks`, `continuation`, `rate_limit_per_sec`). LS field naming can be inconsistent across TRs — note that explicitly in the description when it matters (see `t1902.crate` vs `t1901.cocrate` for an example).
3. List the entry in `docs/LS-TR-INVENTORY.md` and update its status marker.
4. The catalog ships as an embedded resource in `RedoxNet.LsOpenApi.Core`, so the next pack picks it up automatically.

## Adding a new MCP semantic tool

A semantic tool wraps an existing TR and projects the raw response into an LLM-friendly shape.

1. New file under `src/RedoxNet.Mcp.LsOpenApi/Tools/` — one tool per file.
2. Use the established pattern (see `GetEtfInfoTool.cs` as a recent example):
   - `[McpServerToolType]` on the static class.
   - `[McpServerTool(Name = "ls_…")]` on the method, with a `[Description("…")]` block that includes a **USE WHEN** + **AVOID WHEN** clause. LLM tool selection accuracy depends on this pattern — keep it.
   - Surface units explicitly in field names when LS data is non-obvious (e.g. `value_million_won`, `total_assets_eok`).
3. Add a fixture test under `tests/RedoxNet.Mcp.LsOpenApi.Tests/Tools/` that pins the new tool against an actual LS testbed response body — copy from the LS docs sample.
4. Update `README.md` / `README.en.md` (root) and `src/RedoxNet.Mcp.LsOpenApi/README.md` (NuGet) to list the new tool.

## Local dev loop

```bash
dotnet restore mcp-lsopenapi.slnx
dotnet build mcp-lsopenapi.slnx -c Release
dotnet test mcp-lsopenapi.slnx -c Release
```

`scripts/live-smoke.ps1` exercises a tool end-to-end against the LS virtual server when `LS_APPKEY` / `LS_APPSECRETKEY` / `LS_MARKET` are set in the environment.

## Code style

- C# 12 / .NET 8+, file-scoped namespaces, nullable enabled.
- XML doc comments on every public **and** private/internal member.
- Comments in English; tool descriptions can use Korean phrases where they help LLM matching.
- Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`.

## Release commits

`.github/workflows/release.yml` triggers when a commit message on `main` matches:

| Message | Result |
|---|---|
| `Release v0.1.0` | Combined GitHub Release with both packages' notes |
| `Release Core v0.1.1` | Core-only Release (`lsopenapi.core-v0.1.1`) |
| `Release Mcp v0.2.0` | Mcp-only Release (`mcp.lsopenapi-v0.2.0`) |
| `Release Core v0.1.1 Mcp v0.2.0` | Two separate Releases, one each |

NuGet publishing is manual, post-tag: `scripts/publish-core.ps1` / `publish-mcp.ps1` / `publish-nuget.ps1` (Core then Mcp).

## License

By contributing, you agree that your contributions are licensed under MIT.
