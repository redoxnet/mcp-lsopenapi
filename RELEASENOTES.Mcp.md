# Release Notes — RedoxNet.Mcp.LsOpenApi

## v0.8.0 (2026-05-20)

Overseas market data + per-stock analytics. Tool surface 43 → **48**
(+5). The v0.8 line split in two: this release ships the wrapper +
catalog work, while the response-shape / token-economy refactor moves to
v0.9. Five new wrappers turn natural questions — *"나스닥 지수"*,
*"원달러 환율"*, *"삼성전자 투자의견"*, *"SK하이닉스 공매도 추이"*,
*"오늘 52주 신고가"*, *"요즘 예탁금 추이"* — into one tool call each.

### Added — Overseas market data (1 tool)

- `ls_get_global_market_quote(kind?, symbol?)` — one-shot overseas
  index / FX / futures snapshot via `t3521`. An alias table maps
  `nasdaq` → `NAS@IXIC`, `sp500` → `SPI@SPX`, plus `dow` / `soxx` /
  `usdkrw` / `wti` / `gold` and more; raw LS symbols pass through
  unchanged. `kind` ∈ `index` / `fx` / `futures`.

### Added — Per-stock analytics & screener (4 tools)

- `ls_get_analyst_opinions(shcode, count?)` — brokerage (sell-side)
  investment-opinion history via `t3401`. Each entry carries the
  opinion-day date, 회원사, the rating before/after the change, the
  target price before/after, and the opinion-day close, plus a
  current-price snapshot. Returns the ~20 most recent changes.
- `ls_get_short_selling_trend(shcode, from?, to?, count?)` — per-stock
  daily short-selling (공매도) via `t1927`. Short volume / value
  (백만원), short ratio, average short price, cumulative short volume,
  and the uptick-rule applied vs. exempt split, with a period summary
  (totals + highest-ratio day).
- `ls_get_market_funds_trend(market?, count?)` — market-liquidity
  series via `t8428`. Per day: index, 고객예탁금, 예탁증감, 신용잔고,
  미수금, 선물예수금, and equity / mixed / bond / MMF fund money.
  All monetary fields in 억원.
- `ls_get_high_low_stocks(direction?, period?, maintained?, market?, top_n?, exclude_etf?)`
  — 신고가 / 신저가 screener via `t1442`. Defaults tuned from live
  E2E: `maintained=true` (돌파유지) and ETF/ETN excluded server-side,
  so the result is the clean "currently at a new high" list. `period`
  look-back spans 전일 ~ 52주 ~ 년중.

### Changed

- Embedded TR catalog 33 → **45**: `t3102` / `t3518` / `t3521` plus the
  nine staged TRs (`t1105` / `t1305` / `t1403` / `t1442` / `t1475` /
  `t1927` / `t3401` / `t8425` / `t8428`), all reachable via `ls_call_tr`.
- `ls_call_tr` now surfaces body continuation cursors. A TR that pages
  by `tr_cont` header yet also carries a body cursor field (e.g.
  t3401's `cts_date`) previously returned `continuation.keys: {}`; the
  keys map is now populated from the catalog's `key_fields` so the
  caller knows which field to advance for the next page.

### Notes

- News (`t3102`) is catalog-only. The LS news pipeline discovers
  article IDs (`sNewsno`) through the NWS WebSocket push — the push
  payload's `realkey` is the `sNewsno` — which this server does not yet
  implement. A first-class news wrapper waits on WebSocket transport
  (v2.0).

## v0.7.0 (2026-05-18)

Screeners + index history + industry filter. Tool surface 40 → **43**
(net +3 after Tier 2 compression). The headline pivot is from
"snapshot one stock" toward natural screener questions — *"PER 낮은
종목"*, *"오늘 외인 매수 누구 들어왔어"*, *"다음 주주총회 언제"*,
*"내 보유 중 관리종목 있어?"* — answerable in one tool call. v0.7 also
moves portfolio data hygiene forward: `avg_price` no longer drifts
under split/reverse-split round-trips, ETF holdings stop reporting
perpetual "themes pending", and a new `industry` filter classifies
holdings by FICS industry name without a manual refresh.

### Added — Screeners (4 tools)

- `ls_get_fundamentals_rank(field, market?, count?)` — fundamental-metric
  ranking via `t3341` (재무순위종합). One TR call covers PER / PBR /
  PEG / EPS / BPS / ROE plus four growth metrics + 부채비율 + 유보율.
  PER / PBR / PEG are forced ascending (LS-side, undervalued first);
  the other metrics use LS's default ordering. `field` accepts English
  snake_case (`per`, `pbr`, …) or Korean labels (`매출액증가율`, …).
  Each row carries the full fundamental snapshot so the model can
  compare two metrics on the same stock without a follow-up call.
- `ls_get_investor_flow(shcode?, …)` — investor-type flow dispatcher
  across `t1601` (intraday market-wide) and `t1702` (single-stock
  daily). Omit `shcode` → market snapshot per segment (KOSPI / KOSDAQ /
  선물 / 옵션; LS does not label segments, so the wrapper surfaces
  them as `block_index = 1..6`). Pass `shcode` → daily time series
  with `metric` (`volume`/`value`/`price`) + `direction` (`net`/`buy`/
  `sell`) + `cumulative` toggle. Twelve investor types unified across
  both modes: 개인 / 외국인 / 기관계 / 증권 / 투신 / 은행 / 보험 / 종금
  / 기금 / 국가 / 기타 / 사모펀드.
- `ls_get_stock_events(shcode, from?, to?, kinds?)` — corporate-action
  calendar via `t3202` (종목별 증시일정). Covers all 14 LS event types
  (유무상증자, 배당, 감자, 합병/분할, 매수청구, 실권주, 액면교체,
  주주총회, 상호변경, 국내/해외 CB 전환, 해외 BW 행사, 스톡옵션행사).
  `kinds` accepts English snake_case, Korean labels, or raw two-char
  upgu codes. TBD entries (`recdt='00000000'`) survive date filtering
  so *"다음 주총 언제"* surfaces even when LS hasn't fixed the date.
- `ls_get_market_warnings(kinds?, shcodes?, market?)` — KRX surveillance
  list via `t1404` + `t1405`. Covers 13 designations: 관리, 불성실공시,
  투자유의, 투자환기, 투자경고, 매매정지, 정리매매, 투자주의,
  투자위험, 위험예고, 단기과열지정, 이상급등, 상장주식수부족. Default
  kind set is **관리 (designated_admin) only** — pass an explicit list
  (e.g. `["관리", "매매정지", "단기과열"]`) for wider sweep. `shcodes`
  clips against holdings so *"내 보유 중 관리종목"* takes one call.
  Per-kind seen-shcode dedup absorbs the LS quirk where `cts_shcode`
  echoes the same cursor when a single-page screen has more candidates
  to enumerate, so the response stays the size of the actual unique
  set instead of fan-out × pages.

### Added — Index history + metadata refresh (2 tools)

- `ls_get_index_history(upcode, period_type?, count?, cts_date?)` — daily/
  weekly/monthly time series for a Korean index via `t1514`. Aliases
  mirror `ls_get_index_quote` (`kospi` → `001` etc.). Per-bar OHLC,
  volume, transaction value, market breadth (advance/decline/limit-up/
  limit-down), and foreign/institutional net flow. `cts_date` pagination
  surfaced when more pages exist; dataset-handle integration (so
  `ls_add_indicator` can pipe off this series) is deferred to v0.8.
- `ls_stocks_refresh_metadata(shcodes?, kinds?)` — synchronous refresh
  for theme and FICS industry enrichment. Default scope = holdings ∪
  watchlist symbols when `shcodes` omitted; `kinds` ∈ `themes` /
  `industry`. Blocks until the LS-side TPS-1 calls finish (≈N seconds
  for N symbols), then echoes per-symbol `themes_updated` /
  `industry_updated` flags and any errors. Use case: the user just
  imported a fresh portfolio and wants the metadata caches warm.

### Added — Industry filter + `industry_*` columns (A1)

- `ls_holdings_list(industry?)` — new optional filter. Case-insensitive
  substring match against the normalized FICS industry label.
  *"내 보유 중 반도체 종목"* now resolves to a `WHERE industry LIKE
  '%반도체%'`. The response carries a `matching_industries` echo
  (alphabetized distinct labels) so LIKE false positives are visible
  the same way `matched_themes` works.
- New `stocks` columns (`industry_raw`, `industry`, `industry_fetched_at`)
  populated by `t3320` (FNG_요약). `industry_raw` keeps the LS-shipped
  "FICS …" prefix verbatim; `industry` is the normalized label without
  prefix. ETF / SPAC / no-profile stocks record `industry_fetched_at`
  with NULL industry — the same "fetched-but-empty" pattern v0.7 also
  applies to stock_themes (B2) so the column doesn't loop "pending"
  forever.
- Enrichment is fire-and-forget on holdings / watchlist writes (joining
  the existing v0.6 themes path) and synchronous via
  `ls_stocks_refresh_metadata(kinds=["industry"])`. 1 TPS per symbol,
  so cold-fill ≈ N seconds for N symbols; `industry_fetched_at IS NOT
  NULL` is treated as a permanent cache hit (산업 변경은 분기/연 단위
  이벤트).

### Fixed — Storage precision (B1) + ETF perpetual-pending (B2)

- **`holdings.avg_price`** migrated from `REAL` to `INTEGER` fractional
  won (×10000) — schema v4. Public API response shape unchanged
  (`avg_price` still ships as `double`); corporate-action round-trips
  (split N then reverse_split N) now exact down to the 1/10000 won
  instead of drifting by ~1e-10 per cycle. Internal corporate-action
  API switched from `(double qtyMultiplier, double priceMultiplier)`
  to rational `(long qtyNum, long qtyDen)` so the cost basis is exact
  even for non-integer ratios (e.g. bonus(1+r)).
- **`stock_themes` sentinel row** for ETFs and SPACs that LS reports
  with an empty `t1532` array. Before: cache treated "0 rows" as "not
  yet fetched" → 60s cooldown loop forever. After: `Replace…([])`
  inserts `(symbol, "__NONE__", "")` so the cache-hit check matches
  and the `themes_status: pending` flag clears. SELECT side filters
  out the sentinel, so callers see empty themes (consistent with v0.6).

### Changed — Tier 2 compression (BREAKING, −3 tools)

LLM routing burden mitigation; service+repository layers untouched.

- **`ls_watchlist_group_rename` removed.** Renames now flow through
  `ls_watchlist_group_create(name, description?, rename_from?)`.
  When `rename_from` is set, the existing group is renamed (and
  `description` overrides if provided); otherwise the call upserts
  as before. Conflicts raise `ValidationError`.
- **`ls_watchlist_groups_list` removed.** Group-meta listing now flows
  through `ls_watchlist_list(scope?: "items" | "groups" = "items")`.
  `scope="groups"` returns `{ groups: [{name, description, sort_order,
  item_count}, …] }`; `scope="items"` keeps v0.6 behavior.
- **`ls_broker_rename` removed.** Broker label rename now flows through
  `ls_account_upsert(rename_broker_from?, broker?)`. When
  `rename_broker_from` is set, every account with that broker label
  has its broker field updated to `broker` (other args are ignored);
  otherwise the call upserts as before.

### Added — Catalog v0.7 (7 new TRs)

- `t3320` (FNG_요약 / 투자정보) under `/stock/investinfo` — FICS
  industry name + 시장구분 + 회사 프로필 + 시가총액 + 외국인비율 +
  PER/PBR/EPS/ROE 등. Internal-only (A1 enrichment source). 1 TPS;
  6-char shcode only (LS doc's "A"+6 prefix is incorrect, confirmed
  by live verify).
- `t3341` (재무순위종합) — `ls_get_fundamentals_rank` backend.
- `t1601` (투자자별 종합) under `/stock/investor` — `ls_get_investor_flow`
  intraday backend. Six unlabeled OutBlocks, twelve investor types each.
- `t1702` (외인기관 종목별 동향) under `/stock/investor` —
  `ls_get_investor_flow` daily backend.
- `t3202` (종목별 증시일정) — `ls_get_stock_events` backend.
- `t1404` (관리/불성실공시/투자유의) + `t1405` (투자경고/매매정지/
  정리매매/단기과열 …) under `/stock/market-data` —
  `ls_get_market_warnings` backend. Both use `cts_shcode` continuation.

### Deferred — v0.8 candidates

- **Dataset-handle integration for `ls_get_index_history`.** v0.7 ships
  as a thin wrapper; `ls_add_indicator` / `ls_reframe_chart` piping is
  v0.8.
- **t1511 firdiff non-self slot fix (B3).** v0.6 `d0b765e` self-entry
  override stands; the other side (other upcodes' responses that include
  KRX 100 as a related index) still ships LS-as-shipped because no
  top-level `diffjisu` is available for client-side correction. LS bug
  report filed; v0.8 will re-evaluate after LS responds.
- **FICS → KRX standard industry mapping.** v0.7 stores FICS labels
  verbatim (with `industry_raw` keeping the "FICS " prefix and
  `industry` normalized). KRX standard isn't directly published in
  LS OpenAPI; bridging is a v0.8 problem.

### Verified

- **531 tests pass on net8.0** (181 Core + 350 Mcp; was 408 at the end
  of v0.6 — net +123 across schema v4/v5 migration, sentinel-row
  semantics, FICS industry normalization, integer-arithmetic
  corporate actions, 6 new tool wrappers, and E2E polish below).
- **Live E2E (`todo/Test_v0.7.0.txt`)** against the LS 모의투자
  server caught and fixed three issues before tag:
  1. `t3341` `idx` was being serialized as a JSON string when LS
     expects a number — HTTP 500 on every call. C-1 wrapper now
     keeps `idx` as `int` end-to-end.
  2. `t1702` lives under `/stock/frgr-itt`, not `/stock/investor`
     like t1601 (LS splits [주식] 외인/기관 from [주식] 투자자).
     Catalog path corrected; the `todo/t1702.txt` reference sheet
     was staged without the URL header so v0.7 prep guessed wrong.
  3. `ls_get_market_warnings` had a cursor loop on `t1404`/`t1405`
     when the surveillance screen fit in one page — LS kept echoing
     the same `cts_shcode` and the wrapper paged 6× through the same
     rows. Fixed with per-kind seen-shcode dedup.
  4. `ls_get_investor_flow` daily mode shipped ~12k tokens for one
     30-day call (30 × 12 investor types × {kind, korean_label,
     value} objects). Diet applied: default 3 macro categories
     (`investors=["all"]` opts back in), flows array → map shape,
     redundant `sign`/`change` dropped, `summary` block added with
     period totals + per-investor extremes. ~75% token reduction.
- Live verifications also confirmed: t3320 6-char shcode behavior,
  FICS prefix consistency for two semiconductor stocks (005930 +
  000660), ETF empty-OutBlock detection. See
  `scripts/verify-t3320.ps1`.

## v0.6.0 (2026-05-16)

Market context + theme wrappers + portfolio I/O. The third big surface
release: 37 → **39 tools** (net +2 after Tier 1 compression). v0.5's
"watched sectors" naming was a misnomer — LS API splits 업종 (KRX
industry classification) from 테마 (LS curated themes); v0.6 lines up
both DB and tool surface with that split. The portfolio module also
gets its first migration story (export/import + before-import auto-backup).

### Added — Market context (3 tools)

- `ls_get_index_quote(index_code)` — single Korean index snapshot via
  TR `t1511`. Aliases `kospi`→`001`, `kosdaq`→`301`, `kospi200`→`101`,
  `krx100`→`501`. Envelope nests `value` / `previous_close` /
  `change` / `change_pct`, OHLC with timestamps, 52-week and YTD range
  blocks, market breadth (up/down/unchanged/limit), and four related
  auxiliary indices (e.g. KOSPI 종합 returns 대형주/중형주/소형주
  alongside).
- `ls_get_industry_indices(market, top_n)` — sorted top-N industry
  indices for kospi/kosdaq/all. Internally fans out `t8424` (industry
  catalog) + `t1511` per upcode; the catalog default `rate_limit_per_sec=10`
  for `t1511` (confirmed against the LS [업종] 시세 guide) keeps the
  cold-cache cost ≈2.5s for KOSPI's ~25 codes. 60-second in-process
  cache so `top_n=5` then `top_n=30` is one fanout, not two.
- `ls_get_industry_stocks(upcode | industry_keyword, market, top_n)`
  — stocks inside one industry with the industry's index summary, via
  `t1516`. Body-based continuation paging (last shcode echo). Keyword
  resolution against the cached catalog: 0 matches → `IndustryNotFound`,
  1 → `resolved.matched_via="keyword"` echo, 2+ → `AmbiguousIndustry`
  with candidates.

### Added — LS themes (2 tools)

- `ls_get_theme_stocks(theme_code | theme_keyword, top_n)` — stocks
  inside one LS curated theme + the theme's roll-up summary, via
  `t1537`. Header-based continuation paging (`tr_cont` / `tr_cont_key`
  echo). Keyword resolution same 0/1/N branches as the industry tool.
- `ls_get_stock_themes(shcode)` — every theme a stock belongs to via
  `t1532`. Empty array is a valid response — not every stock is
  pinned to a theme.

### Added — Portfolio I/O (2 tools)

- `ls_portfolio_export(path?)` — versioned JSON snapshot covering
  accounts/holdings/watchlists/watched_themes. Default path writes
  `<db-parent>/exports/portfolio-YYYY-MM-DDTHHmmss.json` so backups
  sit alongside `portfolio.db`. `stocks` and `stock_themes` caches are
  intentionally excluded — quote/theme enrichment rebuilds them after
  import.
- `ls_portfolio_import(path, mode, confirm)` — `mode=merge` (default)
  skips duplicates with explicit reason codes per domain
  (`duplicate_account_number`, `duplicate_theme_code`, …);
  `mode=replace` wipes the export-covered domains first, requires
  `confirm=true`, and silently writes a `before-import-*.json`
  auto-backup so a wrong file is recoverable. Unsupported
  `schema_version` → `ImportSchemaMismatch`.

### Added — Theme enrichment + freshness hint

- `t1532` fire-and-forget enrichment on `ls_holdings_set` / `_buy` /
  `ls_watchlist_add`. Per-stock theme memberships cached in a new
  `stock_themes` table. Best-effort: LS errors are absorbed; on stdio
  shutdown a half-finished enrichment retries on next session's first
  write.
- `ls_holdings_list` now emits a `metadata_freshness` block:
  `{ fully_enriched, pending: { themes: N }, hint }`. Each holding row
  carries `themes` (array) and optional `themes_status` (`"pending"`
  when enrichment hasn't caught up yet; omitted when `"ok"` to save
  tokens).
- `ls_holdings_list` gains optional `theme_code` (exact) and
  `theme_keyword` (LIKE on name) filters with AND-combine semantics.
  Responses include a `filter` echo and `matched_themes` (alphabetized
  unique names) so LIKE false positives are visible.

### Added — Catalog v0.6 (6 new TRs)

- `t1511` 업종현재가 (wrapper) — `rate_limit_per_sec=10`.
- `t1485` 예상지수 / `t1514` 업종기간별추이 / `t8424` 전체업종 —
  catalog-only; reachable via `ls_call_tr`. `t8424` is consumed
  internally by `ls_get_industry_indices`.
- `t1516` 업종별종목시세 (wrapper).
- `t1537` 테마종목별시세조회 (wrapper).

### Changed — Renamed (BREAKING)

- `ls_watched_sectors_{add,remove,list}` → `ls_watched_themes_{add,remove,list}`.
  Param `sector_code` → `theme_code`, `sector_name` → `theme_name`.
  v0.5 was caching LS theme tmcodes under the "sector" label; v0.6
  lines names up with the actual concept.
- portfolio.db schema **v2 → v3** migration: `watched_sectors` table
  renamed to `watched_themes` (columns `sector_code`/`sector_name` →
  `theme_code`/`theme_name`). New `stock_themes` cache table.
  v0.5 data (e.g. tmcode `0012`, `0064`) is preserved.

### Removed — Tier 1 compression (BREAKING, −5 tools)

LLM routing burden mitigation; service+repository layers untouched.

- `ls_account_get` — derive from `ls_accounts_list[].is_default`.
- `ls_account_set_default` — same effect via
  `ls_account_upsert(set_default=true)`.
- `ls_holdings_split` / `ls_holdings_reverse_split` /
  `ls_holdings_bonus` — collapsed into `ls_holdings_corporate_action(
  shcode, type, ratio, account?)`. Open enum: v0.6 supports
  `split` / `reverse_split` / `bonus`; v0.7+ adds
  `stock_dividend` / `spin_off` / `merger` by extending the enum
  without growing the tool surface.

### Deferred — v0.7 candidates

- **`stocks.krx_sector` enrichment + `industry?` filter.** SPEC §4.4
  / §10 Q7 — confirmed during v0.6 implementation that `t1102` does
  *not* return a KRX industry classification field. LS's [주식 섹터]
  category is all theme TRs. v0.6 leaves the column NULL; v0.7 will
  identify an enrichment source (LS 마스터 TR / t8424+t1516 reverse
  lookup / static KRX table).
- `ls_get_index_history` (`t1514` wrapper) — catalog-only in v0.6.
- `ls_stocks_refresh_metadata` synchronous refresh tool.

### Verified

- **230 unit + fixture tests pass on net8.0** (was 178). New coverage:
  IndustryDataCache fanout sort + cache reuse, t1516 body-paging,
  t1537 header-paging with `tr_cont_key` echo, keyword resolution
  0/1/N branches for industry + theme, schema v3 migration round-trip,
  ReplaceStockThemes / GetStockThemesBatch, EnrichStockMetadataAsync
  store + LS-error fallback, ListHoldings metadata_freshness +
  per-row themes, theme filters (exact / LIKE / AND-combine),
  portfolio I/O round-trip + replace auto-backup + schema_version
  mismatch, corporate_action dispatch + unknown-type rejection.
- **48-case stdio smoke** (`scripts/portfolio-smoke.py`) extended with
  Tier 1 compression regressions (removed tools absent from
  `tools/list`, calling a removed name returns `__rpc_error`),
  watched_themes rename, portfolio export/import round-trip with
  schema_version=99 → ImportSchemaMismatch, all 5 new tools route
  offline.

## v0.5.0 (2026-05-15)

Local-only portfolio module — multi-account holdings, buy/sell/corporate-action semantics, watchlists, watched sectors. The biggest tool-surface expansion since v0.1: 13 → 37 tools. Stored alongside `token.db`; no broker sync, no data leaves the user's machine.

### Added — Portfolio module (24 new tools)

**Accounts**
- `ls_accounts_list` — every registered account with holdings count and the default flag (empty array when no accounts exist).
- `ls_account_get` — default account, or `null` when none registered.
- `ls_account_upsert(account_number, nickname, broker, set_default)` — create or update by `account_number`. `nickname` is globally UNIQUE; first registration auto-promotes to default; `set_default=true` displaces the existing default within a single transaction.
- `ls_account_set_default(account)` — promote by `account_number` or `nickname`.
- `ls_account_remove(account, confirm)` — two-step cascade. `confirm=false` with holdings returns `RequiresConfirmation` carrying `holding_count` + `market_value` preview; `confirm=true` proceeds. When the removed account was default and others remain, the oldest account (id ASC) is auto-promoted.
- `ls_broker_rename(from, to)` — rename a broker label across every matching account (free text; no merge conflicts since nickname is the unique key).

**Holdings (account-aware)**
- `ls_holdings_list(account?)` — grouped response: `accounts[]` with per-account `summary` + a `total_summary` roll-up across all accounts. Optional `account` filter narrows to one. Each row carries a `warning` field when `current_price/avg_price` diverges 5×+ (likely missed corporate action).
- `ls_holdings_set` — replace state. `quantity=0` is `ValidationError` ("use ls_holdings_remove").
- `ls_holdings_buy(shcode, quantity, price)` — incremental buy with weighted-average merge: `new_avg = (old.qty*old.avg + qty*price) / (old.qty + qty)`.
- `ls_holdings_sell(shcode, quantity)` — partial sell; row auto-removes when remaining quantity reaches zero; raises `InsufficientQuantity` above the position with `applied_to` echo.
- `ls_holdings_remove` — drop a row outright; `removed=false` when not held anywhere.
- `ls_holdings_split(ratio)` / `_reverse_split(ratio)` / `_bonus(ratio)` — corporate actions. With no `account` specified, applied across **every account holding the symbol** (single corporate event affects all owners). Reverse-split rejects non-divisible quantities with `ValidationError`; bonus rejects non-integer results from a single-share holder.

**Watchlists**
- `ls_watchlist_groups_list` / `_group_create` / `_group_delete` / **`_group_rename`** (new).
- `ls_watchlist_add` / `_remove` / `_list` — items inside groups; list enriches with live quotes when credentials are present, falls back to `quote_error` envelope otherwise.

**Watched sectors / themes**
- `ls_watched_sectors_add` / `_remove` / `_list` — `t1531` theme codes with avg percent change. 60-second in-process cache on the theme table so adding multiple watched themes doesn't burn the LS rate limit.

### Added — Cross-cutting

- **Multi-account ambiguity policy.** Reads fall back; writes require an explicit target when ambiguous. Documented in [docs/SPEC-portfolio-multi-account.md](docs/SPEC-portfolio-multi-account.md).
  - 0 accounts → `RequiresAccount` error.
  - 1 account → auto with `applied_to` echo.
  - 2+ accounts → `AmbiguousAccount` with `candidates[]` for set/buy. For sell/remove, only ambiguous when the symbol exists in multiple accounts.
- **`applied_to` echo on every write response.** Single `{account_number, nickname, broker, is_default}` for one-account writes; array of before/after snapshots for corporate actions across multiple accounts. Safety net for the soft single-account fallback.
- **Typed error envelopes** — `RequiresAccount`, `AmbiguousAccount`, `AccountNotFound`, `RequiresConfirmation`, `InsufficientQuantity`, `ValidationError`. Each carries structured fields (candidates, identifier, holding count, market value, current/requested quantity) so the LLM can re-call with the correct argument without prompting the user.
- **`LSOPENAPI_DB_PATH` env var** — override the local portfolio SQLite path. Defaults to `%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db` (next to `token.db`).
- **6-character alphanumeric stock codes.** Validation relaxed across `ls_get_multi_quote` and the portfolio tools so ETF codes with an uppercase letter (e.g. TIGER 코리아AI전력기기TOP3 = `0117V0`) are accepted. Lowercase input is uppercased on the way in so storage stays case-insensitive.
- **Split/bonus warning on `ls_holdings_list`.** When `current_price / avg_price` diverges by 5× or more in either direction, the holding row carries `warning: "분할/무상증자 가능성: 현재가/평단 비율 N배. 분할 도구로 보정하세요."`.

### Changed

- **Tool count 13 → 37.** New tools listed above.
- **`ls_holdings_list` response shape.** Always grouped (`accounts: [...]` + `total_summary`); single-account responses have length 1. Field renames: `current_value → market_value`, `total_cost → cost_basis`.
- **Schema migration v2** on the local SQLite store. Drops the v0.4 placeholder `'UNSET' / '기본 계좌'` account when no holdings reference it, and adds `UNIQUE(nickname)`. The zero-account empty state is now valid and surfaced as `RequiresAccount` on first write.

### Removed

- **`ls_account_set`** (single-account default updater). Replaced by `ls_account_upsert` — same first-run UX, but explicit about creating named accounts.
- **`ls_holdings_add`** / **`ls_holdings_update`**. Replaced by `ls_holdings_set` (replace), `_buy` (weighted-average merge), `_sell` (subtract + auto-remove). The split makes the intent explicit in the tool name so the LLM routes by meaning rather than guessing whether an "add" call meant "more shares bought" or "current state is now this".

### Verified

- **178 unit + fixture tests pass on net8.0** (up from 157), including new multi-account / weighted-average / split-divisibility / cascade-confirmation / default-succession cases.
- **28-case stdio smoke** (`scripts/portfolio-smoke.py`) covers empty state, account upsert + default toggle, nickname collision, multi-account ambiguity, weighted-average buy, sell with auto-remove + over-sell guard, `set(qty=0)` validation, grouped holdings_list with total_summary, split across all holders, non-divisible reverse-split, two-step account cascade with auto succession, broker rename, watchlist group rename. Live-verified against the LS real server.
- **E2E via Claude Code** against the live LS API (12 natural-language scenarios): multi-account registration in one turn, "유안타 LG전자 24주 익절", "민테크 10:1 분할" auto-propagated to both accounts, "유안타증권 계좌 지워줘" with `RequiresConfirmation` → confirm → cascade + auto-succession to 카카오페이.

## v0.4.0 (2026-05-15)

Token-efficient chart payloads, two follow-up tools that operate on a dataset handle, a ZigZag-based swing detector for `key_turns`, and an `IndicatorCoverage` block that lets the model explain why an indicator is null.

### Added

- **`ls_add_indicator`** — follow-up tool that adds an indicator (e.g. `"ma:200"`) to an existing `dataset_id` and refetches the frame with that indicator's warm-up window. Updates the same `dataset_id`, returns the new summary + optional Plotly chart spec. Lets the model honor *"MA200도 추가해줘"* without sending raw OHLCV back through context.
- **`ls_reframe_chart`** — follow-up tool that reframes a dataset to a different period/count using the cached symbol + indicator specs. Replaces the current view in the same `dataset_id` so a subsequent `ls_add_indicator` doesn't need a `period_type` argument.
- **`output_mode` on `ls_get_chart`** — `display` | `analyze` | `export` | `reference`. `display`/`analyze`/`reference` keep raw OHLCV and full indicator arrays out of the model's text; only `export` returns them. Defaults to `display` when `include_chart=true`, otherwise `analyze`. Existing `summary_only` stays as a legacy flag and maps to `analyze` when `output_mode` is omitted.
- **`dataset_id`** is returned on every successful chart call — opaque `ds_*` handle backed by a process-local LRU (16 datasets, 5 MB per dataset). Used by the two follow-up tools.
- **`with_warmup`** — explicit opt-in for the analytical-summary warm-up policy. `null` (default) auto-applies warm-up when `from` is unspecified and skips it when `from` is given; `true` forces warm-up even with explicit `from` (analyze long-period indicators inside a narrow window); `false` skips warm-up even when `from` is null (fastest, narrowest read). The tool description spells out the three cases so the model toggles it without being told.
- **`summary.coverage`** on every chart response — per-indicator availability (`MA5`..`MA200`, `ma60_slope`, `change_1y`, `change_5y`, `key_turns`) reported as `ok` / `insufficient_data` / `disabled`, plus `warmup_applied`, `analytical_bar_count`, `display_bar_count`, and a human-readable `note` when something is missing. The narrow-window `note` literally tells the model *"pass with_warmup=true to populate them"* so it can self-correct in the next turn.

### Changed

- **`ls_get_chart` text payload is summary-first.** The text content now carries `dataset_id` + `summary` + (for `analyze`) the existing `context` block; raw `candles` and full indicator arrays are present only when `output_mode='export'`. Plotly chart specs continue to ship via `structuredContent.chart` — zero token cost. Long-range chart requests no longer blow tens of thousands of tokens into the conversation.
- **`AnalyticalSummary` is computed over a warm-up-inclusive series**, not the trimmed display window. Default warm-up: 240 day bars / 120 week+month / 200 min / 10 year. With `count=60`, `summary.moving_averages` now includes a populated `MA200`, `ma60_slope` is non-null, and `change_pct1_y` is populated — without changing what the user sees on the chart.
- **`key_turns` use a threshold-reversal ZigZag** instead of a 5-bar fractal. Reversal triggers on the close (not on intrabar high/low), so a single wide-range bar can't self-trigger a spurious pivot. Period-aware percent thresholds: day 4%, week 8%, month 12%, year 20%, min 1.5%, tick 1%. Pivots strictly alternate peak/trough; the trailing pivot is `is_confirmed=false` at the latest bar and represents the in-progress swing. The fix also closes a stale-index bug in the old fractal detector that could emit duplicate pivots on the same bar.
- **`InflectionPoint` shape**: `(date, price, kind: peak|trough, change_pct_from_prev, is_confirmed)` — `kind` is now a typed enum (serialized as `"peak"` / `"trough"` via a snake-case enum converter), and each turn carries its leg size + confirmation status.
- **MA60 slope** (`rising`/`flat`/`falling`) is classified by a least-squares fit over the lookback window of MA values, not a two-point delta. A single noisy endpoint no longer flips the verdict.

### Tool count

- **11 → 13** (added `ls_add_indicator`, `ls_reframe_chart`).

### Verified

322 unit and fixture tests pass on .NET 8. Live-verified end-to-end against the LS real-market server: default summary populates `MA5..200` + slope + 1Y change over a 300-bar analytical window; narrow explicit-`from` window with `with_warmup=true` forces padding and re-populates long indicators; `with_warmup=false` skips it (`coverage.note` then guides the model to re-call with `true`); `output_mode='export'` brings back raw OHLCV; `ls_add_indicator(ma:200)` preserves `dataset_id` and returns the latest value; `ls_reframe_chart` swaps day→week in-place; ZigZag pivots on 035720 monthly alternate strictly with a trailing tentative pivot.

## v0.3.0 (2026-05-14)

A new market-screener tool, a search-parameter rename, and Naver-style chart polish.

### Added

- **`ls_get_top_stocks`** — market-wide ranking screener wrapping five TRs behind one `kind` parameter: `gainers` / `losers` / `unchanged` (t1441), `market_cap` (t1444), `volume` (t1452), `amount` (t1463), `volume_surge` (t1466). Supports `market` (all / kospi / kosdaq), `basis` (today / previous_day), `exchange` (unified / krx / nxt), and price/volume floor filters; paginates via the LS `idx` continuation key and merges KOSPI + KOSDAQ for `market_cap` when `market=all`. Brings the tool count to 11.

### Fixed

- **`ls_get_top_stocks` price filters were silently ignored.** LS's ranking TRs (t1441/t1452/t1463/t1466) treat `eprice=0` as "no price filter" and — the non-obvious part — suppress the `sprice` floor along with it. So `min_price` with no `max_price` returned the full unfiltered list. The tool now sends the 8-digit ceiling (`99999999`) as `eprice` whenever a `min_price` floor is set without a `max_price` cap. `min_volume` and the both-bounds case were already correct. Verified end-to-end against the live LS server. (Sending the fields as JSON strings instead of numbers, an early hypothesis, makes LS return HTTP 500 — the field types were never the problem.)

### Changed

- **`ls_search_stock`'s `query` parameter renamed to `keyword`.** `ls_search_tr` already took `keyword`, and models generalize the sibling tool's parameter name — so `ls_search_stock` calls came in with `keyword` and were rejected by the .NET MCP SDK's parameter binder with an opaque *"An error occurred invoking 'ls_search_stock'."* (the real "missing required parameter" detail only reaches the server's stderr log). Both search tools' keyword parameters are now also optional at the protocol level, so a missing or misnamed argument reaches the in-body validation and returns a clear *"keyword is required."* instead.
- **`ls_get_chart` gained an optional `name` parameter.** When supplied, the inline chart title reads *"삼성전자 (005930) — 일봉"* instead of just the code. The chart TRs do not carry the stock name, so the caller passes it through; omitted, the title falls back to the code as before.
- **Inline chart specs polished toward the Naver Finance look.** Candlestick x-axis labels are now an evenly-spaced ~8-tick subset (`MM/dd`) rather than one label per candle; MA / EMA overlays use the Korean retail palette (green / red / orange / purple); the period high and low get `최고` / `최저` annotations; the ETF-holdings treemap uses white labels on a deeper blue so they stay legible regardless of the host theme.

### Internal

- The Plotly chart-spec builders moved into `RedoxNet.LsOpenApi.Core` (see the Core 0.3.0 notes) — no effect on the tool surface.

## v0.2.0 (2026-05-14)

MCP host interoperability fixes. **No tool surface or behavior changes** — the 10 tools, their inputs, and their outputs are identical to v0.1.0. This release only reshapes the published JSON schema and UI-resource metadata so MCP hosts accept and render them correctly.

### Fixed

- **Optional array/string parameters were rejected by strict MCP host validators.** The .NET MCP SDK emits JSON Schema 2020-12's `"type": ["array","null"]` form for nullable parameters. Claude Desktop, Claude Code, and cowork reject that shape, so every `ls_get_chart` call that passed `indicators` failed before reaching the server (`from` / `to` were affected the same way). A `tools/list` request filter — `SchemaNormalizer` — now rewrites `["X","null"]` → `"X"` and drops the orphaned `default: null`, leaving the C# `T?` signatures untouched. Verified end-to-end against the live LS server.

- **MCP Apps (SEP-1865) UI-resource CSP metadata was the wrong shape.** `_meta.ui.csp` used raw CSP directive names (`script-src`, `style-src`, …); the spec expects domain lists (`resourceDomains`, `connectDomains`). Hosts read `csp.resourceDomains` to allowlist the Plotly CDN inside the sandbox iframe — with the old shape the CDN was never allowed. The `_meta` block is now also attached to the `resources/read` content (the host reads CSP from there, not only from the `resources/list` entry), `prefersBorder: true` is declared, and the HTML template declares `availableDisplayModes: ["inline"]` in its `ui/initialize` handshake.

### Added

- **`LS_LOG_LEVEL` environment variable** — sets the minimum log level (`Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` / `None`, default `Information`). With `Trace`, every JSON-RPC message — including full `tools/call` payloads — is written to stderr, for diagnosing host-side interop issues.

### Host support note

Inline Plotly rendering still depends on the host. It works on Claude.ai (web). Claude Desktop / cowork currently cannot embed the MCP Apps sandbox iframe — the `claudemcpcontent.com` sandbox serves a CSP `frame-ancestors` that lists only the web origins, not the desktop app — so on those hosts the tool degrades gracefully to the structured `candles` / `indicators` / `context` payload. This is an Anthropic-side limitation, not a server issue.

## v0.1.0 (2026-05-13)

Initial public release of the MCP server for LS증권 OpenAPI.

### Tools (10)

- **Meta** — `ls_search_tr`, `ls_describe_tr`, `ls_call_tr`. `ls_call_tr` accepts both real JSON objects and JSON-stringified `in_block` payloads as a robustness fallback for clients whose MCP schema inference omits `type: object` on the `JsonElement` parameter.

- **Quotes & info**
  - `ls_get_quote` (t1101) — current price + 10-level order book + session OHLC.
  - `ls_get_multi_quote` (t8407) — up to 50 stocks per call.
  - `ls_get_stock_info` (t1102) — PER/PBR/EPS, quarterly financials, 52-week + YTD ranges, top-5 buy/sell brokerages, foreign-investor activity, SPAC / 관리종목 flags.

- **Charts** — `ls_get_chart` (t8410 / t8412 / t1301)
  - Period types: `day` / `week` / `month` / `year` / `min` / `tick`. Comma-separated period strings (`"day,week,month"`) return a `frames[]` array — one frame per timeframe, each with its own candles / indicators / context.
  - Optional technical indicators: `ma:N`, `ema:N`, `rsi:N`, `macd:F,S,Sig`, `bb:N,SD`.
  - Pre-computed `context` block: `divergence_from_ma`, `volume.{avg20,ratio20,avg60,ratio60}`, `drawdown.{period_high,date,pct}`, `ma_trend`, tristate `bullish_alignment` (`null` when MA warm-up makes the stack undecidable).
  - `include_chart=true` attaches a Plotly v5 JSON spec under `chart.spec`. Korean broker color convention applied (rising = red, falling = blue). `ma`/`ema`/`bb` overlay the price subplot; `rsi`/`macd` are emitted in `indicators` but not plotted (would need separate subplots).
  - `summary_only=true` keeps only the last 5 candles + final indicator scalars while preserving the full context — useful for multi-timeframe screening passes that would otherwise blow past inline token budgets.

- **Discovery & ETF**
  - `ls_search_stock` (t8436) — name → code search with an `instrument` filter (`all` / `stock` / `etf`).
  - `ls_get_etf_info` (t1901) — NAV + divergence (괴리율, from `kasis`) + tracking error (추적오차율, from `cocrate`) + reference index (참고지수 — e.g. KOSPI 200 for KODEX 200) + AUM + LP list + 52-week / year ranges + related futures. Foreign-ownership ratio (소진율) surfaced as `foreign_ownership_percent`. `listing_shares` converted from LS's 천-주 unit to raw share count.
  - `ls_get_etf_holdings` (t1904) — PDF (구성종목) array sorted by weight, with a top-N cap (`top_n=10` etc.) for ETFs with 200+ constituents. Summary block always reflects the full ETF (NAV, AUM, constituent count, cash); only the holdings array is truncated. The InBlock correctly sends LS's required `date` + `sgb` fields — omitting either produces a misleading `rsp_msg="해당자료가 없습니다"` response.

### Credentials

- **Environment variable only.** `LS_APPKEY` and `LS_APPSECRETKEY` are accepted via the process environment only — never through chat, tool arguments, or MCP elicitation. This is the strictest reading of the MCP spec's *"Servers MUST NOT use elicitation to request sensitive information"* guidance. Rationale is documented in [`docs/ADR-001-credential-management.md`](docs/ADR-001-credential-management.md).
- Logs, errors, and tool responses never echo credentials. Diagnostics show `****` + the last four characters of the app key; the secret key is never logged in any form.

### Packaging

- dotnet tool — `dnx RedoxNet.Mcp.LsOpenApi`. Dual-targeted `net8.0` + `net9.0`.
- MCP Server package type with `.mcp/server.json` for the registry. MSBuild `VerifyServerJsonVersion` target catches drift between csproj `<Version>` and `.mcp/server.json` at pack time.

### Out of scope (v0.1.0)

- Real-time (WebSocket) subscriptions — planned for v0.2.x (separate package `RedoxNet.Mcp.LsOpenApi.Realtime`).
- Account balance, order history, unfilled orders (read-only) — planned for v0.2.x.
- Order placement (매수 / 매도) — explicitly deferred to a future major release with elicitation-gated confirmation; this v0.1.x line stays read-only.

### Verified

Live-verified against the LS 모의투자 server on 2026-05-13: current quote (Samsung), multi-timeframe chart (SK하이닉스 일·주·월 with `ma:5/20/60`), search-then-quote (카카오), bio-only ETF discovery (`instrument="etf"`, 16 results), KODEX 200 PDF (201 holdings, AUM ≈ 25조원), and KODEX 200 ETF info (NAV / 괴리율 0.00% / 추적오차율 0.01% / KOSPI 200 reference / 외인 보유율 23.39%). 234 unit and fixture tests pass on .NET 8 and .NET 10.
