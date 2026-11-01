"""Scan hero starter quests (levels 1–15, maps 1000/1100) for playability gating patterns.

    python tools/audit_starter_bugs.py

Output: summary to stdout; referenced by docs/parity/STARTER_BUGS.ru.md.
"""
from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QUESTS = ROOT / "tools/data/quests.json"
CHAINS = ROOT / "tools/data/hero-chains.json"
STARTER_MAPS = {1000, 1100, 1001, 2001}
MAX_LEVEL = 15


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def hero_starter_codes(chains: dict) -> list[str]:
    out: list[str] = []
    for side in ("light", "dark"):
        for code in chains[side]:
            q = quests.get(code)
            if not q:
                continue
            lvl = q.get("recommended_level") or q.get("level") or 99
            if lvl > MAX_LEVEL:
                break
            out.append(code)
    return out


def future_step_targets(q: dict) -> tuple[list[tuple[int, int]], list[tuple[int, int]]]:
    kills: list[tuple[int, int]] = []
    envs: list[tuple[int, int]] = []
    for step in q.get("steps") or []:
        order = int(step.get("order") or 0)
        for o in step.get("objectives") or []:
            if o.get("type") in ("KillNpc", "CollectFakeItem"):
                for n in o.get("npcs") or []:
                    if isinstance(n, int):
                        kills.append((order, n))
            if o.get("type") == "UseEnvObj":
                for e in o.get("envs") or []:
                    if isinstance(e, int):
                        envs.append((order, e))
    return kills, envs


def main() -> None:
    global quests
    quests = load(QUESTS)["quests"]
    chains = load(CHAINS)
    codes = hero_starter_codes(chains)
    gated_kills: list[str] = []
    gated_envs: list[str] = []
    for code in codes:
        q = quests[code]
        kills, envs = future_step_targets(q)
        kill_ids = {n for _, n in kills}
        for n in kill_ids:
            orders = [o for o, nid in kills if nid == n]
            if len(orders) > 1 or (orders and min(orders) > 1):
                gated_kills.append(f"{code}: npc {n} steps {sorted(set(orders))}")
        for e in {e for _, e in envs}:
            orders = [o for o, eid in envs if eid == e]
            if len(orders) > 1 or (orders and min(orders) > 1):
                gated_envs.append(f"{code}: env {e} steps {sorted(set(orders))}")

    print(f"Hero starter quests scanned: {len(codes)}")
    print(f"Kill/CollectFakeItem gating candidates: {len(gated_kills)}")
    for row in gated_kills[:20]:
        print(" ", row)
    print(f"UseEnvObj gating candidates: {len(gated_envs)}")
    for row in gated_envs[:20]:
        print(" ", row)


if __name__ == "__main__":
    main()
