# TR Catalog

The TR catalog is the contract between the MCP server and LS증권 OpenAPI. Every TR the server can invoke must appear in the catalog with its endpoint path, input schema, and output blocks. The catalog is shipped as an **embedded JSON resource** inside `RedoxNet.LsOpenApi.Core`:

```
src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json
```

It is loaded once at startup by `TrCatalog.Default`.

## v1.0 seed

The shipping seed (`source: "manual-seed"`) is hand-curated and covers the read-only TRs the v1.0 semantic tools need:

| TR | Purpose | Endpoint |
| --- | --- | --- |
| `t1101` | Current price + 10-level order book | `/stock/market-data` |
| `t8410` | Day / week / month OHLCV | `/stock/chart` |
| `t8412` | Minute OHLCV | `/stock/chart` |
| `t1301` | Time-and-sales (tick) | `/stock/market-data` |
| `t8430` | KOSPI / KOSDAQ stock universe | `/stock/etc` |

> The seed entries were authored from public docs and reference implementations. Field names and types should be verified against the live LS docs before production use — once the catalog builder lands (below), regenerating against the live site is the source of truth.

## Regenerating from the LS site

`RedoxNet.LsOpenApi.Core.Catalog.Builder` is a dev-only scraper that walks the LS API service pages and rebuilds the catalog JSON. **Not packaged or shipped.** Run it manually when LS publishes API changes; commit the regenerated JSON via PR.

```bash
dotnet run --project src/RedoxNet.LsOpenApi.Core.Catalog.Builder \
    -- --output src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json
```

> v1.0 status: the scraper skeleton is in place but the page-parsing logic is not yet implemented. Regenerate the catalog manually for now.

## Schema

```jsonc
{
  "version": "1.0.0-alpha.1+seed",
  "generated_at_utc": "2026-05-13T00:00:00Z",
  "source": "manual-seed",
  "trs": [
    {
      "tr_code": "t1101",
      "name": "주식 현재가호가조회",
      "category": "주식시세",
      "path": "/stock/market-data",
      "description": "...",
      "in_blocks":  [{ "name": "...InBlock",  "is_array": false, "fields": [...] }],
      "out_blocks": [{ "name": "...OutBlock", "is_array": false, "fields": [...] }],
      "continuation": { "supported": false, "key_field": null },
      "rate_limit_per_sec": 1
    }
  ]
}
```

Field rules:
- `tr_code` is case-insensitive (the accessor normalizes).
- `is_array=true` on an output block indicates a multi-row payload (e.g. candle list).
- `continuation.supported=true` means the TR honors `tr_cont=Y` for paging.
- `rate_limit_per_sec` feeds `TrRateLimiter`; leave `null` if LS does not publish a number.
