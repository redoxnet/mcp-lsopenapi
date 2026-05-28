# LS OpenAPI — quirks & data anomalies

Behaviors of the LS증권 OpenAPI (REST) that are undocumented, internally
inconsistent, or outright wrong — and how this project works around them.
Recorded so the same surprises are not re-investigated every session.

Each entry: **symptom → cause (as understood) → workaround → status**.

Status legend: ✅ handled · ⚠️ partially handled · 🔲 open (backlog) ·
💭 investigated but not productized (kept here so the investigation is not repeated).

Last updated: 2026-05-27.

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

### 4.2b /stock/accno family returns non-`00000` success codes ✅

**TRs:** the `/stock/accno` family — `CSPAQ12200` / `CSPAQ22200`
(예수금/총평가), `CSPAQ12300` (BEP), `CSPAQ13700` (주문체결내역),
`CSPAQ00600` (신용한도), `CSPBQ00200` (주문가능수량), `CDPCQ04700`
(거래내역), `FOCCQ33600` (기간별 수익률).

LS's docs and live traffic for these TRs return non-`"00000"` success
codes alongside "조회가 완료되었습니다." or "조회내역이 없습니다." —
strict `RspCode == "00000"` success check mis-classifies these as
business-level errors and discards the payload. Codes observed in the
wild (cross-checked against programgarden's `tracker.SUCCESS_CODES`):

- `00000` — standard success
- `00001` — CSPAQ family generic success
- `00133` — FOCCQ33600 paginated success ("조회가 계속 됩니다")
- `00136` — CSPAQ snapshot success (live-confirmed CSPAQ22200 against
  user's real account 2026-05-28)
- `00200` — no-data success on CSPAQ13700 / CDPCQ04700
- `00707` — overseas/derivatives "조회할 내역 없음" success

**Workaround:** `AccountInquiryTools.IsCspaqSuccess` accepts any of
those codes when the expected output block is present.
`LsTrResponse.IsSuccess` stays untouched so the broader TR surface is
unaffected.

**Status:** ✅ v1.6 — code list confirmed against programgarden
(tracker.py:33) and live-verified on CSPAQ22200 against a real account.

### 4.2c CSPAQ22200 is v2 of CSPAQ12200, NOT a virtual variant ✅

**TRs:** `CSPAQ12200` (현물계좌예수금 주문가능금액 총평가 조회) and
`CSPAQ22200` (현물계좌예수금 주문가능금액 총평가2).

The "22200" naming initially read as a virtual-mode counterpart to
"12200" — that interpretation was wrong. Live verification 2026-05-28:
calling CSPAQ22200 with a *real* appkey returned the *real* account's
data, exactly as CSPAQ12200 would. The "2" suffix indicates an API
revision (v2 with a slimmer field set), not a paper-trading switch.
The real-vs-virtual mode lives in the appkey pair, not in the TR
code — see [§4.2d](#42d-ls-rest-mode-is-the-appkey-not-the-endpoint).

**Workaround:** `AccountInquiryTools.Balance` always calls CSPAQ12200
regardless of `LS_MARKET`. The v1 has the richer field set
(evaluation amount, investment P&L, withdrawable presumed) so we lose
nothing by sending only the v1.

**Status:** ✅ v1.6 — corrected after the initial v1.6 release branched
on `LS_MARKET` between the two TRs (wrong assumption).

### 4.2d LS REST mode is the appkey pair, not the endpoint or env var ✅

**Endpoints (all modes):** `https://openapi.ls-sec.co.kr:8080`.

programgarden's `URLS.LS_URL` confirms: real and paper REST traffic
both flow through the same host:port. The
`run_o3117.py` example header explicitly says "REST endpoint:
paper/live 모두 https://openapi.ls-sec.co.kr:8080 동일". Only the
WebSocket endpoint splits (`:9443` real vs `:29443` virtual), and this
project does not use WebSocket per [[mcp-realtime-skeptic]].

What actually determines the mode: LS issues **separate appkey /
appsecretkey pairs** for the real account and the virtual (모의투자)
account. The token issued from a given pair is tied to that account.
Same REST URL + different keys → different account answers.

**Implication for `LS_MARKET`:** the env var is *informational and
namespacing*, not routing. It tags `portfolio.db` rows so two locally
registered accounts (one real, one virtual) don't collide. The actual
LS routing happens at the appkey level — set `LS_APPKEY` /
`LS_APPSECRETKEY` to the pair that matches the intended account, and
set `LS_MARKET` to match for the local labelling.

**Status:** ✅ v1.6 — discovered after the user's smoke showed real
account data with `LS_MARKET=virtual` set, because the loaded appkey
was the real-account pair. The `LsApiOptions.DefaultRealBaseUrl` and
`DefaultVirtualBaseUrl` constants are intentionally identical and a
comment block explains why.

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

---

## 7. WebSocket realtime & news (NWS / t3102)

The findings in this section come from a v1.6 design exploration
(2026-05-27) that ultimately decided **not to productize NWS in v1.x**
— see §7.9 for the rationale. Everything else here is recorded so a
future session that revisits WebSocket realtime — should one ever be
warranted under the conditions in [DESIGN-PRINCIPLES.md §1.2](./DESIGN-PRINCIPLES.md)
— does not re-investigate the same protocol surface from scratch.

### 7.1 WebSocket protocol basics 💭

**Endpoints:**
- Production: `wss://openapi.ls-sec.co.kr:9443/websocket`
- Mock investing: `wss://openapi.ls-sec.co.kr:29443/websocket`

**Auth — token format differs from REST.** REST sends
`Authorization: Bearer <token>`. WebSocket sends the **raw token**
inside the message header (no `Bearer ` prefix):

```json
{ "header": { "token": "<raw access_token>", "tr_type": "3" },
  "body":   { "tr_cd": "NWS", "tr_key": "NWS001" } }
```

A façade that reuses the same `LsTokenCache` must strip `Bearer ` on
the WebSocket path, or the connection authenticates as guest and
returns nothing.

**`tr_type` codes:**
- `1` — 계좌등록 (no `tr_key` required)
- `2` — 계좌해제
- `3` — 실시간 시세 등록 (`tr_key` required)
- `4` — 실시간 시세 해제 (`tr_key` required)

**Heartbeat & reconnect (k-ebest-im pattern, presumed production-tested):**
- `socket.ping()` every **5 s**.
- `onclose` → reconnect after **1 s** (no exponential backoff).
- On reconnect, **all subscriptions must be re-sent** — LS does not
  preserve session state server-side.

**Status:** 💭 documented for future reference. No daemon or wrapper
exists today; catalog-only TRs (NWS, JIF, etc.) are reachable only as
schema.

### 7.2 NWS frame carries fields beyond the spec ⚠️

**TR:** `NWS` (실시간뉴스제목패킷).

The published spec lists `date / time / id / realkey / title / code /
bodysize`. Live frames (2026-05-27) consistently include **two
additional fields**:

| Field | Observed values | Meaning (best guess) |
|---|---|---|
| `categoryid` | `99`, `06`, `05`, ... | News category bucket — see §7.3 |
| `codeaccu` | `"C"` or `""` | Unknown. Possibly an accumulator/continuation flag |

`categoryid` is the only practical handle for client-side noise
reduction (§7.3). `codeaccu` semantics remain unconfirmed — preserve
it in `raw_payload` rather than expose it as a typed field.

**Status:** 💭 documented. A future NWS implementation must accept
unknown extra fields gracefully.

### 7.3 NWS is a global mixed stream — `"NWS001"` is an alias ⚠️

**TR:** `NWS`.

The spec describes `tr_key` as "단축코드 6자리 또는 8자리" but the
example payload sends `"NWS001"`, which is not a valid ticker. Live
testing (2026-05-27, real-trading WebSocket, KST 20:30) with
`tr_key="NWS001"` returns a **fully global, multi-category stream** —
not per-symbol news.

Observed in a 2-minute window:

| Headline (excerpt) | `code` | `categoryid` | Kind |
|---|---|---|---|
| `SKIET, 폴란드 중심으로 생산체계 개편` | `000000096770` | `99` | KR stock news |
| `요약-애플랩, 라비쉬 N. 모디를 CFO로 임명` | `""` | `06` | Overseas (Reuters translation) |
| `중국 남부 전력망, 기록적인 전력 소비량 기록` | `""` | `06` | Overseas general |
| `NHC 열대 날씨 전망` | `""` | `06` | Overseas weather (!) |
| `동작구 써밋더힐·아크로리버스카이 1순위 두자릿수 경쟁률` | `""` | `05` | Korean real-estate auction |

The `code` field (240 chars) is empty for most frames — it carries
12-char zero-padded ticker codes only when the news is attributable to
specific Korean stocks, up to 20 packed (`240 / 12`).

**Implication:** any consumer must filter on `categoryid` (or `code`
non-empty) to extract trading-relevant news; otherwise the stream is
polluted with weather, real-estate auctions, and unrelated
international briefs.

**Observed volume** (single 2-minute sample, KST 20:30, after market
close): ~7-8 frames/minute with a bursty pattern — ~90 s of silence
then 10+ frames in <1 s. Intraday volume is presumed higher but not
yet measured.

**`tr_key=""` also works** (k-ebest-im uses an empty string) and
appears to be equivalent to `"NWS001"` for the "everything" channel.
Other `"NWSxxx"` aliases (002, 003, ...) may select narrower channels
— not yet enumerated.

**Status:** 💭 documented. Per-symbol news, if it exists at all, would
likely be `tr_key=<6-char ticker>` but this was never verified.

### 7.4 NWS `realkey` ↔ t3102 `sNewsno` — same ID namespace ✅

**TRs:** `NWS` (WebSocket push), `t3102` (REST body fetch).

Verified empirically (2026-05-27): a 24-char `realkey` from a live NWS
frame, passed verbatim as `t3102.sNewsno`, returns the full body for
that headline.

**Format note — two coexisting forms.** Live 2026 frames use 24
**all-numeric** characters: `YYYYMMDD HHmmss + 10-char sequence` —
e.g. `202605272032312300009798`. The spec example
(`"2023051510383935PL7HQ87D"`) shows an older **alphanumeric** form
ending in 8 letters. **Both forms are accepted by t3102** — the 2023
example still returned its body in 2026 (LS retention is ≥3 years).

**Status:** ✅ confirmed. A future NWS pipeline can use NWS `realkey`
directly as the body cache key with no transformation.

### 7.5 t3102 response shape varies by whether news has stock mapping ⚠️

**TR:** `t3102` (뉴스본문).

The spec declares three output blocks: `t3102OutBlock` (sJongcode),
`t3102OutBlock1` (sBody chunks), `t3102OutBlock2` (sTitle). Live
responses (2026-05-27) **omit either `t3102OutBlock` or `t3102OutBlock2`
depending on news type** — they are effectively mutually exclusive:

| News kind | `t3102OutBlock` | `t3102OutBlock1` | `t3102OutBlock2` |
|---|---|---|---|
| Korean stock news (`code` populated in NWS) | ✅ (sJongcode array) | ✅ | ❌ **missing** |
| Overseas / real-estate / general (`code` empty in NWS) | ❌ **missing** | ✅ | ✅ (sTitle — *but see §7.6*) |

`out_block_names` reflects this — a consumer must inspect
`out_block_names` rather than assume all three blocks exist.

Body is always returned in a single response: `continuation.has_more`
was `false` for every observed call. No paging needed.

**Status:** ⚠️ documented but no wrapper exists; `ls_call_tr` callers
must branch on `out_block_names`.

### 7.6 t3102 server-side buffer pollution in non-stock news 🔴

**TR:** `t3102` for news without stock mapping (§7.5 second row).

Two distinct corruption patterns, both consistent with **uninitialized
server-side buffers**:

**(a) Block name leaked into data prefix.** The first body chunk and
the title field carry their own block name as a literal prefix:

```
sBody[0]:  "t3102OutBlock1  BRIEF-Aplab Ltd Appointed Ravish..."
sTitle:    "t3102OutBlock2  요약-애플랩, 라비쉬 N. 모디를 CFO로 임명하다..."
```

A consumer must strip `^t3102OutBlock[12]  ` if present.

**(b) `sTitle` contains fragments of other news titles.** Past the
expected headline, the field continues with what looks like cross-news
contamination from a shared server buffer:

```
sTitle: "t3102OutBlock2  요약-애플랩, 라비쉬 N. 모디를 CFO로 임명하다
         입� � �이 s晫sco, 5일 0~6� � 04 하루 -6.0mcm 정전 예정
         � 인 H, 유 힌� � 폐 s晫 처� 계약 체결 � s晫� 나..."
```

`sTitle` cannot be trusted past the first headline. **Workaround:
use the NWS frame's `title` field as the authoritative source** — that
field is delivered cleanly. `t3102OutBlock2.sTitle` should only be a
fallback when no NWS frame is available, and even then must be
truncated heuristically (e.g. cut at the first `  ` double-space after
the legitimate headline, or simply ignore everything past 300 chars).

Korean-stock responses (the `t3102OutBlock` shape) do **not** exhibit
this corruption — their body chunks and the absent title field don't
have the buggy code path.

**Status:** 🔴 LS server-side bug. Not fixable client-side beyond
strip-and-truncate. Recorded so a future wrapper does not surface
contaminated titles to the LLM.

### 7.7 t3102 "not found" returns success codes 🔴

**TR:** `t3102`.

Calls with malformed (`"abc"`) or merely nonexistent
(`"000000000000000000000000"`) `sNewsno` return:

```json
{ "status": 200,
  "rsp_cd": "00000",
  "is_success": true,
  "rsp_msg": "해당자료가 없습니다. 다시 조회 바랍니다.",
  "out_block_names": [],
  "body": { "rsp_cd": "00000", "rsp_msg": "해당자료가 없습니다..." } }
```

`rsp_cd`, `is_success`, and HTTP status all signal **success** for what
is clearly a not-found. **The only reliable signal is
`out_block_names.Count == 0`.** A wrapper that trusts `is_success`
will treat a missing news ID as a valid empty body.

LS also does no input validation — `"abc"` (3 chars, vs spec-required
24) gets the same soft-failure as a well-formed-but-nonexistent ID.

**Status:** 🔴 LS API contract bug. A future wrapper must guard with
`out_block_names` check.

### 7.8 t3102 body chunks split at byte boundaries, corrupting Korean ⚠️

**TR:** `t3102`.

`t3102OutBlock1[].sBody` is the body split into ~100-byte chunks. LS
splits at **byte** boundaries, ignoring UTF-8 multi-byte character
boundaries, so 2- or 3-byte Korean characters straddling a chunk
boundary arrive as `U+FFFD` (`�`) on **both sides** of the split:

```
sBody[2]: "...국내 충북 공장 상업 생산도 중단하는 대신 폴란드를 중심으로 \r\n한 생산체계 재편에 나선다. 북미·유럽"
sBody[3]: "전기차 시장 대응에 집중하기 위한 공급망 \r\n재편 차원이다.</p>..."
                                                                          ^ clean join here

sBody[2]: "...SKIET는 중국 공장 운영법인인 SK하이�"
sBody[3]: "淪㈇蕁섯�얼즈 지분 100%를 중국 분리막 업\r\n체 셈코프에..."
                ^^^^^^^^^^^ broken char straddles the boundary
```

By the time the JSON arrives the bytes have already been decoded to
strings with replacement characters; **byte-level reassembly is not
possible** — the original bytes are gone.

**Workaround:** concat chunks verbatim, leave the `U+FFFD`
replacement characters in place. LLM context usually disambiguates
(`높여잡� 있다` is obviously `높여잡고 있다`). Any attempt at
heuristic restoration risks hallucination.

Body also carries HTML markup (`<p>`, `<br/>`, `<a href>`, `<img>`,
`<span stockcode='192820'>`), Thomson Reuters disclaimers, related-
article links, and reporter bylines. Preserve verbatim — the markup
carries useful signal (stockcode tagging especially).

**Status:** ⚠️ LS API quirk, no client-side fix possible. Document
in any future wrapper's tool description so the LLM knows broken
characters are source-side.

### 7.9 Strategic note — NWS not productized in v1.x 💭

A v1.6 daemon slice for NWS was designed in detail
(2026-05-27 session) and **rejected**. Recording the rationale so a
future revisit starts from the right baseline rather than re-running
the same analysis:

1. **NWS is push-only.** No REST news search TR exists in the
   catalog. If the daemon misses frames (host off, daemon restart,
   reconnect gap), those headlines are **permanently unrecoverable**
   — the value proposition of "managed news feed" has no safety net.
2. **No alternative discovery path for t3102.** WTS/MTS apps do not
   expose `sNewsno`. Naver/Daum finance use their own ID namespace.
   Without NWS WebSocket, t3102 has no realistic input source — the
   body-fetch tool would be unreachable from a user's perspective.
3. **Stream content is mixed and noisy.** §7.3 documents that NWS
   carries Korean stock news + Reuters translations + real-estate
   auctions + weather forecasts in one channel. A useful tool would
   require curating the `categoryid` catalog (not provided by LS) and
   shipping client-side filters — meaningful ongoing maintenance.
4. **LS ecosystem signal.** ProgramGarden (the LS-backed Python quant
   platform) has no news nodes. k-ebest-im wraps NWS at the rawest
   possible level (callback registration only, no managed
   processing). The absence of any second-party investment in news
   suggests low ROI in this corner of the API.
5. **Server-side bugs** (§7.6, §7.7) push a non-trivial fraction of
   any wrapper's code into "uninitialized buffer cleanup" — adds
   complexity disproportionate to delivered value.
6. **Daemon infrastructure investment** (sidecar process + named
   pipe IPC + schtasks install) is more cleanly justified by **JIF
   market state** as the v1.6 use case, where the data is unique,
   small, and free of these problems.

**Conditions under which to revisit:**
- LS publishes a news search REST TR (would solve #1, #2).
- A `categoryid` catalog becomes available, ideally with a
  Korean-stock-only channel alias (would solve #3).
- Server-side `sTitle` corruption is fixed (would solve #5 partially).
- User demand emerges from production telemetry showing LLM sessions
  routinely failing on news questions (none observed as of v1.5.1).

Until then: t3102 remains catalog-only and reachable via
`ls_call_tr`; NWS has no wrapper. The `ServerInstructions` line
`"t3102 (뉴스본문) is catalog-only and unusable as a news tool
without NWS WebSocket number discovery"` stays accurate.

**Status:** 💭 closed (until conditions above change).

### 7.10 JIF is transition-only push — silent in steady state ⚠️

**TR:** `JIF` (장운영정보 WebSocket).

Subscribing to JIF on the real-trading WebSocket (`tr_key="1"` = KOSPI)
at KST 20:30 (well after market close) returns **zero frames** for the
entire 2-minute test window. JIF emits **only on state transitions** —
the jstatus codes (`11` 장전동시호가, `21` 장시작, `41` 장마감, `61`
서킷브레이크1단계, etc.) are a list of *events*, not a snapshot.

**Implication for any consumer that wants "current state":** JIF
*cannot* answer "what is the market doing right now?" unless the
consumer has been listening continuously since the last relevant
transition. A daemon started mid-session sees an **empty state** until
the next transition arrives. Worse, a daemon that misses one
transition (laptop closed during 사이드카 발동, host restart between
장시작 and 장마감) keeps serving *stale-but-confidently-wrong* state
indefinitely until the next transition corrects it.

**Asymmetry with NWS:** NWS frames are append-only headline events —
missing frames means "fewer recent headlines", never wrong state. JIF
is the opposite — missing transitions means *actively wrong* current
state. The two streams have inverted failure modes.

**Practical takeaway:** Do not productize JIF as "current market
state" without (a) guaranteed always-on capture **and** (b) a startup
backfill mechanism. No REST equivalent for JIF state was found, so (b)
is unsolvable. Without both, the LLM ends up with confidently wrong
context, which is *worse* than no context (the LLM's own
clock + holiday-calendar knowledge would have been more accurate by
default).

**Status:** 💭 documented. v1.6 design (2026-05-27) considered JIF for
a `_meta.session_now` field and rejected it for this reason — see
§7.11 for the broader rejection of WebSocket as an MCP transport.

### 7.11 WebSocket as a whole adds little for MCP use cases 💭

A v1.6 design session (2026-05-27) examined every WebSocket category
exposed by LS — news (NWS, §7.9), market state (JIF, §7.10), order
events (SC0–SC4 주문접수/체결/정정/취소/거부), and market data push
(체결 `S3_`/`K3_`, 호가 `H1_`/`HA_`, VI `VI_`, 프로그램매매
`PH_`/`PM_`, NXT `NS3`/`NBT`, ...) — and concluded that **none of
them justify daemon/sidecar infrastructure under the MCP-server use
case**.

The recurring reasons:

1. **LLMs are episodic.** They execute when the user prompts, not
   continuously. Sub-second push value evaporates while nobody is
   listening for sub-second windows. The MCP-chat cadence is "user
   asks → tool calls → LLM responds, then idle for minutes" —
   incompatible with streaming-bot patterns.
2. **Most push data is also pollable.** Order fills (SC1) → REST
   `t0425` / `CSPAQ13700`. Account balance → `t0424`. Current price →
   any quote TR. The WebSocket version is a *latency optimization*
   for bots, not a *capability* unlocking otherwise-inaccessible data.
3. **Push with downtime is actively dangerous.** Anything stateful
   (JIF, SCx fills) gives the LLM *confidently wrong* context when
   the daemon misses frames (§7.10). REST polling returns a fresh
   snapshot every call — the wrongness window is bounded by call
   frequency, not by daemon uptime.
4. **The few push-only signals** (NWS headlines §7.9, VI 발동/해제
   for halts) are either covered by general LLM web tooling (news →
   web search) or too rare per session to justify always-on capture.
5. **Ecosystem signal.** ProgramGarden (LS-backed Python quant
   platform) wraps WebSocket *inside its workflow engine* — for
   automated bots, not assistants. k-ebest-im exposes raw WebSocket
   as a callback registry with no managed processing. Neither
   provides a reference model for MCP-style push handling, because
   the use case doesn't naturally exist in the LS Python ecosystem
   either.

**Strategic implication:** mcp-lsopenapi can remain **daemon-less,
stdio-friendly, single-binary indefinitely**. A future trading slice
is a pure REST wrapper around `/stock/order`
(CSPAT00601/00701/00801) + `/stock/accno` (`t0424`, `t0425`,
`CSPAQ13700`, `CSPAQ12200`, ...). No WebSocket. No sidecar. No
installed service. No `schtasks` integration. No named-pipe IPC.

**Conditions to revisit (any one):**
- A push-only signal emerges that has *no* REST equivalent **and**
  matches a recurring LLM use case (not an automation use case).
- The MCP spec gains first-class push primitives that hosts
  consistently implement (resources/subscribe in MCP 1.4+ is
  promising but host support remains spotty as of 2026-05).
- Production telemetry shows monitor-mode demand ("alert me when X
  happens") — but that pattern itself may belong in a workflow tool
  (cron/Routines/Cowork), not inside an MCP server.

This insight is also recorded as a memory ([[mcp-realtime-skeptic]])
so future sessions inherit it without re-deriving it.

**Status:** 💭 closed (until conditions above change). Supersedes the
earlier daemon framing in older drafts of [[next-nxt-realtime]] —
that memory now points back here. See also [DESIGN-PRINCIPLES.md §1](./DESIGN-PRINCIPLES.md)
for the generalized form of this conclusion.

