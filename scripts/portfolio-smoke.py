"""Local smoke test for v0.5+v0.6 portfolio MCP tools.

Spawns the MCP stdio server with an isolated SQLite DB. When LS
credentials are available (env vars or the .env.local at ENV_LOCAL_PATH
below), quote-enrich and live-data tools are also asserted against LS.
Without credentials the script still exercises every tool's local path
and verifies the quote_error / auth-error envelopes.

v0.6 highlights covered:
- watched_sectors → watched_themes rename (DB + tool surface)
- portfolio export/import round-trip + replace-mode confirm gate
- new tools route smoke: ls_get_index_quote / ls_get_industry_indices /
  ls_get_industry_stocks / ls_get_theme_stocks / ls_get_stock_themes
- Tier 1 compression regression: removed tools absent from tools/list,
  ls_holdings_corporate_action dispatches and rejects unknown types

Usage:
    python scripts/portfolio-smoke.py            # auto-detect creds
    python scripts/portfolio-smoke.py --no-live  # force no-credentials mode
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# Force UTF-8 on stdout so Korean and the ≈ character render on Windows consoles.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except AttributeError:
    pass

REPO = Path(__file__).resolve().parent.parent
PROJECT = REPO / "src" / "RedoxNet.Mcp.LsOpenApi" / "RedoxNet.Mcp.LsOpenApi.csproj"
DLL = REPO / "src" / "RedoxNet.Mcp.LsOpenApi" / "bin" / "Debug" / "net8.0" / "redoxnet-mcp-lsopenapi.dll"
ENV_LOCAL_PATH = Path(r"E:\MCP_E2E\.env.local")


def green(s): return f"\033[32m{s}\033[0m"
def red(s):   return f"\033[31m{s}\033[0m"
def gray(s):  return f"\033[90m{s}\033[0m"
def yellow(s):return f"\033[33m{s}\033[0m"


def mask(value):
    if not value:
        return "(empty)"
    if len(value) <= 4:
        return "****"
    return "****" + value[-4:]


def load_dotenv(path):
    result = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            continue
        k, _, v = line.partition("=")
        v = v.strip()
        if (v.startswith('"') and v.endswith('"')) or (v.startswith("'") and v.endswith("'")):
            v = v[1:-1]
        result[k.strip()] = v
    return result


class McpClient:
    def __init__(self, env):
        self._next_id = 1
        self.proc = subprocess.Popen(
            ["dotnet", "exec", str(DLL)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=str(REPO),
            env=env,
            encoding="utf-8",
            bufsize=1,
        )

    def _send(self, msg):
        self.proc.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()

    def _recv(self):
        while True:
            if self.proc.poll() is not None:
                err = self.proc.stderr.read()
                raise RuntimeError(f"Server exited rc={self.proc.returncode}\n{err}")
            line = self.proc.stdout.readline()
            if not line:
                continue
            line = line.strip()
            if not line:
                continue
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue

    def request(self, method, params=None):
        msg_id = self._next_id
        self._next_id += 1
        self._send({"jsonrpc": "2.0", "id": msg_id, "method": method, "params": params or {}})
        while True:
            resp = self._recv()
            if resp.get("id") == msg_id:
                return resp

    def notify(self, method, params=None):
        self._send({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def call_tool(self, name, args=None):
        resp = self.request("tools/call", {"name": name, "arguments": args or {}})
        if "error" in resp:
            return {"__rpc_error": resp["error"]}
        result = resp.get("result", {})
        content = result.get("content") or []
        if not content:
            return {"__empty": True, "raw": result}
        text = content[0].get("text", "")
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return {"__raw_text": text}

    def list_tools(self):
        resp = self.request("tools/list")
        return [t["name"] for t in resp.get("result", {}).get("tools", [])]

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()


def main():
    if not DLL.exists():
        print("[setup] dll missing, building...")
        rc = subprocess.run(
            ["dotnet", "build", str(PROJECT), "-c", "Debug", "-f", "net8.0", "--nologo", "-v", "quiet"],
            cwd=str(REPO),
        )
        if rc.returncode != 0:
            print(red("build failed"))
            return 1

    tmp = Path(tempfile.mkdtemp(prefix="mcp-portfolio-smoke-"))
    db_path = tmp / "portfolio.db"

    env = os.environ.copy()
    env["LSOPENAPI_DB_PATH"] = str(db_path)

    force_no_live = "--no-live" in sys.argv
    if not force_no_live:
        if not env.get("LS_APPKEY") and not env.get("LS_APPSECRETKEY") and ENV_LOCAL_PATH.is_file():
            loaded = load_dotenv(ENV_LOCAL_PATH)
            for k in ("LS_APPKEY", "LS_APPSECRETKEY", "LS_MARKET"):
                if k in loaded:
                    env[k] = loaded[k]
            print(f"[setup] loaded creds from {ENV_LOCAL_PATH}")
    else:
        for k in ("LS_APPKEY", "LS_APPSECRETKEY"):
            env.pop(k, None)

    live = bool(env.get("LS_APPKEY") and env.get("LS_APPSECRETKEY"))
    print(f"[setup] db     = {db_path}")
    print(f"[setup] appkey = {mask(env.get('LS_APPKEY'))}")
    print(f"[setup] market = {env.get('LS_MARKET') or '(default)'}")
    print(f"[setup] mode   = {'LIVE (quote-enrich asserted)' if live else 'OFFLINE (quote_error expected)'}")

    client = McpClient(env)
    failures = 0
    total = 0

    def check(name, ok, detail=""):
        nonlocal failures, total
        total += 1
        mark = green("OK  ") if ok else red("FAIL")
        suffix = f"  {gray(detail)}" if detail else ""
        print(f"  {mark}  {name}{suffix}")
        if not ok:
            failures += 1

    try:
        init = client.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "portfolio-smoke", "version": "0.0"},
        })
        si = init["result"]["serverInfo"]
        print(f"[init] {si['name']} v{si['version']}")
        client.notify("notifications/initialized")

        # --- empty state ---
        print("\n[empty state]")
        accs = client.call_tool("ls_accounts_list")
        check("accounts_list empty initially", isinstance(accs, list) and len(accs) == 0, f"{accs}")

        # v0.6 Tier 1 compression: ls_account_get was removed. The default
        # account is derived from the is_default flag in accounts_list.
        default = next((a for a in accs if a.get("is_default")), None) if isinstance(accs, list) else None
        check("default null when no accounts (via is_default filter)", default is None, f"{default}")

        hl_empty = client.call_tool("ls_holdings_list")
        check("holdings_list empty when no accounts", hl_empty.get("accounts") == [], f"{hl_empty}")

        empty_set = client.call_tool("ls_holdings_set", {"shcode": "005930", "quantity": 10, "avg_price": 70000})
        check("holdings_set RequiresAccount when no accounts",
              empty_set.get("error") == "RequiresAccount",
              json.dumps(empty_set, ensure_ascii=False))

        # --- account upsert ---
        print("\n[account upsert]")
        hantoo = client.call_tool("ls_account_upsert", {"account_number": "12345-01", "nickname": "한투", "broker": "한국투자"})
        check("upsert 한투 auto-promotes to default",
              hantoo.get("nickname") == "한투" and hantoo.get("is_default") is True,
              json.dumps(hantoo, ensure_ascii=False))

        kb = client.call_tool("ls_account_upsert", {"account_number": "67890-22", "nickname": "KB ISA", "broker": "KB증권"})
        check("upsert KB ISA does not displace default",
              kb.get("is_default") is False,
              json.dumps(kb, ensure_ascii=False))

        dup_nick = client.call_tool("ls_account_upsert", {"account_number": "99999-99", "nickname": "한투", "broker": "X"})
        check("nickname collision returns ValidationError",
              dup_nick.get("error") == "ValidationError",
              json.dumps(dup_nick, ensure_ascii=False))

        accs2 = client.call_tool("ls_accounts_list")
        check("accounts_list shows 2 accounts", isinstance(accs2, list) and len(accs2) == 2, f"{len(accs2)} accounts")

        # v0.6: ls_account_set_default removed; same effect via
        # ls_account_upsert(set_default=true). Upsert is idempotent so
        # re-supplying the existing identity is the canonical pattern.
        switched = client.call_tool("ls_account_upsert", {
            "account_number": "67890-22", "nickname": "KB ISA", "broker": "KB증권", "set_default": True
        })
        check("set_default via upsert(set_default=true)",
              switched.get("is_default") is True and switched.get("nickname") == "KB ISA",
              json.dumps(switched, ensure_ascii=False))
        # switch back for the rest of the smoke
        client.call_tool("ls_account_upsert", {
            "account_number": "12345-01", "nickname": "한투", "broker": "한국투자", "set_default": True
        })

        # --- holdings set/buy/sell on single account fallback ---
        print("\n[holdings basic - 한투 only via fallback after explicit target]")
        first_buy = client.call_tool("ls_holdings_buy", {"shcode": "005930", "quantity": 10, "price": 70000, "account": "한투"})
        check("buy first time records position",
              first_buy.get("quantity") == 10 and first_buy.get("avg_price") == 70000 and first_buy.get("applied_to", {}).get("nickname") == "한투",
              json.dumps(first_buy, ensure_ascii=False))

        weighted = client.call_tool("ls_holdings_buy", {"shcode": "005930", "quantity": 5, "price": 80000, "account": "한투"})
        expected_avg = (10*70000 + 5*80000) / 15
        check("buy merges weighted average",
              weighted.get("quantity") == 15 and abs(weighted.get("avg_price", 0) - expected_avg) < 1,
              f"qty={weighted.get('quantity')}, avg={weighted.get('avg_price')!r} expected≈{expected_avg:.2f}")

        zero_set = client.call_tool("ls_holdings_set", {"shcode": "005930", "quantity": 0, "avg_price": 50000, "account": "한투"})
        check("set quantity=0 → ValidationError",
              zero_set.get("error") == "ValidationError",
              json.dumps(zero_set, ensure_ascii=False))

        # --- multi-account same symbol → ambiguity ---
        print("\n[ambiguity - same symbol two accounts]")
        kb_buy = client.call_tool("ls_holdings_buy", {"shcode": "005930", "quantity": 4, "price": 90000, "account": "KB ISA"})
        check("buy on KB ISA",
              kb_buy.get("applied_to", {}).get("nickname") == "KB ISA",
              json.dumps(kb_buy, ensure_ascii=False))

        ambig_sell = client.call_tool("ls_holdings_sell", {"shcode": "005930", "quantity": 1})
        check("sell without account → AmbiguousAccount",
              ambig_sell.get("error") == "AmbiguousAccount" and len(ambig_sell.get("candidates", [])) == 2,
              json.dumps(ambig_sell, ensure_ascii=False))

        # --- ambiguity does not fire for writes that target an existing single-symbol account ---
        targeted_sell = client.call_tool("ls_holdings_sell", {"shcode": "005930", "quantity": 5, "account": "한투"})
        check("targeted sell on 한투 reduces quantity",
              targeted_sell.get("quantity") == 10 and targeted_sell.get("applied_to", {}).get("nickname") == "한투",
              json.dumps(targeted_sell, ensure_ascii=False))

        over_sell = client.call_tool("ls_holdings_sell", {"shcode": "005930", "quantity": 999, "account": "한투"})
        check("over-sell → InsufficientQuantity",
              over_sell.get("error") == "InsufficientQuantity" and over_sell.get("current_quantity") == 10,
              json.dumps(over_sell, ensure_ascii=False))

        # --- list grouped ---
        print("\n[list grouped]")
        hl = client.call_tool("ls_holdings_list")
        accounts = hl.get("accounts") or []
        check("holdings_list returns both accounts grouped",
              len(accounts) == 2 and all("holdings" in a and "summary" in a for a in accounts),
              f"{[a['nickname'] for a in accounts]}")
        ts = hl.get("total_summary") or {}
        # 한투: 10주 @ avg, KB: 4주 @ 90000
        expected_total_cost = 10 * expected_avg + 4 * 90000
        check("total_summary cost_basis aggregates",
              abs(ts.get("cost_basis", 0) - expected_total_cost) < 1,
              f"cost_basis={ts.get('cost_basis')}, expected≈{expected_total_cost:.2f}")

        if live:
            for acc in accounts:
                for h in acc.get("holdings", []):
                    if h.get("quote"):
                        break
            check("live quote enriches at least one row",
                  any(h.get("quote") for acc in accounts for h in acc.get("holdings", [])),
                  f"quote_error={hl.get('quote_error')}")
        else:
            check("offline list returns quote_error",
                  hl.get("quote_error") is not None,
                  f"quote_error={hl.get('quote_error')!r}")

        # --- corporate actions (v0.6 unified dispatcher) ---
        print("\n[corporate actions — ls_holdings_corporate_action]")
        split = client.call_tool("ls_holdings_corporate_action", {
            "shcode": "005930", "type": "split", "ratio": 2
        })
        applied = split.get("applied_to") or []
        check("type=split with no account applies to all holders",
              len(applied) == 2 and all(r["after"]["quantity"] == r["before"]["quantity"] * 2 for r in applied),
              json.dumps([{a["account"]["nickname"]: a["after"]["quantity"]} for a in applied], ensure_ascii=False))

        # 7 shares before reverse_split, ratio=3 should fail divisibility
        client.call_tool("ls_holdings_set", {"shcode": "000660", "quantity": 7, "avg_price": 100000, "account": "한투"})
        bad_rev = client.call_tool("ls_holdings_corporate_action", {
            "shcode": "000660", "type": "reverse_split", "ratio": 3, "account": "한투"
        })
        check("type=reverse_split non-divisible → ValidationError",
              bad_rev.get("error") == "ValidationError",
              json.dumps(bad_rev, ensure_ascii=False))

        # v0.6 Tier 1 compression: unknown type rejected with a friendly
        # message that includes the supported set + future-extension hint.
        unknown_type = client.call_tool("ls_holdings_corporate_action", {
            "shcode": "000660", "type": "stock_dividend", "ratio": 0.05, "account": "한투"
        })
        check("unknown type=stock_dividend → ValidationError + future-extension hint",
              unknown_type.get("error") == "ValidationError"
              and "Additional types" in (unknown_type.get("message") or ""),
              json.dumps(unknown_type, ensure_ascii=False))

        client.call_tool("ls_holdings_remove", {"shcode": "000660", "account": "한투"})

        # --- account remove cascade confirm ---
        print("\n[account remove]")
        rm_kb_unsafe = client.call_tool("ls_account_remove", {"account": "KB ISA", "confirm": False})
        check("remove account w/ holdings & confirm=false → RequiresConfirmation",
              rm_kb_unsafe.get("error") == "RequiresConfirmation" and rm_kb_unsafe.get("holding_count", 0) > 0,
              json.dumps(rm_kb_unsafe, ensure_ascii=False))

        rm_kb_ok = client.call_tool("ls_account_remove", {"account": "KB ISA", "confirm": True})
        check("remove account w/ confirm=true cascades",
              rm_kb_ok.get("removed") is True and rm_kb_ok.get("cascaded_holdings") > 0,
              json.dumps(rm_kb_ok, ensure_ascii=False))

        # Now remove 한투 (default) and verify auto-succession to a fresh account
        client.call_tool("ls_account_upsert", {"account_number": "NEW-01", "nickname": "신계좌"})
        rm_default = client.call_tool("ls_account_remove", {"account": "한투", "confirm": True})
        check("remove default cascades and auto-promotes another account",
              rm_default.get("removed") is True and rm_default.get("new_default", {}).get("nickname") == "신계좌",
              json.dumps(rm_default, ensure_ascii=False))

        # --- broker rename ---
        print("\n[broker rename]")
        client.call_tool("ls_account_upsert", {"account_number": "NEW-02", "nickname": "신계좌2", "broker": "X증권"})
        client.call_tool("ls_account_upsert", {"account_number": "NEW-03", "nickname": "신계좌3", "broker": "X증권"})
        renamed = client.call_tool("ls_broker_rename", {"from": "X증권", "to": "X증권 (renamed)"})
        check("broker_rename affects matching rows",
              renamed.get("accounts_affected") == 2 and renamed.get("to") == "X증권 (renamed)",
              json.dumps(renamed, ensure_ascii=False))

        # --- watchlist group rename ---
        print("\n[watchlist group rename]")
        client.call_tool("ls_watchlist_group_create", {"name": "semis"})
        ren = client.call_tool("ls_watchlist_group_rename", {"old_name": "semis", "new_name": "semiconductors"})
        check("group rename succeeds",
              ren.get("new_name") == "semiconductors",
              json.dumps(ren, ensure_ascii=False))

        client.call_tool("ls_watchlist_group_create", {"name": "bio"})
        clash = client.call_tool("ls_watchlist_group_rename", {"old_name": "bio", "new_name": "semiconductors"})
        check("group rename to existing → ValidationError",
              clash.get("error") == "ValidationError",
              json.dumps(clash, ensure_ascii=False))

        # --- validation ---
        print("\n[validation]")
        bad = client.call_tool("ls_watchlist_add", {"shcode": "12X", "group_name": "default"})
        check("watchlist_add rejects malformed shcode",
              bad.get("error") == "ValidationError",
              json.dumps(bad, ensure_ascii=False))

        # --- v0.6: Tier 1 compression regression ---
        print("\n[v0.6 Tier 1 compression]")
        tools = set(client.list_tools())
        removed = ["ls_account_get", "ls_account_set_default",
                   "ls_holdings_split", "ls_holdings_reverse_split", "ls_holdings_bonus"]
        check("removed v0.5 tools absent from tools/list",
              not (set(removed) & tools),
              f"still present: {sorted(set(removed) & tools)}")
        added = ["ls_holdings_corporate_action", "ls_get_index_quote",
                 "ls_get_industry_indices", "ls_get_industry_stocks",
                 "ls_get_theme_stocks", "ls_get_stock_themes",
                 "ls_portfolio_export", "ls_portfolio_import",
                 "ls_watched_themes_add", "ls_watched_themes_remove", "ls_watched_themes_list"]
        missing = sorted(set(added) - tools)
        check("v0.6 new tools registered", not missing, f"missing: {missing}")

        legacy_call = client.call_tool("ls_holdings_split", {"shcode": "005930", "ratio": 2})
        check("calling removed ls_holdings_split → RPC error",
              "__rpc_error" in legacy_call,
              json.dumps(legacy_call, ensure_ascii=False))

        # --- v0.6: watched_themes rename (v0.5 watched_sectors → watched_themes) ---
        print("\n[v0.6 watched_themes (renamed from watched_sectors)]")
        added_theme = client.call_tool("ls_watched_themes_add", {
            "theme_code": "0064", "theme_name": "2차전지", "note": "smoke"
        })
        check("watched_themes_add returns theme_code/theme_name",
              added_theme.get("theme_code") == "0064" and added_theme.get("theme_name") == "2차전지",
              json.dumps(added_theme, ensure_ascii=False))

        themes_list = client.call_tool("ls_watched_themes_list")
        items = themes_list.get("items") or []
        check("watched_themes_list returns the added theme",
              len(items) == 1 and items[0].get("theme_code") == "0064",
              f"items={[i.get('theme_code') for i in items]}")

        removed_theme = client.call_tool("ls_watched_themes_remove", {"theme_code": "0064"})
        check("watched_themes_remove succeeds",
              removed_theme.get("removed") is True,
              json.dumps(removed_theme, ensure_ascii=False))

        # --- v0.6: portfolio export/import round-trip ---
        print("\n[v0.6 portfolio I/O]")
        export_path = str(tmp / "smoke-export.json")
        exported = client.call_tool("ls_portfolio_export", {"path": export_path})
        check("portfolio_export writes JSON with schema_version=1",
              exported.get("schema_version") == 1 and exported.get("size_bytes", 0) > 0
              and Path(exported.get("path", "")).is_file(),
              json.dumps(exported, ensure_ascii=False))

        # Replace mode without confirm should be gated.
        gated = client.call_tool("ls_portfolio_import", {
            "path": export_path, "mode": "replace", "confirm": False
        })
        check("replace without confirm → RequiresConfirmation",
              gated.get("error") == "RequiresConfirmation",
              json.dumps(gated, ensure_ascii=False))

        # Merge mode against the same DB → every row is a duplicate.
        merged = client.call_tool("ls_portfolio_import", {
            "path": export_path, "mode": "merge"
        })
        check("merge re-import finds every account as duplicate",
              merged.get("mode") == "merge"
              and merged.get("imported", {}).get("accounts") == 0
              and len(merged.get("skipped", {}).get("accounts", [])) >= 1,
              json.dumps(merged.get("imported"), ensure_ascii=False))

        # Unknown schema_version should bounce.
        bad_schema = tmp / "bad-schema.json"
        bad_schema.write_text(json.dumps({
            "schema_version": 99, "exported_at": "2026-05-15T12:34:56+09:00",
            "exporter_version": "future",
            "accounts": [], "watchlist_groups": [], "watched_themes": []
        }), encoding="utf-8")
        mismatch = client.call_tool("ls_portfolio_import", {"path": str(bad_schema), "mode": "merge"})
        check("schema_version=99 → ImportSchemaMismatch",
              mismatch.get("error") == "ImportSchemaMismatch"
              and mismatch.get("file_schema_version") == 99,
              json.dumps(mismatch, ensure_ascii=False))

        # --- v0.6: new market/theme tool routing ---
        print("\n[v0.6 new tool routing]")
        idx = client.call_tool("ls_get_index_quote", {"index_code": "kospi"})
        if live:
            check("ls_get_index_quote(kospi) returns t1511 envelope",
                  idx.get("index_code") == "001" and "value" in idx and "related_indices" in idx,
                  f"keys={list(idx.keys())[:6] if isinstance(idx, dict) else type(idx).__name__}")
        else:
            check("ls_get_index_quote routes without credentials",
                  isinstance(idx, dict) and ("error" in idx or "details" in idx),
                  json.dumps(idx, ensure_ascii=False)[:120])

        unknown_alias = client.call_tool("ls_get_index_quote", {"index_code": "fake-index"})
        check("ls_get_index_quote unknown alias → validation error",
              isinstance(unknown_alias, dict) and "error" in unknown_alias,
              json.dumps(unknown_alias, ensure_ascii=False)[:120])

        industry_idx = client.call_tool("ls_get_industry_indices", {"market": "kospi", "top_n": 5})
        if live:
            check("ls_get_industry_indices returns sorted rows",
                  isinstance(industry_idx.get("rows"), list)
                  and industry_idx.get("market") == "kospi",
                  f"count={industry_idx.get('count')}")
        else:
            check("ls_get_industry_indices routes without credentials",
                  isinstance(industry_idx, dict),
                  json.dumps(industry_idx, ensure_ascii=False)[:120])

        industry_stocks = client.call_tool("ls_get_industry_stocks", {"upcode": "001", "top_n": 5})
        check("ls_get_industry_stocks routes (upcode mode)",
              isinstance(industry_stocks, dict),
              json.dumps(industry_stocks, ensure_ascii=False)[:120])

        theme_stocks = client.call_tool("ls_get_theme_stocks", {"theme_code": "0064", "top_n": 5})
        check("ls_get_theme_stocks routes (code mode)",
              isinstance(theme_stocks, dict),
              json.dumps(theme_stocks, ensure_ascii=False)[:120])

        stock_themes = client.call_tool("ls_get_stock_themes", {"shcode": "005930"})
        check("ls_get_stock_themes routes for valid shcode",
              isinstance(stock_themes, dict)
              and (stock_themes.get("shcode") == "005930" or "error" in stock_themes),
              json.dumps(stock_themes, ensure_ascii=False)[:120])

        bad_shcode = client.call_tool("ls_get_stock_themes", {"shcode": "12X"})
        check("ls_get_stock_themes rejects malformed shcode",
              isinstance(bad_shcode, dict) and "error" in bad_shcode,
              json.dumps(bad_shcode, ensure_ascii=False)[:120])

        # --- v0.6: holdings_list theme filter + metadata_freshness ---
        # metadata_freshness is always emitted; offline mode shows "pending"
        # because t1532 enrichment can't fire without credentials.
        hl_meta = client.call_tool("ls_holdings_list")
        check("holdings_list emits metadata_freshness block",
              "metadata_freshness" in hl_meta
              and "fully_enriched" in hl_meta.get("metadata_freshness", {}),
              json.dumps(hl_meta.get("metadata_freshness"), ensure_ascii=False))

        filtered = client.call_tool("ls_holdings_list", {"theme_keyword": "2차전지"})
        check("holdings_list with theme_keyword echoes filter",
              filtered.get("filter", {}).get("theme_keyword") == "2차전지"
              and "matched_themes" in filtered,
              json.dumps({k: v for k, v in filtered.items() if k in ("filter", "matched_themes")}, ensure_ascii=False))

    finally:
        client.close()
        shutil.rmtree(tmp, ignore_errors=True)

    print()
    if failures == 0:
        print(green(f"==== all {total} cases passed ===="))
        return 0
    print(red(f"==== {failures}/{total} failed ===="))
    return 1


if __name__ == "__main__":
    sys.exit(main())
