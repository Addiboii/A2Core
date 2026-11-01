"""Quest reference: every quest up to the live level cap, with Russian text, and the goal-type coverage report.

Inputs:
- tools/data/quests.json (python tools/quests.py build; client Quest*.dat via WorldProbe, see docs/quest-system.md).
- research/local/l10n-20261030/localization-001.json (ru-RU L10NString.dat export, optional; texts stay English without it).
- server/Aion.World/*.cs (grep: which objective types the server handles).

Outputs:
- tools/data/quest-reference.json: quests with level <= cap that are not marked unavailable.
- docs/quest-coverage.md: goal types and quests supported vs missing (generated, do not edit).

Usage: python tools/quest_reference.py [--cap 45]
"""
import argparse
import sys
import json
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/quests.json'
OUT = ROOT / 'tools/data/quest-reference.json'
DOC = ROOT / 'docs/quest-coverage.md'
RU = ROOT / 'research/local/l10n-20261030/localization-001.json'
SERVER = ROOT / 'server/Aion.World'
sys.path.insert(0, str(ROOT / 'tools'))

# How the server handles each objective type (verified by reading server/Aion.World; the grep below re-checks presence).
HANDLING = {
    'KillNpc': 'QuestLog.Killed (kill credit, map-event KillNpc)',
    'ClearMapEvent': 'map event run DungeonClear -> QuestLog progress',
    'AskNpc': 'NPC talk (0x9230 / NpcConversation)',
    'CloseNpc': 'NPC talk, same path as AskNpc',
    'UseEnvObj': 'env object use (GameSession.Quests.World)',
    'EnterVolumePC': 'volume entry (Quests.cs radius CANDIDATE)',
    'EnterSubZone': 'sub-zone entry (GameSession.Quests.World)',
    'CompleteCelviceTalk': 'client objective 0x9230 (ClientObjectiveTypes)',
    'CloseNote': 'client objective 0x9230 (ClientObjectiveTypes)',
    'UseFakeItem': 'client objective 0x9230 (ClientObjectiveTypes)',
    'CollectFakeItem': 'drop chance on KillNpc targets',
    'CollectItem': 'inventory count (GameSession.Quests.World)',
    'KillPC': 'PvP kill (GameSession.Pvp)',
    'KillPCAbyssGrade': 'Abyss PvP kill (GameSession.Abyss)',
    'ClearQuest': 'evaluated on step check (Quests.cs)',
    'ClearQuestType': 'evaluated on step check (Quests.cs)',
    'ClearQuestExplorationType': 'Exploration subtype counts on step check (QuestBook.ExplorationSubtype)',
    'EngraveTeleportArtifact': 'teleport artifact engrave (GameSession.Quests.World)',
}


def server_types(server=SERVER):
    found = set()
    for path in server.glob('*.cs'):
        found.update(re.findall(r'"([A-Z][A-Za-z]+)"', path.read_text(encoding='utf-8', errors='replace')))
    return found


def ru_quests(path=RU):
    """The same generator run against the ru-RU L10NString export: code -> quest with Russian texts."""
    if not Path(path).exists():
        return {}
    try:
        from quests import build
    except ImportError:
        from tools.quests import build
    return build(localization=path)['quests']


def reference(data, cap, supported, ru):
    quests = []
    for code, q in sorted(data['quests'].items()):
        if q.get('unavailable') or (q.get('level') or 0) > cap:
            continue
        rq = ru.get(code) or {}
        rsteps = {s['order']: s for s in rq.get('steps') or []}
        steps = []
        for s in q.get('steps') or []:
            rs = rsteps.get(s['order'], {})
            robj = rs.get('objectives') or []
            steps.append({
                'order': s['order'], 'id': s.get('id'), 'text': s.get('text'), 'text_ru': rs.get('text'),
                'objectives': [dict(o, text_ru=(robj[i].get('text') if i < len(robj) else None),
                                    supported=o['type'] in supported)
                               for i, o in enumerate(s['objectives'])],
            })
        title_ru = rq.get('title')
        quests.append({
            'code': code, 'id': q['id'], 'title': q.get('title'), 'title_ru': title_ru, 'type': q.get('type'),
            'race': q.get('race'), 'level': q.get('level'), 'recommended_level': q.get('recommended_level'),
            'map': q.get('map'), 'acquire': q.get('acquire'), 'start_npc': q.get('start_npc'),
            'start_npc_name': q.get('start_npc_name'), 'complete': q.get('complete'),
            'finish_npc': q.get('finish_npc'), 'finish_npc_name': q.get('finish_npc_name'),
            'previous': q.get('previous'), 'next': q.get('next'), 'prerequisites': q.get('prerequisites'),
            'repeat': q.get('repeat'), 'steps': steps,
            'supported': all(o['supported'] for s in steps for o in s['objectives']),
        })
    return quests


def coverage_md(quests, supported, cap):
    types = Counter(o['type'] for q in quests for s in q['steps'] for o in s['objectives'])
    quests_by_type = Counter()
    for q in quests:
        for t in {o['type'] for s in q['steps'] for o in s['objectives']}:
            quests_by_type[t] += 1
    ok = sum(q['supported'] for q in quests)
    lines = [
        '# Quest coverage (generated)', '',
        f'Generated by `python tools/quest_reference.py --cap {cap}` from tools/data/quests.json and server/Aion.World. Do not edit.', '',
        f'- Quests up to level {cap} (available): **{len(quests)}**',
        f'- Fully supported (every objective type handled): **{ok}** ({ok * 100 // max(len(quests), 1)}%)',
        f'- With at least one missing goal type: **{len(quests) - ok}**', '',
        '| Goal type | Objectives | Quests | Server | Handling |', '|---|---:|---:|---|---|',
    ]
    for t, n in types.most_common():
        lines.append(f"| {t} | {n} | {quests_by_type[t]} | {'yes' if t in supported else '**missing**'} | {HANDLING.get(t, '-')} |")
    missing = [q for q in quests if not q['supported']]
    if missing:
        lines += ['', '## Quests with missing goal types', '', '| Quest | Level | Type | Missing |', '|---|---:|---|---|']
        for q in missing:
            miss = sorted({o['type'] for s in q['steps'] for o in s['objectives'] if not o['supported']})
            lines.append(f"| {q['code']} {q['title'] or ''} | {q['level']} | {q['type']} | {', '.join(miss)} |")
    acquire = Counter(q['acquire'] for q in quests)
    lines += ['', '## Acquire types', '', '| Acquire | Quests |', '|---|---:|']
    lines += [f'| {k} | {v} |' for k, v in acquire.most_common()]
    return '\n'.join(lines) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--cap', type=int, default=45)
    args = parser.parse_args()
    data = json.loads(DATA.read_text(encoding='utf-8'))
    grep = server_types()
    supported = {t for t in HANDLING if t in grep}
    quests = reference(data, args.cap, supported, ru_quests())
    OUT.write_text(json.dumps({'cap': args.cap, 'source': 'tools/data/quests.json', 'quests': quests},
                              ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
    DOC.write_text(coverage_md(quests, supported, args.cap), encoding='utf-8')
    print('wrote %s (%d quests) and %s' % (OUT, len(quests), DOC))


if __name__ == '__main__':
    main()
