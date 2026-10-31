"""Official BM shop catalog from a local platform capture (never committed).

Usage: python tools/shop_official_import.py <dir with extracted response bodies>
Reads the goods/v2.0/goods, goods/v2.0/me/goods and goods/v2.0/me/goods/policy JSON bodies and writes
tools/data/official_shop.json (embedded by Aion.Host). Account/character state is stripped: purchase counters
are zeroed, remaining_seconds dropped (recomputed by the server) and restricted_game_server_ids cleared so
goods are not hidden on our game server ids.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

OUT = ROOT / 'tools/data/official_shop.json'


def load(p: Path):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def clean_goods(g: dict) -> dict:
    g = dict(g)
    if 'restricted_game_server_ids' in g:
        g['restricted_game_server_ids'] = ''
    return g


def clean_quota(q: dict) -> dict:
    q = dict(q)
    q['is_purchasable'] = '1'
    limits = []
    for lim in q.get('limit_list', []):
        lim = dict(lim)
        if 'purchased_quantity' in lim:
            lim['purchased_quantity'] = '0'
        lim.pop('remaining_seconds', None)
        limits.append(lim)
    q['limit_list'] = limits
    return q


def main(src: Path) -> None:
    goods, me_goods, quotas, revision = {}, {}, {}, '1'
    for f in sorted(src.glob('*.json')):
        d = load(f)
        if isinstance(d, dict) and 'quotas' in d:
            for q in d['quotas']:
                quotas[q['goods_id']] = clean_quota(q)
        elif isinstance(d, dict) and 'page' in d:
            pgs15 = all(any(p.get('pg_id') == '15' for p in g.get('pgs', [])) for g in d['page'])
            target = me_goods if d['page'] and pgs15 and len(d['page']) < 100 else goods
            for g in d['page']:
                target[g['goods_id']] = clean_goods(g)
            if target is goods:
                revision = d.get('revision_id', revision)
    write_data_json(OUT, {
        'source': 'official goods/v2.0 capture 2026-10-30 (account data stripped)',
        'revision_id': revision,
        'goods': list(goods.values()),
        'me_goods': list(me_goods.values()),
        'quotas': list(quotas.values()),
    })
    print(f'goods={len(goods)} me_goods={len(me_goods)} quotas={len(quotas)} -> {OUT}')


if __name__ == '__main__':
    main(Path(sys.argv[1]))
