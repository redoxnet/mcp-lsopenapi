# ADR-001 — Credential management

**Status:** Accepted, v1.0.

## Context

LS증권 OpenAPI requires two long-lived secrets per developer account:
- `AppKey` (public-ish identifier, but still sensitive)
- `AppSecretKey` (high-sensitivity)

These are exchanged at `POST /oauth2/token` (grant `client_credentials`) for a short-lived bearer `access_token` (default `expires_in` ≈ 86400s = 24h).

The MCP server must:
1. Accept credentials from several places without leaking them to logs or transcripts.
2. Cache access tokens across restarts so each restart doesn't burn a fresh token.
3. Refresh before expiry without blocking user calls.

## Decision

### Resolution chain

`ILsCredentialsResolver` walks sources in this order:

1. **Environment variables** — `LS_APPKEY`, `LS_APPSECRETKEY`, `LS_MARKET`.
2. **MCP elicitation** (future) — when running as MCP server and creds are missing, ask the client (Claude Desktop, etc.) to elicit them. Elicited secrets are kept in memory only by default.
3. **CLI args** (debug only) — `--appkey`, `--appsecretkey`, `--market`.

The shipping v1.0 implementation does (1) only; (2) and (3) are planned add-ons.

### Token cache

- **Storage:** SQLite, WAL journal mode.
- **Path:**
    - Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db`
    - Linux/macOS: `~/.local/share/redoxnet/lsopenapi/token.db`
- **Key:** `SHA256(appkey):market` — the raw app key never lives on disk.
- **Permissions:** POSIX chmod 0600 on the db and its `-wal` / `-shm` siblings; Windows relies on the user's profile ACL.
- **Refresh policy:** When a cached token's remaining lifetime is ≤ 5 min, the next `GetAccessTokenAsync` call re-issues. Concurrent callers share a single in-flight issuance via a per-issuer semaphore.

### Secret hygiene

- `SecretMasker.Mask("...XYZW")` returns `****XYZW` (only the last four chars visible).
- All logging that mentions an app key, app secret, or access token must pass through the masker.
- `LsCredentials` is a record — do not serialize it.

## Consequences

- Local users on shared machines should still trust the OS user boundary; we do not encrypt the cache at rest.
- A token cache that survives across restarts means a stolen `token.db` is as good as a stolen `access_token` for up to 24 h. Mitigation: user-only file permissions.
- The chain pattern keeps Core free of MCP elicitation surface — the Mcp project can plug in an elicitation-backed resolver later without touching Core.
