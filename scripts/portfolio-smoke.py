"""Local smoke test for the v0.10 portfolio MCP tool surface.

Spawns the MCP stdio server with an isolated SQLite DB and exercises the
five v0.10 domain dispatchers (ls_account / ls_holding / ls_watchlist /
ls_watched_themes / ls_portfolio_io) plus the two standalone portfolio
tools (ls_holdings_list, ls_stocks_refresh_metadata). When LS credentials
are available (env vars or the .env.local at ENV_LOCAL_PATH), the
market-data routes are smoke-checked too; without them the script still
exercises every local path and the quote_error envelope.

v0.10 coverage:
- 5 domain dispatchers — action routing + per-action arg validation.
- Tool-surface regression: merged v0.9 tool names absent from tools/list,
  dispatchers present, catalog trio hidden in the default `standard`
  profile.
- portfolio export/import round-trip + replace-mode confirm gate.

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

    def j(obj):
        return json.dumps(obj, ensure_ascii=False)

    try:
        init = client.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "portfolio-smoke", "version": "0.0"},
        })
        si = init["result"]["serverInfo"]
        print(f"[init] {si['name']} v{si['version']}")
        client.notify("notifications/initialized")

        # --- v0.10 tool-surface regression ---
        print("\n[v0.10 tool surface]")
        tools = set(client.list_tools())
        dispatchers = ["ls_account", "ls_watchlist", "ls_watched_themes", "ls_portfolio_io", "ls_holding"]
        check("5 domain dispatchers registered",
              set(dispatchers) <= tools,
              f"missing: {sorted(set(dispatchers) - tools)}")
        check("standalone portfolio tools kept",
              {"ls_holdings_list", "ls_stocks_refresh_metadata"} <= tools,
              j(sorted({"ls_holdings_list", "ls_stocks_refresh_metadata"} & tools)))
        merged = [
            "ls_accounts_list", "ls_account_upsert", "ls_account_remove",
            "ls_watchlist_add", "ls_watchlist_remove", "ls_watchlist_list",
            "ls_watchlist_group_create", "ls_watchlist_group_delete",
            "ls_watched_themes_add", "ls_watched_themes_remove", "ls_watched_themes_list",
            "ls_portfolio_export", "ls_portfolio_import",
            "ls_holdings_set", "ls_holdings_buy", "ls_holdings_sell",
            "ls_holdings_remove", "ls_holdings_corporate_action",
        ]
        check("merged v0.9 tool names absent",
              not (set(merged) & tools),
              f"still present: {sorted(set(merged) & tools)}")
        catalog = {"ls_search_tr", "ls_describe_tr", "ls_call_tr"}
        check("catalog trio hidden in standard profile",
              not (catalog & tools),
              f"unexpectedly present: {sorted(catalog & tools)}")
        check("standard surface is 32 tools", len(tools) == 32, f"{len(tools)} tools")

        # --- empty state ---
        print("\n[empty state]")
        accs = client.call_tool("ls_account", {"action": "list"})
        check("ls_account list empty initially", isinstance(accs, list) and len(accs) == 0, j(accs))

        hl_empty = client.call_tool("ls_holdings_list")
        check("holdings_list empty when no accounts", hl_empty.get("accounts") == [], j(hl_empty))

        empty_set = client.call_tool("ls_holding", {"action": "set", "shcode": "005930", "quantity": 10, "avg_price": 70000})
        check("ls_holding set RequiresAccount when no accounts",
              empty_set.get("error") == "RequiresAccount", j(empty_set))

        # --- ls_account dispatcher ---
        print("\n[ls_account]")
        hantoo = client.call_tool("ls_account", {
            "action": "upsert", "account_number": "12345-01", "nickname": "한투", "broker": "한국투자"})
        check("upsert 한투 auto-promotes to default",
              hantoo.get("nickname") == "한투" and hantoo.get("is_default") is True, j(hantoo))

        kb = client.call_tool("ls_account", {
            "action": "upsert", "account_number": "67890-22", "nickname": "KB ISA", "broker": "KB증권"})
        check("upsert KB ISA does not displace default", kb.get("is_default") is False, j(kb))

        dup = client.call_tool("ls_account", {
            "action": "upsert", "account_number": "99999-99", "nickname": "한투", "broker": "X"})
        check("nickname collision → ValidationError", dup.get("error") == "ValidationError", j(dup))

        miss = client.call_tool("ls_account", {"action": "upsert", "nickname": "한투"})
        check("upsert missing account_number → structured error",
              "missing required" in str(miss.get("error", "")) and "account_number" in str(miss.get("error", "")),
              j(miss))

        bad_action = client.call_tool("ls_account", {"action": "frobnicate"})
        check("unknown action → valid_actions echoed",
              "unknown action" in str(bad_action.get("error", ""))
              and bad_action.get("details", {}).get("valid_actions") == ["list", "upsert", "remove"],
              j(bad_action))

        accs2 = client.call_tool("ls_account", {"action": "list"})
        check("ls_account list shows 2 accounts", isinstance(accs2, list) and len(accs2) == 2, f"{len(accs2)}")

        switched = client.call_tool("ls_account", {
            "action": "upsert", "account_number": "67890-22", "nickname": "KB ISA",
            "broker": "KB증권", "set_default": True})
        check("set_default via upsert", switched.get("is_default") is True, j(switched))
        client.call_tool("ls_account", {
            "action": "upsert", "account_number": "12345-01", "nickname": "한투",
            "broker": "한국투자", "set_default": True})

        renamed = client.call_tool("ls_account", {
            "action": "upsert", "rename_broker_from": "KB증권", "broker": "KB증권 (PB)"})
        check("rename-broker mode reports accounts_affected",
              renamed.get("accounts_affected") == 1 and renamed.get("to") == "KB증권 (PB)", j(renamed))

        # --- ls_holding dispatcher ---
        print("\n[ls_holding]")
        first_buy = client.call_tool("ls_holding", {
            "action": "buy", "shcode": "005930", "quantity": 10, "price": 70000, "account": "한투"})
        check("buy records position",
              first_buy.get("quantity") == 10 and first_buy.get("avg_price") == 70000, j(first_buy))

        weighted = client.call_tool("ls_holding", {
            "action": "buy", "shcode": "005930", "quantity": 5, "price": 80000, "account": "한투"})
        expected_avg = (10 * 70000 + 5 * 80000) / 15
        check("buy merges weighted average",
              weighted.get("quantity") == 15 and abs(weighted.get("avg_price", 0) - expected_avg) < 1,
              f"qty={weighted.get('quantity')} avg={weighted.get('avg_price')!r}")

        miss_buy = client.call_tool("ls_holding", {"action": "buy", "shcode": "005930", "quantity": 5})
        check("buy missing price → structured error",
              "missing required" in str(miss_buy.get("error", "")) and "price" in str(miss_buy.get("error", "")),
              j(miss_buy))

        kb_buy = client.call_tool("ls_holding", {
            "action": "buy", "shcode": "005930", "quantity": 4, "price": 90000, "account": "KB ISA"})
        check("buy on second account", kb_buy.get("applied_to", {}).get("nickname") == "KB ISA", j(kb_buy))

        ambig = client.call_tool("ls_holding", {"action": "sell", "shcode": "005930", "quantity": 1})
        check("sell without account → AmbiguousAccount",
              ambig.get("error") == "AmbiguousAccount" and len(ambig.get("candidates", [])) == 2, j(ambig))

        targeted = client.call_tool("ls_holding", {
            "action": "sell", "shcode": "005930", "quantity": 5, "account": "한투"})
        check("targeted sell reduces quantity", targeted.get("quantity") == 10, j(targeted))

        over = client.call_tool("ls_holding", {
            "action": "sell", "shcode": "005930", "quantity": 999, "account": "한투"})
        check("over-sell → InsufficientQuantity",
              over.get("error") == "InsufficientQuantity" and over.get("current_quantity") == 10, j(over))

        split = client.call_tool("ls_holding", {
            "action": "corporate_action", "shcode": "005930", "type": "split", "ratio": 2})
        applied = split.get("applied_to") or []
        check("corporate_action split applies to all holders",
              len(applied) == 2 and all(r["after"]["quantity"] == r["before"]["quantity"] * 2 for r in applied),
              j([a["after"]["quantity"] for a in applied]))

        bad_type = client.call_tool("ls_holding", {
            "action": "corporate_action", "shcode": "005930", "type": "stock_dividend", "ratio": 0.05})
        check("corporate_action unknown type → ValidationError + hint",
              bad_type.get("error") == "ValidationError" and "Additional types" in str(bad_type.get("message", "")),
              j(bad_type))

        miss_ca = client.call_tool("ls_holding", {"action": "corporate_action", "shcode": "005930"})
        check("corporate_action missing type/ratio → structured error",
              "missing required" in str(miss_ca.get("error", "")), j(miss_ca))

        # --- ls_holdings_list (standalone) ---
        print("\n[ls_holdings_list]")
        hl = client.call_tool("ls_holdings_list")
        accounts = hl.get("accounts") or []
        check("holdings_list groups both accounts",
              len(accounts) == 2 and all("summary" in a for a in accounts),
              j([a["nickname"] for a in accounts]))
        if live:
            check("live quote enriches a row",
                  any(h.get("quote") for a in accounts for h in a.get("holdings", [])),
                  f"quote_error={hl.get('quote_error')}")
        else:
            check("offline holdings_list returns quote_error", hl.get("quote_error") is not None,
                  f"{hl.get('quote_error')!r}")

        removed = client.call_tool("ls_holding", {"action": "remove", "shcode": "005930", "account": "한투"})
        check("ls_holding remove drops the row", removed.get("removed") is True, j(removed))

        # --- ls_watchlist dispatcher ---
        print("\n[ls_watchlist]")
        grp = client.call_tool("ls_watchlist", {"action": "group_upsert", "name": "semis", "description": "반도체"})
        check("group_upsert creates a group", "error" not in grp, j(grp))

        ren = client.call_tool("ls_watchlist", {
            "action": "group_upsert", "name": "semiconductors", "rename_from": "semis"})
        check("group_upsert rename via rename_from", "error" not in ren, j(ren))

        added = client.call_tool("ls_watchlist", {"action": "add", "shcode": "005930", "group_name": "semiconductors"})
        check("watchlist add", added.get("shcode") == "005930", j(added))

        bad_add = client.call_tool("ls_watchlist", {"action": "add", "shcode": "12X"})
        check("watchlist add rejects malformed shcode", bad_add.get("error") == "ValidationError", j(bad_add))

        miss_add = client.call_tool("ls_watchlist", {"action": "add"})
        check("watchlist add missing shcode → structured error",
              "missing required" in str(miss_add.get("error", "")), j(miss_add))

        wl = client.call_tool("ls_watchlist", {"action": "list"})
        check("watchlist list returns groups", "groups" in wl, j(list(wl.keys())))

        wl_groups = client.call_tool("ls_watchlist", {"action": "list", "scope": "groups"})
        check("watchlist list scope=groups",
              wl_groups.get("scope") == "groups"
              and any(g["name"] == "semiconductors" for g in wl_groups.get("groups", [])),
              j(wl_groups))

        bad_scope = client.call_tool("ls_watchlist", {"action": "list", "scope": "everything"})
        check("watchlist list bad scope → error", "not recognized" in str(bad_scope.get("error", "")), j(bad_scope))

        # --- ls_watched_themes dispatcher ---
        print("\n[ls_watched_themes]")
        th_add = client.call_tool("ls_watched_themes", {
            "action": "add", "theme_code": "0064", "theme_name": "2차전지", "note": "smoke"})
        check("watched_themes add", th_add.get("theme_code") == "0064", j(th_add))

        th_list = client.call_tool("ls_watched_themes", {"action": "list"})
        items = th_list.get("items") or []
        check("watched_themes list returns the theme",
              len(items) == 1 and items[0].get("theme_code") == "0064", j([i.get("theme_code") for i in items]))

        th_rm = client.call_tool("ls_watched_themes", {"action": "remove", "theme_code": "0064"})
        check("watched_themes remove", th_rm.get("removed") is True, j(th_rm))

        # --- ls_account remove cascade ---
        print("\n[ls_account remove]")
        rm_unsafe = client.call_tool("ls_account", {"action": "remove", "account": "KB ISA", "confirm": False})
        check("remove account w/ holdings & confirm=false → RequiresConfirmation",
              rm_unsafe.get("error") == "RequiresConfirmation" and rm_unsafe.get("holding_count", 0) > 0,
              j(rm_unsafe))
        rm_ok = client.call_tool("ls_account", {"action": "remove", "account": "KB ISA", "confirm": True})
        check("remove account w/ confirm=true cascades",
              rm_ok.get("removed") is True and rm_ok.get("cascaded_holdings", 0) > 0, j(rm_ok))

        miss_rm = client.call_tool("ls_account", {"action": "remove"})
        check("remove missing account → structured error",
              "missing required" in str(miss_rm.get("error", "")), j(miss_rm))

        # --- ls_portfolio_io dispatcher ---
        print("\n[ls_portfolio_io]")
        export_path = str(tmp / "smoke-export.json")
        exported = client.call_tool("ls_portfolio_io", {"action": "export", "path": export_path})
        check("export writes JSON schema_version=1",
              exported.get("schema_version") == 1 and Path(exported.get("path", "")).is_file(), j(exported))

        gated = client.call_tool("ls_portfolio_io", {
            "action": "import", "path": export_path, "mode": "replace", "confirm": False})
        check("import replace without confirm → RequiresConfirmation",
              gated.get("error") == "RequiresConfirmation", j(gated))

        merged_import = client.call_tool("ls_portfolio_io", {
            "action": "import", "path": export_path, "mode": "merge"})
        check("import merge re-import finds duplicates",
              merged_import.get("mode") == "merge"
              and merged_import.get("imported", {}).get("accounts") == 0,
              j(merged_import.get("imported")))

        miss_import = client.call_tool("ls_portfolio_io", {"action": "import"})
        check("import missing path → structured error",
              "missing required" in str(miss_import.get("error", "")), j(miss_import))

        bad_schema = tmp / "bad-schema.json"
        bad_schema.write_text(json.dumps({
            "schema_version": 99, "exported_at": "2026-05-20T12:00:00+09:00",
            "exporter_version": "future", "accounts": [], "watchlist_groups": [], "watched_themes": [],
        }), encoding="utf-8")
        mismatch = client.call_tool("ls_portfolio_io", {"action": "import", "path": str(bad_schema), "mode": "merge"})
        check("schema_version=99 → ImportSchemaMismatch",
              mismatch.get("error") == "ImportSchemaMismatch" and mismatch.get("file_schema_version") == 99,
              j(mismatch))

        # --- market-data route smoke ---
        print("\n[market-data routes]")
        idx = client.call_tool("ls_get_index_quote", {"index_code": "kospi"})
        check("ls_get_index_quote routes", isinstance(idx, dict), j(idx)[:120])

        ih = client.call_tool("ls_get_index_history", {"index_code": "kospi", "output_mode": "export", "count": 60})
        if live:
            check("ls_get_index_history export returns dataset_id",
                  ih.get("output_mode") == "export" and str(ih.get("dataset_id", "")).startswith("ds_"),
                  j(ih)[:140])
        else:
            check("ls_get_index_history export routes without credentials", isinstance(ih, dict), j(ih)[:120])

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
