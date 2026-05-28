# Security Policy

## Supported versions

Active maintenance follows the **latest minor release** on the current `1.x` line. Older minors don't receive backports; security fixes ship in the next minor (or a patch release on the latest minor).

| Version | Supported |
|---|---|
| Latest `1.x` minor (currently `1.6.x`) | ✅ |
| Older `1.x` minors (`1.0.x` – `1.5.x`) | ❌ |
| `0.x` (any) | ❌ |

The MCP tool surface (names, model-facing params, response shapes) is frozen from `1.0.0` per `ToolSurfaceFreezeTests`; breaking changes to that surface require a major version bump.

## Reporting a vulnerability

If you discover a security issue, please report it privately via one of:

- **GitHub Private Advisory** (preferred): [github.com/redoxnet/mcp-lsopenapi/security/advisories/new](https://github.com/redoxnet/mcp-lsopenapi/security/advisories/new)
- **Email**: `diluculo@gmail.com`

Please **do not** file a public issue for security reports. We aim to acknowledge within 5 business days and ship a fix within 30 days for confirmed issues; you'll be credited in the release notes unless you ask otherwise.

## Credential handling — in scope

This server's design closes the channels through which the static LS credentials (`LS_APPKEY` / `LS_APPSECRETKEY`) could leak to LLMs or chat transcripts. The full rationale and the four leakage channels closed by the "env-var only" policy are in [`docs/ADR-001-credential-management.md`](docs/ADR-001-credential-management.md).

A finding that lets a static secret reach the model context **is a security issue** — report it via the channels above. Examples:

- A tool response that echoes the app key or secret back to the caller.
- A log line that fails to mask the secret.
- A code path that accepts the secret via MCP `elicitation/create` or any other model-visible channel.
- A token-cache write that records the raw app key (the cache key is intentionally `SHA256(appkey):market`).

## Out of scope

- **LS증권 OpenAPI itself** — server-side flaws on `openapi.ls-sec.co.kr` should be reported to LS Securities directly.
- **Third-party MCP clients** that mishandle protocol messages or expose secrets via their UI: report to that client's maintainer.
- **Upstream NuGet dependencies** (`Polly`, `Skender.Stock.Indicators`, `Microsoft.Data.Sqlite`, `ModelContextProtocol`, `Microsoft.Extensions.*`): we'll bump pinned versions once a CVE is published upstream, but the underlying issue should be reported to the dependency author.
- **User-controlled storage** — your shell history, environment-variable dumps, IDE plugins, screenshots: outside our threat model. The "credential never reaches the model" guarantee assumes the host injects env vars correctly and the user keeps their machine secure.

## Disclaimer

This is an unofficial third-party project. It is provided "as-is" under the MIT license; there is no warranty regarding fitness for any particular purpose, including in trading workflows. See [`LICENSE`](LICENSE).
