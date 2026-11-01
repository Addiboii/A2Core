#!/usr/bin/env python3
"""Summarize Item.dat UseEffect coverage in tools/data/boxes.json vs server handlers.

Writes docs/parity/ITEM_USE_COVERAGE.md (and optional JSON).

Run:
  python tools/item_use_coverage.py
  python tools/item_use_coverage.py --json docs/parity/item-use-coverage.json
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BOXES = ROOT / 'tools/data/boxes.json'
DOC = ROOT / 'docs/parity/ITEM_USE_COVERAGE.md'

# Known missing handlers (Wave 6 audit pin); counts come from boxes.json when rebuilt from Item.dat.
MISSING_EFFECT_NOTES = {
    'GetGodstone': 'Godstone codex / socket (engrave uses 0x3777, not item use)',
    'GetArcana': 'Arcana inventory',
    'AddContentsTicket': 'Contents tickets (instances)',
    'GetBattlePassExp': 'Battle pass XP items',
    'GetSubscription': 'Subscription items',
    'GetDutyQuest': 'Duty quest scrolls',
    'GetForceMastery': 'Force mastery books',
    'GetManastone': 'Manastones',
    'RegisterKisk': 'Kisk registration',
    'ExpandInventory': 'Bag expansion',
    'ExpandWarehouse': 'Warehouse expansion',
}


def load_handled(boxes: dict) -> set[str]:
    handled = boxes.get('handled_use_effects')
    if isinstance(handled, list):
        return set(handled)
    try:
        from box_data import HANDLED_USE_EFFECTS
    except ImportError:
        from tools.box_data import HANDLED_USE_EFFECTS
    return set(HANDLED_USE_EFFECTS)


def summarize(boxes: dict) -> dict:
    uses = boxes['uses']
    handled = load_handled(boxes)
    by_effect = Counter(row[0] for row in uses.values())
    implemented = {e: by_effect[e] for e in sorted(by_effect) if e in handled}
    missing = {e: by_effect[e] for e in sorted(by_effect) if e not in handled}
    return {
        'usable_items': len(uses),
        'use_effect_types': len(by_effect),
        'implemented_effects': implemented,
        'missing_handlers': missing,
        'implemented_item_count': sum(implemented.values()),
        'missing_handler_item_count': sum(missing.values()),
        'handled_use_effects': sorted(handled),
    }


def render_md(report: dict) -> str:
    lines = [
        '# Item use: UseEffect export vs server handlers',
        '',
        '**Data:** `tools/data/boxes.json` (`python tools/box_data.py` ← Item.dat).',
        '**Code:** `GameSession.Rides.cs` (0x5612 / 0x5613), `RewardBoxes.ImplementsUseEffect`.',
        '**Audit:** `python tools/audit_item_effects.py`, `python tools/item_use_coverage.py`.',
        '',
        f"- Usable items in export: **{report['usable_items']}** ({report['use_effect_types']} distinct UseEffect types)",
        f"- Items with implemented handler: **{report['implemented_item_count']}**",
        f"- Items exported but handler missing (0x5613 result 1 + `item_use_refused`): **{report['missing_handler_item_count']}**",
        '',
        '## Implemented UseEffect (server handler)',
        '',
        '| UseEffect | Items |',
        '|-----------|------:|',
    ]
    for effect, count in sorted(report['implemented_effects'].items(), key=lambda kv: (-kv[1], kv[0])):
        lines.append(f'| {effect} | {count} |')
    lines.extend(['', '## Exported UseEffect without handler', '', '| UseEffect | Items | Note |', '|-----------|------:|------|'])
    for effect, count in sorted(report['missing_handlers'].items(), key=lambda kv: (-kv[1], kv[0])):
        note = MISSING_EFFECT_NOTES.get(effect, '')
        lines.append(f'| {effect} | {count} | {note} |')
    lines.extend([
        '',
        '## Rebuild',
        '',
        '1. `research/local/game-catalog-v2/entries.jsonl` after WorldProbe / `python tools/game_catalog.py build`.',
        '2. `python tools/box_data.py` - all Item.dat rows with UseEffect -> `uses` (Wave 6 / W6).',
        '3. `python tools/item_use_coverage.py` - refresh this file.',
        '',
    ])
    return '\n'.join(lines) + '\n'


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', type=Path, help='Write machine-readable summary')
    args = parser.parse_args()
    boxes = json.loads(BOXES.read_text(encoding='utf-8'))
    report = summarize(boxes)
    DOC.parent.mkdir(parents=True, exist_ok=True)
    DOC.write_text(render_md(report), encoding='utf-8')
    print(f'Wrote {DOC.relative_to(ROOT)}', flush=True)
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
