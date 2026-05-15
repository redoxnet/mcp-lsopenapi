"""Local smoke test for portfolio MCP tools.

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
    """Parse a simple KEY=VALUE file. Lines starting with # are skipped."""
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

        # --- account ---
        print("\n[account]")
        acc = client.call_tool("ls_account_get")
        check("account_get → seeded default",
              acc.get("account_no") == "UNSET" and acc.get("nickname") == "기본 계좌",
              json.dumps(acc, ensure_ascii=False))

        acc2 = client.call_tool("ls_account_set", {"account_no": "12345678-01", "nickname": "테스트"})
        check("account_set persists changes",
              acc2.get("account_no") == "12345678-01" and acc2.get("nickname") == "테스트",
              json.dumps(acc2, ensure_ascii=False))

        # --- watchlist groups ---
        print("\n[watchlist groups]")
        groups = client.call_tool("ls_watchlist_groups_list")
        check("groups_list shows seeded 'default'",
              isinstance(groups, list) and any(g.get("name") == "default" for g in groups),
              f"{len(groups) if isinstance(groups, list) else '?'} groups")

        created = client.call_tool("ls_watchlist_group_create", {"name": "semis", "description": "반도체"})
        check("group_create semis",
              created.get("name") == "semis" and created.get("description") == "반도체",
              json.dumps(created, ensure_ascii=False))

        # --- watchlist items ---
        print("\n[watchlist items]")
        a1 = client.call_tool("ls_watchlist_add", {"shcode": "005930", "group_name": "semis", "note": "core"})
        check("watchlist_add 005930 → semis",
              a1.get("shcode") == "005930" and a1.get("group_name") == "semis" and a1.get("note") == "core",
              json.dumps(a1, ensure_ascii=False))

        a2 = client.call_tool("ls_watchlist_add", {"shcode": "000660", "group_name": "semis"})
        check("watchlist_add 000660 → semis",
              a2.get("shcode") == "000660",
              json.dumps(a2, ensure_ascii=False))

        listed = client.call_tool("ls_watchlist_list", {"group_name": "semis"})
        sub = (listed.get("groups") or [{}])[0]
        items = sub.get("items") or []
        if live:
            quoted = [i for i in items if i.get("quote") is not None]
            check("watchlist_list semis: 2 items, all with live quotes",
                  len(items) == 2 and len(quoted) == 2 and listed.get("quote_error") is None,
                  f"{len(items)} items, {len(quoted)} quoted, quote_error={listed.get('quote_error')!r}")
            if quoted:
                q = quoted[0].get("quote") or {}
                check("watchlist_list quote populates price/name/timestamp",
                      isinstance(q.get("price"), int) and q.get("price") > 0
                      and quoted[0].get("name") and not quoted[0].get("name", "").isdigit()
                      and q.get("timestamp"),
                      f"{quoted[0].get('name')!r} price={q.get('price')} ts={q.get('timestamp')}")
        else:
            check("watchlist_list semis returns 2 items, quote_error set (no creds)",
                  len(items) == 2 and listed.get("quote_error") is not None,
                  f"{len(items)} items, quote_error={listed.get('quote_error')!r}")

        listed_all = client.call_tool("ls_watchlist_list")
        names = [g.get("name") for g in (listed_all.get("groups") or [])]
        check("watchlist_list (no group) includes default+semis",
              "default" in names and "semis" in names,
              f"groups={names}")

        rem = client.call_tool("ls_watchlist_remove", {"shcode": "000660", "group_name": "semis"})
        check("watchlist_remove 000660 → removed=true",
              rem.get("removed") is True,
              json.dumps(rem))

        bad = client.call_tool("ls_watchlist_add", {"shcode": "12X", "group_name": "semis"})
        check("watchlist_add rejects malformed shcode",
              "error" in bad,
              json.dumps(bad, ensure_ascii=False))

        # --- group delete ---
        print("\n[group delete]")
        d1 = client.call_tool("ls_watchlist_group_delete", {"name": "semis"})
        check("group_delete cascades 1 remaining item",
              d1.get("deleted") is True and d1.get("cascaded_items") == 1,
              json.dumps(d1))

        d2 = client.call_tool("ls_watchlist_group_delete", {"name": "no-such"})
        check("group_delete missing → deleted=false, cascaded=0",
              d2.get("deleted") is False and d2.get("cascaded_items") == 0,
              json.dumps(d2))

        # --- sectors ---
        print("\n[sectors]")
        s1 = client.call_tool("ls_watched_sectors_add", {"sector_code": "0012", "sector_name": "반도체 장비"})
        check("sector_add 0012",
              s1.get("sector_code") == "0012",
              json.dumps(s1, ensure_ascii=False))

        sl = client.call_tool("ls_watched_sectors_list")
        sitems = sl.get("items") or []
        if live:
            row = next((i for i in sitems if i.get("sector_code") == "0012"), None)
            check("sector_list returns 0012 with t1531 change_pct",
                  row is not None and (row.get("quote") or {}).get("change_pct") is not None and sl.get("quote_error") is None,
                  f"quote={row.get('quote') if row else None}, quote_error={sl.get('quote_error')!r}")
        else:
            check("sector_list returns 0012, quote_error set",
                  any(i.get("sector_code") == "0012" for i in sitems) and sl.get("quote_error") is not None,
                  f"{len(sitems)} items, quote_error={sl.get('quote_error')!r}")

        s2 = client.call_tool("ls_watched_sectors_remove", {"sector_code": "0012"})
        check("sector_remove 0012",
              s2.get("removed") is True,
              json.dumps(s2))

        # --- holdings ---
        print("\n[holdings]")
        h1 = client.call_tool("ls_holdings_add", {"shcode": "005930", "quantity": 10, "avg_price": 70000, "note": "first"})
        check("holdings_add 005930 x10 @ 70000",
              h1.get("shcode") == "005930" and h1.get("quantity") == 10 and h1.get("avg_price") == 70000,
              json.dumps(h1, ensure_ascii=False))

        h2 = client.call_tool("ls_holdings_update", {"shcode": "005930", "quantity": 12, "avg_price": 71000})
        check("holdings_update qty=12 avg=71000",
              h2.get("quantity") == 12 and h2.get("avg_price") == 71000,
              json.dumps(h2))

        hl = client.call_tool("ls_holdings_list")
        hitems = hl.get("items") or []
        check("holdings_list returns 1 item with cost summary",
              len(hitems) == 1 and hl.get("summary", {}).get("total_cost") == 12 * 71000,
              f"summary={hl.get('summary')}, quote_error={hl.get('quote_error')!r}")

        if hitems:
            row = hitems[0]
            if live:
                check("holdings_list name populated from live quote",
                      isinstance(row.get("name"), str) and not row.get("name", "").isdigit(),
                      f"name={row.get('name')!r}")
                check("holdings_list pnl/current_value/total_value computed",
                      row.get("current_value") is not None
                      and row.get("pnl") is not None
                      and row.get("pnl_pct") is not None
                      and hl.get("summary", {}).get("total_value") is not None,
                      f"current_value={row.get('current_value')} pnl={row.get('pnl')} summary={hl.get('summary')}")
            else:
                check("holdings_list name is null without quote",
                      row.get("name") is None,
                      f"name={row.get('name')!r}, quote={row.get('quote') is not None}")
                check("holdings_list pnl fields null without quote",
                      row.get("current_value") is None and row.get("pnl") is None and row.get("pnl_pct") is None,
                      f"current_value={row.get('current_value')} pnl={row.get('pnl')} pnl_pct={row.get('pnl_pct')}")

        hr = client.call_tool("ls_holdings_remove", {"shcode": "005930"})
        check("holdings_remove",
              hr.get("removed") is True,
              json.dumps(hr))

        hr2 = client.call_tool("ls_holdings_remove", {"shcode": "005930"})
        check("holdings_remove (already gone) → removed=false",
              hr2.get("removed") is False,
              json.dumps(hr2))

        hu = client.call_tool("ls_holdings_update", {"shcode": "999999", "quantity": 1})
        check("holdings_update missing → error envelope",
              "error" in hu,
              json.dumps(hu, ensure_ascii=False))

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
