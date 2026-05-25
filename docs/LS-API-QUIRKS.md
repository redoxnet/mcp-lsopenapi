# LS OpenAPI — quirks & data anomalies

Behaviors of the LS증권 OpenAPI (REST) that are undocumented, internally
inconsistent, or outright wrong — and how this project works around them.
Recorded so the same surprises are not re-investigated every session.

Each entry: **symptom → cause (as understood) → workaround → status**.

Status legend: ✅ handled · ⚠️ partially handled · 🔲 open (backlog).

Last updated: 2026-05-26.

---

## 1. Field encoding & formatting

### 1.1 `hname` is a fixed-width 20-byte field ⚠️

**TRs:** `t8424` (전체업종), `t1511` (업종현재가); the same padding also
appears in broker-name fields and ETF names in other TRs.

The `hname` column is fixed-width — the t8424 spec declares it `String, 20`.
LS fills the 20 bytes two different ways:

- **Short names** are padded with a space *between every character* —
  `"종       합"`, `"K R X 1 0 0"`, `"대   형  주"`.
- **Long names** overflow 20 bytes and are **truncated mid-character**.
  The dangling partial byte decodes to U+FFFD (`�`): e.g.
  `"KP200 정보기술 레버리지"` → `"KP200 정보기술 레버�"`.

A truncated name is unrecoverable — LS never sends the missing tail, and
t8424 and t1511 truncate identically.

**Workaround:** `GetIndexQuoteTool.CompactName` strips all internal
whitespace and every U+FFFD. Used by `ls_get_index_quote` and, since
v1.0.0, by `IndustryDataCache` (`ls_get_industry_indices`).

**Status:** ✅ for index/industry names, and `t1636` stock names
(`GetProgramTradingTool.CleanName`, v1.1). 🔲 The same padding/truncation
appears in **broker names** (`"I B K"` instead of `IBK`) from `t3401`
(analyst opinions) and in **ETF names** from the `t1466` screener
(`"HANARO 26-12 은행채("` — cut off); those fields are not yet normalized.

---

## 2. Catalog & classification

### 2.1 `t8424` returns far more than 업종 ✅

**TR:** `t8424` (전체업종), input `gubun1`.

`gubun1` is undocumented (spec marks it required, `String, 1`, no enum).
Observed behavior:

- `gubun1=""` → the **full index zoo** (~252 rows): real 업종 *plus*
  KP200/F-K200 leveraged & inverse sector indices, composites such as
  `"KQ150 L KP200 0.5 S"`, etc.
- `gubun1="1"` → KOSPI side, `gubun1="2"` → KOSDAQ side — but **both
  still include** KP200 / KP50 GICS sector indices and market-cap
  composites (KOSPI50/100/200, F-KOSPI200, KOSDAQ100/150/글로벌).

So no single `gubun1` value yields a clean 업종 list. Upcode prefixes do
not separate them cleanly either (KOSDAQ composites such as `KOSDAQ100`
share the `3xx` range with KOSDAQ industries; `KOSDAQ150` is `4xx`). The
reliable signal is the **name**: real 업종 are plain Korean sector names;
every index product carries a Latin index-family prefix — `KP` (KP50 /
KP100 / KP200), `KOSPI`, `KOSDAQ`, `KRX`, `F-`, `VKOSPI`. Korean
industries that merely *start* with Latin letters (e.g. `"IT서비스"`) are
**not** products and must be kept.

**Workaround:** `IndustryDataCache` (v1.0.0) fetches `gubun1="1"` +
`gubun1="2"` and merges, never `gubun1=""`, then drops rows whose name
starts with a known index-family prefix (`IsDerivedIndexName`).

**Also observed — intermittent empty leg.** The two back-to-back t8424
calls occasionally return one leg (often `gubun1="2"`/KOSDAQ) with an
empty `t8424OutBlock` — a transient hiccup, not a business error.
v1.0.0 retries an empty leg once; if it is still empty, the partial
board is returned with the gap named in `partial_error` rather than
silently dropped.

**Status:** ✅ since v1.0.0.

### 2.2 `t3320` is the only stock → industry source 🔲

**TR:** `t3320` (FICS 산업분류).

The only LS endpoint that maps an individual stock to its industry.
Quirks:

- Industry names carry a literal `"FICS "` prefix (`"FICS 반도체 및
  관련장비"`).
- Accepts a **6-character `shcode` only**.
- ETFs and SPACs return an **empty result silently** (no error).

**Status:** 🔲 known; callers handle the empty case. Documented here so
the `"FICS "` prefix and 6-char constraint are not rediscovered.

---

## 3. Numeric / value anomalies

### 3.1 `t1511` `change` is computed against a stale base ✅

**TR:** `t1511` (업종현재가).

For at least 전기전자 (`upcode 013`) the `change` field is wrong:
`pricejisu` (value) and `diffjisu` (percent) are mutually consistent,
but `change` is not. Confirmed across two calls — `value − change`
stayed constant at `14535.93`, i.e. LS subtracts a **frozen base**
instead of the real previous close (an index-rebase remnant).

Example: value `24416.59`, `diffjisu` `8.63%` → real change ≈ `+1,940`,
but the `change` field reports `+9,880.66`.

**Workaround:** `IndustryDataCache` (v1.0.0) ignores the `change` field
and derives it: `change = value × pct / (100 + pct)`. `diffjisu` and
`pricejisu` are trusted; the `change` field is not.

**Status:** ✅ since v1.0.0 (for `ls_get_industry_indices`).

### 3.2 `t1511` firdiff self-entry ships at 1/10 scale ✅

**TR:** `t1511` (업종현재가), related-index block.

When a related-index entry's `firstjcode` equals the queried `upcode`
(the index referencing itself), its `firdiff` value is delivered at
**1/10 of the true scale**.

**Workaround:** v0.6 (`d0b765e`) — `GetIndexQuoteTool` substitutes the
top-level `diffjisu` for the self entry instead of using `firdiff`.

**Status:** ✅ since v0.6.

### 3.3 `/stock/program` amount units differ per TR ⚠️

**TRs:** `t1662` (시간대별 추이), `t1633` (기간별 추이), `t1636` (종목별 동향),
`t1637` (종목별 추이).

All four live under `/stock/program` and report 금액 when `gubun1`
selects the amount basis, but the **unit differs**:

- `t1662` / `t1633` ship amounts in **백만원**. t1662 verified against
  Naver (`2,042,794` → `20,428` 억원); t1633's daily `tot3` for the same
  session (`2,038,664`) matches t1662's end-of-day cumulative.
- `t1636` / `t1637` ship program amounts (`svalue` / `offervalue` /
  `stksvalue`) in **천원**. t1636 verified against its own
  `mkcap_cmpr_val` (= net buying ÷ market cap): SK `svalue` `6,726,820`
  천원 = `67.3` 억 ≈ `0.02%` × `sgta` `448,067` 억. t1637 verified
  against `svolume` × price (삼성전자 intraday `svalue` `-210,133,974`
  천원 = `-2,101` 억 ≈ `708,026` shares × ~297,000원). `sgta` in t1636
  is itself in **억원**.

`gubun1` polarity is **inconsistent**: t1636 / t1637 use `0=수량 1=금액`,
t1662 / t1633 use `0=금액 1=수량`.

⚠️ t1637's **daily** `svolume` does not match the same day's intraday
cumulative `svolume` and is not a plain share count — only the 금액
fields (`svalue` etc.) are trustworthy across both t1637 periods.

⚠️ t1637 **intraday** (gubun2=0) only ever returns the **current**
session — the InBlock `date` field is ignored, so a past day's minute
series cannot be retrieved.

**Workaround:** `GetProgramTradingTool` converts t1662 / t1633 백만원 ÷ 100
and t1636 / t1637 천원 ÷ 100,000 onto the 억원 scale the charts render in;
stock scope (t1637) surfaces amounts only.

**Status:** ✅ since v1.1.

### 3.4 Overseas chart `comp_yn="Y"` corrupts price bytes ✅

**TRs:** `g3204` (해외주식 일주월년별 차트), `g3203` (해외주식 분봉),
`g3202` (해외주식 틱). InBlock `comp_yn` (압축여부, `Y` = 압축,
`N` = 비압축).

Asking LS for compressed delivery (`comp_yn="Y"`) on overseas chart
TRs corrupts the response: the price fields come back with control
bytes prefixed to the decimal digits, and LS rejects its own payload
mid-stream with `rsp_cd=IGW40014`. The uncompressed path
(`comp_yn="N"`) returns clean OHLCV:

```
id=[시가(open)] in.data=[ 190.8400(<] dPoint=[8]
Error=Character   is neither a decimal digit number, decimal point,
nor "e" notation exponential mark.
```

Confirmed 2026-05-26 on NVDA `g3204` daily: `comp_yn="Y"` HTTP 500,
`comp_yn="N"` returns 30 clean daily candles. The KR chart wrapper
(`GetChartTool`) has always used `"N"`; the overseas wrapper was
coded with `"Y"` and the bug never surfaced in unit tests because the
test fixtures hardcode the response shape.

**Workaround:** `OverseasStockTools` forces `comp_yn="N"` on all three
overseas chart TRs. A test pins `\"comp_yn\":\"N\"` on the request
body so a regression to `"Y"` fails CI.

**Status:** ✅ since v1.3.0 (fixed during release-prep E2E).

---

## 4. Screener semantics (surprising, not bugs)

### 4.1 `t1466` volume-surge % explodes on thin instruments 🔲

**TR:** `t1466` (거래량 급증), exposed via `ls_get_top_stocks`
`kind="volume_surge"`.

`volume_surge_percent` is today's volume against a small `reference_volume`.
For thinly-traded instruments (short-term bond ETFs, etc.) the reference
can be a handful of shares, producing values like `82,257%`. This is
arithmetically correct but ranks low-information instruments to the top.

**Status:** 🔲 not a defect — LS screener semantics. Consumers should not
read `volume_surge_percent` as a meaningful magnitude; treat it as a
coarse "unusually active" flag. The model is expected to note ETF/bond
inclusion when summarizing.

### 4.2 `t1825` / `t1826` return `rsp_cd=""` on success ✅

**TRs:** `t1825` (종목Q클릭검색 실행), `t1826` (종목Q클릭검색 리스트조회) —
the Q-Click / 씽큐스마트 saved-screener pair.

LS's own response example in the official spec shows a successful
response with `"rsp_cd": ""` (empty string) and `"rsp_msg": ""` — not
the usual `"00000"` / `"정상"` envelope. Every other documented TR uses
`"00000"` on success, so a strict `RspCode == "00000"` success check
mis-classifies these two as business-level errors and discards the
payload.

**Workaround:** `ScreenerTools.IsScreenerSuccess` treats an empty
`rsp_cd` plus a present output block (`t1825OutBlock1` or
`t1826OutBlock`) as success. Non-empty error codes still surface
normally, and `LsTrResponse.IsSuccess` was left untouched so other
TRs are unaffected.

**Status:** ✅ v1.4-dev — discovered during user E2E ("Break_Above_MA20"
saved condition; `ls_list_screeners` initially returned `rsp_cd=""`
errors). Defensive unit tests cover both the quirk and the
hypothetical `"00000"` future-fix path.

### 4.3 "Q-Click / 씽큐스마트" is LS-curated, not user-authored ✅

**TRs:** `t1825`, `t1826` (Q-Click signal pair) + HTS screens [1801]
((KRX)종목Q클릭검색) and [1892] ((KRX)조건검색).

The naming suggests "saved by the user," but the t1825/t1826 surface
exposes **LS's own curated signal catalog** — not user-authored
conditions. As of 2026-05-24 the catalog is 87 signals across four
groups:

- `search_gb=0` 핵심검색 → 6001–6023 (23 signals: 이평밀집정배열, 쌍바닥형, 스윙트레이딩매수, …)
- `search_gb=1` 지표검색 → 6101–6133 (33: 골든크로스/이평/MACD/Stochastic, …)
- `search_gb=2` 시세동향 → 6201–6216 (16: 상한가 패턴, 14시 이후 돌파, …)
- `search_gb=3` 투자자동향 → 6301–6315 (15: 외인/프로그램/거래원, …)

Every account sees the *same* catalog from the first call; there is no
write-back path. The "API보내기" button on HTS [1892] (the visual
condition builder for user-authored expressions) ships matched stock
codes to *other HTS screens* (관심종목, 주문창), not to this OpenAPI
surface — user-authored conditions are therefore not reachable via
t1825/t1826 in v1.4. The "급변종목" group visible in HTS [1801] is also
out of the 0–3 enumeration (likely surfaced by a separate TR family
already covered by `ls_get_top_stocks(kind=...)`).

**Workaround:** none — this is product semantics, not a defect. v1.4
tooling (`ls_list_screeners`, `ls_run_screener`, `ls_combine_screeners`)
and SPEC §3 are written against the LS-curated catalog assumption. Tool
descriptions explicitly call out that HTS [1892] conditions are a
separate system. `ls_combine_screeners` is the user-facing compensation
for the LS-curated constraint — it lets the model combine N catalog
signals via shcode set operations (AND / OR), expressing compound
conditions that no single HTS screen can.

**Status:** ✅ documented in [`SPEC-v1.4.md`](./SPEC-v1.4.md#3-슬라이스-b--q-클릭-시그널-카탈로그-노출)
§3 and reflected in `ScreenerTools` tool descriptions. The original
v1.4 kickoff frame ("expose user-saved conditions") is preserved as a
postmortem in SPEC §3.6.

### 4.4 `t1826` `search_gb=4` works but is undocumented ✅

**TR:** `t1826` (종목Q클릭검색 리스트조회 / 씽큐스마트).

LS's official spec doc (`todo/[주식] 종목검색_t1826.txt:26`) enumerates
`search_gb` values 0..3 (핵심검색 / 지표검색 / 시세동향 / 투자자동향) —
the same four groups in the documented response example. HTS [1801]
((KRX)종목Q클릭검색) however shows a 5th group, **급변종목**, with 12
intra-minute signals (가격급등/급락 × 4 bar widths, 거래량급증 × 4 bar
widths).

v1.4-dev E2E (2026-05-25) confirmed that calling `t1826` with
`search_gb=4` returns those 12 signals — ids **6401–6412** — even
though the spec doc never mentions them. Total catalog with the 5th
group: 99 signals (23 + 33 + 16 + 15 + 12).

**Workaround:** `ScreenerTools.Groups` includes `search_gb=4` and
`FetchScreenersAsync` wraps the call in a try/catch + IsScreenerSuccess
safety net so a *future* server-side rejection (if LS ever decides to
hide the group) downgrades to a silent skip rather than breaking the
whole catalog fetch.

**Status:** ✅ catalog exposure complete; the group is named
`rapid_change` (English) / `급변종목` (Korean label) in
`ls_list_screeners` / `ls_run_screener` / `ls_combine_screeners`.
Note: most rapid-change signals are also reachable through dedicated
screener TRs we already wrap (`t1442` etc., surfaced via
`ls_get_top_stocks(kind="volume_surge")`); the Q-Click variants run on
shorter minute buckets and complement rather than duplicate.

---

## 5. Environment & data-source notes

### 5.1 The virtual (모의투자) server returns real market data

`LS_MARKET=virtual` returns **real** quotes, indices, charts, and
screener results — identical to `real`. Only orders and account balances
are synthetic. Never hypothesize that virtual prices differ from real;
they do not. (v1.0.0 defaults `LS_MARKET` to `real` regardless.)

### 5.2 Testbed JSON samples are static

The LS developer-site testbed returns **canned documentation samples**,
not live data. Use them to pin response *shapes* only — never as a source
of value truth or for behavioral assumptions (e.g. `gubun1` semantics in
§2.1 could not be derived from the testbed and required live calls).

---

## 6. Continuation & paging

### 6.1 `g3190` body cursor ignored without `tr_cont: Y` header ✅

**TR:** `g3190` (해외주식 마스터). Body cursor field: `cts_value`.

Catalog declares `g3190` as a body-cursor-paged TR
(`"continuation": { "supported": true, "key_fields": ["cts_value"] }`),
but LS silently treats every request as the **initial page** unless the
HTTP header `tr_cont: Y` is also set. Symptom: the response's
`cts_value` echoes the request's `cts_value` unchanged, and rows on
"page 2" are byte-for-byte identical to page 1, so a paging loop
scans the first 500 rows forever. Confirmed 2026-05-26 by issuing two
consecutive `ls_call_tr` calls — `cts_value=""` and
`cts_value="0000000000000501"` returned the same first-page rows
starting at `AACB`.

**Workaround:** `OverseasStockTools.SearchOverseasStock` threads the
returned cursor back through `LsApiClient.CallTrAsync`'s
`continuationKey:` parameter, which sets `tr_cont: Y` (and a redundant
`tr_cont_key`). The body `cts_value` is still sent for catalog
conformance. A unit test pins both `tr_cont: N` on the first request
and `tr_cont: Y` on the continuation so a regression to body-only
paging fails CI.

**Likely scope:** Other body-cursor-only TRs in the catalog
(`{ "supported": true, "key_fields": [...] }` with no header
declaration) probably share this requirement. The general fix is
"always pass the cursor through `continuationKey:`" — header-paged
TRs already do, this just brings body-paged TRs in line.

**Status:** ✅ for `g3190` since v1.3.0. 🔲 broader audit deferred — the
v1.3 wrapper that hit it is currently the only paging path that loops
in production.
