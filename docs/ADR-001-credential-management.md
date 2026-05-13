# ADR-001 — Credential management

**Status:** Accepted, applies from `0.1.0-alpha.2`.
**Date:** 2026-05-13.

## Context

LS증권 OpenAPI requires two long-lived secrets per developer account:
- `AppKey` (public-ish identifier, but still sensitive)
- `AppSecretKey` (high-sensitivity)

These are exchanged at `POST /oauth2/token` (grant `client_credentials`) for a short-lived bearer `access_token` (default `expires_in` ≈ 86400 s = 24 h).

The MCP server must:
1. Accept these credentials without leaking them to logs, transcripts, or the LLM that drives the host.
2. Cache access tokens across restarts so each restart doesn't burn a fresh token.
3. Refresh before expiry without blocking user calls.

## Decision

### Resolution

**Environment variables only.** The server reads `LS_APPKEY`, `LS_APPSECRETKEY`, and (optional) `LS_MARKET` from the process environment. No other path is accepted — not chat, not tool arguments, not MCP elicitation. Hosts (Claude Desktop, Claude Code, AssistStudio, etc.) are responsible for injecting these into the child process's environment from their own credential store.

**Why not elicitation.** MCP `elicitation/create` (spec 2025-06-18+) lets a server request structured input from the user via the client UI. In a well-behaved client (Claude Desktop, Claude Code, AssistStudio), the elicitation prompt and response are exchanged out-of-band with respect to the LLM context. The MCP spec, however, explicitly states:

> Servers MUST NOT use elicitation to request sensitive information.

The reasoning, made concrete in this project:

1. **Client implementation variance.** The protocol does not forbid a client from surfacing the elicitation prompt as a regular chat message visible to the model. New / third-party clients may do this and still be spec-conformant.
2. **User behaviour.** Users who don't recognise the elicitation dialog may type the key directly into the chat input, sending it straight to the model.
3. **Echo accidents.** A server that includes the elicited value in any tool response, error message, or log entry leaks it into the LLM context.
4. **Log / trace channels.** Anything the server writes to stderr — including diagnostic helpers — can be captured by a host and routed back to the model in unexpected ways.

Environment variables, by contrast, are injected by the OS into the process environment with **zero** channels that touch MCP, the LLM, or any chat surface. This is the strictest correct interpretation of the spec.

**Why not CLI arguments.** Process arguments are visible in `ps`/Task Manager output and frequently captured in shell history. They were considered as a debug path in earlier drafts (and in the original `todo/1` spec) but offer no security advantage over env vars, so they are not implemented.

### Token cache

The access token *is* persisted, because (a) re-issuing a token every restart wastes the LS-side rate budget and (b) the token itself is a dynamic credential whose persistence is the server's responsibility (cf. ADR-001 in FieldCure AssistStudio, which distinguishes static secrets from dynamic credentials).

- **Storage:** SQLite, WAL journal mode.
- **Path:**
    - Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db`
    - Linux/macOS: `~/.local/share/redoxnet/lsopenapi/token.db`
- **Key:** `SHA256(appkey):market` — the raw app key never lives on disk.
- **Permissions:** POSIX `chmod 0600` on the db and its `-wal` / `-shm` siblings; Windows relies on the user profile ACL.
- **Refresh policy:** When a cached token's remaining lifetime is ≤ 5 min, the next `GetAccessTokenAsync` call re-issues. Concurrent callers share a single in-flight issuance via a per-issuer semaphore.

### Secret hygiene

- [`SecretMasker.Mask("...XYZW")`](../src/RedoxNet.LsOpenApi.Core/Auth/SecretMasker.cs) returns `****XYZW` (only the last four chars visible). All log output that mentions an app key, app secret, or access token passes through the masker.
- `AppSecretKey` is **never** logged in any form, masked or unmasked. Only `AppKey` is logged (masked) for the diagnostic purpose of distinguishing two appkeys at a glance.
- Tool responses never echo credentials back. LS authentication failures (e.g. `IGW00121`) surface as [`LsAuthException`](../src/RedoxNet.LsOpenApi.Core/Auth/LsAuthException.cs) in the tool response's `error` field with no credential material attached.
- `LsCredentials` is a record — do not serialize it.

## Consequences

- **Cross-platform, zero OS-secret-store dependencies.** No DPAPI, Keychain, or libsecret coupling in the server itself; that responsibility is delegated to the host.
- **Users on shared machines should still trust the OS user boundary.** The token cache file is user-only but not encrypted at rest — same security posture as GitHub CLI, Docker CLI, AWS CLI.
- **A stolen `token.db` is as good as a stolen `access_token` for up to 24 h.** Mitigation: user-only file permissions.
- **Hosts without env-var injection support cannot run this server.** This is by design. The intended UX is that the host stores keys in its own credential store (Claude Desktop config, AssistStudio PasswordVault, etc.) and injects them at child-process launch — exactly the pattern documented in `README.md` under "자격 증명 처리 정책" / "Credential handling policy".

## References

- [MCP Elicitation Spec (2025-06-18)](https://modelcontextprotocol.io/specification/2025-06-18/server/elicitation) — see the "Security Considerations" subsection for the `MUST NOT` quoted above.
- [FieldCure AssistStudio ADR-001](../../fieldcure-assiststudio/docs/ADR-001-MCP-Credential-Management.md) — sibling project; distinguishes static secret vs dynamic credential and rolled out a wider env-var-with-elicit-fallback chain. This project takes the stricter no-elicit subset of that strategy for static secrets, while keeping the same dynamic-credential persistence pattern for the access token.
