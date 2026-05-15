"""Local smoke test for v0.5 portfolio MCP tools.

Spawns the MCP stdio server with an isolated SQLite DB. When LS
credentials are available (env vars or the .env.local at ENV_LOCAL_PATH
below), quote-enrich tools are also asserted against live LS data.
Without credentials the script still exercises every tool's local path
and verifies the quote_error envelope.

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

        default = client.call_tool("ls_account_get")
        check("account_get null when no accounts", default is None, f"{default}")

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

        switched = client.call_tool("ls_account_set_default", {"account": "KB ISA"})
        check("set_default by nickname",
              switched.get("is_default") is True and switched.get("nickname") == "KB ISA",
              json.dumps(switched, ensure_ascii=False))
        # switch back for the rest of the smoke
        client.call_tool("ls_account_set_default", {"account": "한투"})

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

        # --- corporate actions ---
        print("\n[corporate actions]")
        split = client.call_tool("ls_holdings_split", {"shcode": "005930", "ratio": 2})
        applied = split.get("applied_to") or []
        check("split with no account applies to all holders",
              len(applied) == 2 and all(r["after"]["quantity"] == r["before"]["quantity"] * 2 for r in applied),
              json.dumps([{a["account"]["nickname"]: a["after"]["quantity"]} for a in applied], ensure_ascii=False))

        # 7 shares before reverse_split, ratio=3 should fail divisibility
        client.call_tool("ls_holdings_set", {"shcode": "000660", "quantity": 7, "avg_price": 100000, "account": "한투"})
        bad_rev = client.call_tool("ls_holdings_reverse_split", {"shcode": "000660", "ratio": 3, "account": "한투"})
        check("reverse_split non-divisible → ValidationError",
              bad_rev.get("error") == "ValidationError",
              json.dumps(bad_rev, ensure_ascii=False))

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
