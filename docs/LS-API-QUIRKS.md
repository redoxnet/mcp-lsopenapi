# LS OpenAPI — quirks & data anomalies

Behaviors of the LS증권 OpenAPI (REST) that are undocumented, internally
inconsistent, or outright wrong — and how this project works around them.
Recorded so the same surprises are not re-investigated every session.

Each entry: **symptom → cause (as understood) → workaround → status**.

Status legend: ✅ handled · ⚠️ partially handled · 🔲 open (backlog).

Last updated: 2026-05-22.

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
