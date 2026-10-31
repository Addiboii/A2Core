"""Cross-check tools/data/quests.json and emit docs/quests/AUDIT.md.

Internal checks: quest graph, unresolved ids, missing triggers/env placements, objective types.
External alignment is manual for starter zones; rows mark source URLs and emulator status.

    python tools/audit_quests.py
"""
from __future__ import annotations

import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QUESTS = ROOT / 'tools/data/quests.json'
NPCS = ROOT / 'tools/data/world-npcs.json'
OBJECTS = ROOT / 'tools/data/world-objects.json'
OUT = ROOT / 'docs/quests/AUDIT.md'

# Public references used when spot-checking starter chains (not scraped automatically).
PUBLIC_SOURCES = {
    'HQ1101011': ('Making Yourself Useful', 'https://aion2.fandom.com/wiki/Making_Yourself_Useful'),
    'HQ1101018': ('Withered Tree Daminu', 'https://aion2.fandom.com/wiki/Withered_Tree_Daminu'),
    'HQ1101020': ('The Power in the Lake', 'https://aion2.fandom.com/wiki/The_Power_in_the_Lake'),
    'HQ2101020': ('Dark counterpart lake quest', 'https://aion2.fandom.com/wiki/Category:Ishalgen_Quests'),
}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding='utf-8'))


def npc_template_ids(npcs: dict) -> set[int]:
    return {int(k) for k in npcs.get('templates', {})}


def map_npc_ids(npcs: dict) -> dict[int, set[int]]:
    by_map: dict[int, set[int]] = defaultdict(set)
    for map_id, rows in npcs.get('maps', {}).items():
        mid = int(map_id)
        for row in rows:
            if isinstance(row, list) and len(row) > 1:
                by_map[mid].add(int(row[1]))
            elif isinstance(row, dict):
                by_map[mid].add(int(row['npc_id']))
    return by_map


def all_spawned_npc_ids(npcs: dict) -> set[int]:
    out: set[int] = set()
    for rows in npcs.get('maps', {}).values():
        for row in rows:
            if isinstance(row, list) and len(row) > 1:
                out.add(int(row[1]))
    return out


def world_triggers(objects: dict) -> dict[int, set[str]]:
    out: dict[int, set[str]] = defaultdict(set)
    for map_id, entry in objects.get('maps', {}).items():
        for t in entry.get('triggers', []):
            out[int(map_id)].add(t['name'])
    return out


def world_envs(objects: dict) -> dict[int, set[int]]:
    out: dict[int, set[int]] = defaultdict(set)
    for map_id, entry in objects.get('maps', {}).items():
        for o in entry.get('objects', []):
            out[int(map_id)].add(o['env'])
    return out


def audit(data: dict, templates: set[int], map_npcs: dict[int, set[int]], spawned_any: set[int],
          triggers: dict[int, set[str]], envs: dict[int, set[int]]):
    quests = data['quests']
    codes = set(quests)
    rows = []
    stats = Counter()

    for code, q in sorted(quests.items()):
        issues = []
        qmap = q.get('map') or 0
        for prev in q.get('previous') or []:
            if prev not in codes:
                issues.append(f'missing previous quest {prev}')
        for pre in q.get('prerequisites') or []:
            if pre not in codes:
                issues.append(f'missing prerequisite {pre}')
        nxt = q.get('next')
        if nxt and nxt not in codes:
            issues.append(f'missing next quest {nxt}')

        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                otype = o.get('type')
                omap = o.get('map', qmap)
                if otype in ('KillNpc', 'CollectFakeItem', 'AskNpc', 'CloseNpc'):
                    ids = o.get('npcs') or ([o['npc']] if o.get('npc') else [])
                    for nid in ids:
                        if isinstance(nid, str):
                            issues.append(f'unresolved npc id {nid!r}')
                        elif nid not in templates:
                            issues.append(f'unknown NpcData template {nid}')
                        elif omap and omap in map_npcs and nid not in map_npcs[omap]:
                            if nid not in spawned_any:
                                issues.append(f'npc {nid} not spawned on map {omap}')
                            # Spawn exists on another map (instance / map event): OK for emulator.
                if otype == 'UseEnvObj':
                    for eid in o.get('envs') or []:
                        if isinstance(eid, str):
                            issues.append(f'unresolved env {eid!r}')
                        elif omap and omap in envs and eid not in envs[omap]:
                            issues.append(f'env {eid} not placed on map {omap}')
                if otype == 'EnterVolumePC':
                    target = o.get('target')
                    if target and omap in triggers and target not in triggers[omap]:
                        issues.append(f'trigger {target!r} missing on map {omap}')

        status = 'ok' if not issues else 'data_issue'
        if code in PUBLIC_SOURCES:
            title, url = PUBLIC_SOURCES[code]
            note = f'Public: [{title}]({url})'
        else:
            note = ''
        if issues:
            stats['quests_with_issues'] += 1
            for _ in issues:
                stats['issue_count'] += 1
        else:
            stats['quests_clean'] += 1
        if issues or code in PUBLIC_SOURCES:
            rows.append({
                'code': code,
                'title': q.get('title') or '',
                'race': q.get('race'),
                'level': q.get('level'),
                'map': qmap,
                'status': status,
                'issues': '; '.join(issues) if issues else '—',
                'notes': note,
            })
    return rows, stats, len(quests)


def render_md(rows: list, stats: Counter, total: int, source_meta: dict) -> str:
    lines = [
        '# Quest data audit',
        '',
        'Generated by `tools/audit_quests.py`. Compare exported `quests.json` against world spawns and',
        'documented public quest pages where available. **Uncertain** rows need manual play or dat mining.',
        '',
        f'- Total quests: **{total}**',
        f'- Clean (internal checks): **{stats["quests_clean"]}**',
        f'- With internal issues listed below: **{stats["quests_with_issues"]}** ({stats["issue_count"]} findings)',
        '',
        'Data source: ' + source_meta.get('tables', 'Quest tables export'),
        '',
        '## Emulator fixes (Oct 2026 starter bugs)',
        '',
        '| Symptom | Root cause (ours) | Fix |',
        '| --- | --- | --- |',
        '| HQ1101018 step 5 never completes | `EnterVolumePC` `value` (radius cm) ignored | `AreaQuestsAsync` passes `TriggerArea.Contains(at, value)` |',
        '| Watchstones not dying / kills not counted | `Immune` abnormal zeroed damage | Quest `OpenKillNpcIds` bypass immunity |',
        '| Early watchstone / chest / scout damage | KillNpc/UseEnvObj world spawns always active | `PendingKillNpcIds`, `ProtectedTalkNpcIds`, respawn on step |',
        '| Chest interact UI stale after step change | `canInteract` only on 0x3634 spawn | `SyncQuestEnvInteractFlags` → 0x363c in `ApplyQuestWorldGating` |',
        '| Party kill credit only on killer | `QuestKillsAsync` local log only | `ApplyPartyQuestKillCreditAsync` within `PartyExpRange` |',
        '| Accept/give-up leave wrong NPC immunity | Gating only on quest progress | `ApplyQuestWorldGating` on 0x8d0a / 0x8d0c |',
        '| CollectItem stuck | `QuestLog.Holding` never called | `SyncQuestCollectItemsAsync` on inventory sync |',
        '| Mob vanishes on large maps | NPC streaming despawn | Keep streaming for open kill objectives until dead |',
        '| HQ1101011 supply box | Verify env **1200530** on map 1000 | Data OK; UseEnvObj uses EnvObj id |',
        '',
        '## Quest rows (issues or public reference)',
        '',
        '| Code | Title | Race | Lvl | Map | Status | Issues | Notes |',
        '| --- | --- | --- | ---: | ---: | --- | --- | --- |',
    ]
    for r in rows:
        lines.append(
            f'| {r["code"]} | {r["title"]} | {r["race"]} | {r["level"]} | {r["map"]} | {r["status"]} | {r["issues"]} | {r["notes"]} |'
        )
    lines.append('')
    return '\n'.join(lines)


def main():
    data = load_json(QUESTS)
    npcs = load_json(NPCS)
    objects = load_json(OBJECTS)
    templates = npc_template_ids(npcs)
    map_npcs = map_npc_ids(npcs)
    spawned_any = all_spawned_npc_ids(npcs)
    triggers = world_triggers(objects)
    envs = world_envs(objects)
    rows, stats, total = audit(data, templates, map_npcs, spawned_any, triggers, envs)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(render_md(rows, stats, total, data.get('source', {})), encoding='utf-8')
    print(f'wrote {OUT}: {total} quests, {stats["quests_with_issues"]} with issues, {len(rows)} table rows')


if __name__ == '__main__':
    main()
