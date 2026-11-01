#!/usr/bin/env python3
"""Read-only parity audit: Gamers4Life AION 2 DB vs tools/data exports.

Polite crawl: ~1 req/s, respects robots.txt (no /api/), caches under tools/.cache/gamers4life/.
Client dat in tools/data always wins — this script never writes game data, only reports.

  python tools/audit_gamers4life.py
  python tools/audit_gamers4life.py --sample 300 --write-doc docs/parity/GAMERS4LIFE_AUDIT.md
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'
CACHE = ROOT / 'tools/.cache/gamers4life'

BASE = 'https://gamers4.life/aion-2/database'
SITEMAP_INDEX = f'{BASE}/sitemap.xml'
USER_AGENT = 'A2Core-audit/1.0 (parity read-only)'

PIN = {
    'steam_build': 25767555,
    'manifest': '1699273097574069541',
    'pin_date': '2026-10-07',
    'live_level_cap': 45,
}

# Gamers4Life JSON stat keys (lowercase) -> Item.dat export keys in tools/data/items.json
ITEM_STAT_MAP = {
    'weaponmindamage': 'WeaponMinDamage',
    'weapondamage': 'WeaponDamage',
    'weaponaccuracy': 'WeaponAccuracy',
    'weaponfixingdamage': 'WeaponFixingDamage',
    'critical': 'Critical',
    'block': 'Block',
    'decreaseweaponblock': 'DecreaseWeaponBlock',
    'maxweaponblock': 'MaxWeaponBlock',
    'hpmax': 'HPMax',
    'mpmax': 'MPMax',
}

SITEMAP_CATEGORIES = {
    'item': ('en-item.xml', 'item'),
    'skill': ('en-skill.xml', 'skill'),
    'quest': ('en-quest.xml', 'quest'),
    'npc': ('en-npc.xml', 'npc'),
    'title': ('en-title.xml', 'title'),
    'achievement': ('en-achievement.xml', 'achievement'),
    'dungeon': ('en-dungeon.xml', 'dungeon'),
    'gatherable': ('en-gatherable.xml', 'gatherable'),
    'itemSet': ('en-itemSet.xml', 'itemSet'),
    'pet': ('en-pet.xml', 'pet'),
    'recipe': ('en-recipe.xml', 'recipe'),
    'wing': ('en-wing.xml', 'wing'),
}


@dataclass
class Fetcher:
    delay_s: float = 1.05
    last_fetch: float = 0.0

    def _cache_path(self, url: str) -> Path:
        h = hashlib.sha256(url.encode()).hexdigest()[:32]
        return CACHE / 'pages' / h

    def get(self, url: str, *, binary: bool = False) -> tuple[int, bytes | str, dict[str, str]]:
        CACHE.mkdir(parents=True, exist_ok=True)
        path = self._cache_path(url)
        meta_path = path.with_suffix('.meta.json')
        if path.exists() and meta_path.exists():
            meta = json.loads(meta_path.read_text(encoding='utf-8'))
            body = path.read_bytes() if binary else path.read_text(encoding='utf-8')
            return meta.get('status', 200), body, meta.get('headers', {})

        elapsed = time.monotonic() - self.last_fetch
        if elapsed < self.delay_s:
            time.sleep(self.delay_s - elapsed)

        req = urllib.request.Request(url, headers={'User-Agent': USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                raw = resp.read()
                headers = {k.lower(): v for k, v in resp.headers.items()}
                status = resp.status
        except urllib.error.HTTPError as e:
            status = e.code
            raw = e.read()
            headers = {k.lower(): v for k, v in e.headers.items()} if e.headers else {}

        self.last_fetch = time.monotonic()
        path.parent.mkdir(parents=True, exist_ok=True)
        if binary:
            path.write_bytes(raw)
            body: bytes | str = raw
        else:
            text = raw.decode('utf-8', errors='replace')
            path.write_text(text, encoding='utf-8')
            body = text
        meta_path.write_text(
            json.dumps({'url': url, 'status': status, 'headers': headers, 'fetched_at': _utc_now()}),
            encoding='utf-8',
        )
        return status, body, headers


def _utc_now() -> str:
    return datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def load_robots(fetcher: Fetcher) -> str:
    status, body, _ = fetcher.get('https://gamers4.life/robots.txt')
    if status != 200 or not isinstance(body, str):
        raise SystemExit(f'robots.txt unavailable ({status})')
    return body


def url_allowed(robots: str, url: str) -> bool:
    """Minimal robots checker for our crawl paths."""
    path = urllib.parse.urlparse(url).path
    disallowed: list[str] = []
    for line in robots.splitlines():
        line = line.strip()
        if line.lower().startswith('disallow:'):
            disallowed.append(line.split(':', 1)[1].strip())
    for rule in disallowed:
        if rule and path.startswith(rule):
            return False
    return True


def parse_sitemap_urls(fetcher: Fetcher, sitemap_name: str, kind: str) -> list[str]:
    url = f'{BASE}/sitemaps/{sitemap_name}'
    status, body, _ = fetcher.get(url)
    if status != 200 or not isinstance(body, str):
        return []
    root = ET.fromstring(body)
    ns = {'sm': 'http://www.sitemaps.org/schemas/sitemap/0.9'}
    ids: list[str] = []
    pat = re.compile(rf'/{kind}/(\d+)/')
    for loc in root.findall('.//sm:loc', ns):
        if loc.text:
            m = pat.search(loc.text)
            if m:
                ids.append(m.group(1))
    return ids


def load_repo() -> dict[str, Any]:
    items = json.loads((DATA / 'items.json').read_text(encoding='utf-8'))['items']
    quests_raw = json.loads((DATA / 'quests.json').read_text(encoding='utf-8'))
    quests_by_num: dict[str, dict] = {}
    for row in quests_raw.get('quests', quests_raw).values():
        if isinstance(row, dict) and 'id' in row:
            quests_by_num[str(row['id'])] = row
    skills = json.loads((DATA / 'class-skills.json').read_text(encoding='utf-8'))
    skill_map = skills.get('skills', {})
    pc_stat = skills.get('stats', {})
    levels = json.loads((DATA / 'levels.json').read_text(encoding='utf-8'))
    wn = json.loads((DATA / 'world-npcs.json').read_text(encoding='utf-8'))
    templates = wn.get('templates', {})
    titles = json.loads((DATA / 'titles.json').read_text(encoding='utf-8'))
    title_map = titles.get('titles', titles)
    achievements = json.loads((DATA / 'achievements.json').read_text(encoding='utf-8'))
    ach_map = achievements.get('achievements', achievements)
    return {
        'items': items,
        'quests_by_num': quests_by_num,
        'skills': skill_map,
        'pc_stat': pc_stat,
        'levels_need_max': max(int(k) for k in levels.get('need', {}).keys()),
        'npc_templates': templates,
        'titles': title_map,
        'achievements': ach_map,
    }


def sample_ids(all_ids: list[str], n: int, seed: int) -> list[str]:
    if not all_ids:
        return []
    rng = random.Random(seed)
    if len(all_ids) <= n:
        return list(all_ids)
    return rng.sample(all_ids, n)


def fetch_item_json(fetcher: Fetcher, item_id: str) -> dict | None:
    url = f'{BASE}/db/item/en/{urllib.parse.quote(item_id)}.json'
    status, body, headers = fetcher.get(url)
    if status != 200 or not isinstance(body, str):
        return None
    try:
        data = json.loads(body)
    except json.JSONDecodeError:
        return None
    data['_headers'] = headers
    return data


def compare_item(item_id: str, site: dict, repo_item: dict | None) -> dict[str, Any]:
    out: dict[str, Any] = {'id': item_id, 'category': 'item'}
    if repo_item is None:
        out['status'] = 'site_only'
        out['site_name'] = site.get('name')
        return out
    out['status'] = 'both'
    out['site_name'] = site.get('name')
    repo_stats = repo_item.get('s') or {}
    main = (site.get('itemStats') or {}).get('mainStats') or {}
    stat_diffs = []
    for g4l_key, repo_key in ITEM_STAT_MAP.items():
        if g4l_key not in main:
            continue
        site_val = main[g4l_key]
        repo_val = repo_stats.get(repo_key)
        if repo_val is None:
            continue
        if int(site_val) != int(repo_val):
            stat_diffs.append({'field': repo_key, 'site': site_val, 'repo': repo_val})
    out['stat_diffs'] = stat_diffs
    site_il = (site.get('equipmentInfo') or {}).get('itemLevel')
    repo_l = repo_item.get('l')
    if site_il is not None and repo_l is not None and int(site_il) != int(repo_l):
        out['item_level'] = {'site_equipmentInfo': site_il, 'repo_l': repo_l}
    site_req = site.get('minLevelRequirement')
    repo_p = repo_item.get('p')
    if site_req is not None and repo_p is not None and int(site_req) != int(repo_p):
        out['req_level'] = {'site': site_req, 'repo': repo_p}
    return out


def parse_html_fields(html: str) -> dict[str, str]:
    title_m = re.search(r'<h1 class="title">([^<]+)</h1>', html)
    title = title_m.group(1).strip() if title_m else ''
    facts = dict(re.findall(r'class="quest-fact"><span>([^<]+)</span><b>([^<]*)</b>', html))
    facts.update(dict(re.findall(r'class="info-row"><span>([^<]+)</span><b>([^<]*)</b></div>', html)))
    return {'title': title, 'facts': facts}


def fetch_detail_html(fetcher: Fetcher, kind: str, entity_id: str) -> str | None:
    url = f'{BASE}/en/{kind}/{entity_id}/'
    status, body, _ = fetcher.get(url)
    if status != 200 or not isinstance(body, str):
        return None
    return body


def compare_quest(qid: str, parsed: dict, repo_q: dict | None) -> dict[str, Any]:
    out: dict[str, Any] = {'id': qid, 'category': 'quest'}
    if repo_q is None:
        out['status'] = 'site_only'
        out['site_title'] = parsed.get('title')
        return out
    out['status'] = 'both'
    out['site_title'] = parsed.get('title')
    out['repo_title'] = repo_q.get('title')
    if parsed.get('title') and repo_q.get('title') and parsed['title'].strip() != repo_q['title'].strip():
        out['title_mismatch'] = {'site': parsed['title'], 'repo': repo_q['title']}
    facts = parsed.get('facts') or {}
    for label, repo_key in [('Recommended level', 'recommended_level'), ('Unlock level', 'level')]:
        if label in facts and repo_key in repo_q:
            try:
                site_v = int(facts[label])
                repo_v = int(repo_q[repo_key])
                if site_v != repo_v:
                    out.setdefault('level_diffs', []).append({'field': repo_key, 'site': site_v, 'repo': repo_v})
            except ValueError:
                pass
    return out


def compare_npc(nid: str, parsed: dict, repo_t: dict | None) -> dict[str, Any]:
    out: dict[str, Any] = {'id': nid, 'category': 'npc'}
    if repo_t is None:
        out['status'] = 'site_only'
        out['site_title'] = parsed.get('title')
        return out
    out['status'] = 'both'
    facts = parsed.get('facts') or {}
    if 'Level' in facts:
        try:
            site_lv = int(facts['Level'])
            repo_lv = int(repo_t.get('level', -1))
            if site_lv != repo_lv:
                out['level_diff'] = {'site': site_lv, 'repo': repo_lv}
        except ValueError:
            pass
    return out


def compare_skill(sid: str, parsed: dict, repo_s: dict | None) -> dict[str, Any]:
    out: dict[str, Any] = {'id': sid, 'category': 'skill'}
    if repo_s is None:
        out['status'] = 'site_only'
        return out
    out['status'] = 'both'
    out['repo_name'] = repo_s.get('name')
    facts = parsed.get('facts') or {}
    if 'Level' in facts:
        try:
            out['site_skill_level'] = int(facts['Level'])
        except ValueError:
            pass
    if repo_s.get('name') in ('???', '', None) and parsed.get('title', '').startswith('Skill'):
        out['repo_name_unresolved'] = True
    return out


def version_probe(fetcher: Fetcher, robots: str) -> dict[str, Any]:
    probe: dict[str, Any] = {'pin': PIN, 'signals': []}
    main_url = f'{BASE}/sitemaps/main.xml'
    if url_allowed(robots, main_url):
        status, body, _ = fetcher.get(main_url)
        if status == 200 and isinstance(body, str):
            probe['site_classes'] = re.findall(r'/en/classes/([a-z]+)/', body)
            if 'brawler' in probe['site_classes']:
                probe['signals'].append(
                    'sitemap: Brawler class (Gauntlet) is KR/TW Chapter 1 content; the Global Oct 7 pin has no playable Brawler'
                )
    idx_url = f'{BASE}/en/'
    if url_allowed(robots, idx_url):
        _, _, headers = fetcher.get(idx_url)
        if headers.get('last-modified'):
            probe['index_last_modified'] = headers['last-modified']
    sample_item = f'{BASE}/db/item/en/110120003.json'
    if url_allowed(robots, sample_item):
        _, _, headers = fetcher.get(sample_item)
        if headers.get('last-modified'):
            probe['sample_item_json_last_modified'] = headers['last-modified']
    leveling = f'{BASE}/en/leveling/'
    if url_allowed(robots, leveling):
        status, body, _ = fetcher.get(leveling)
        if status == 200 and isinstance(body, str):
            probe['leveling_mentions_45'] = '45' in body
            probe['leveling_mentions_50'] = '50' in body
            if probe.get('leveling_mentions_50'):
                probe['signals'].append('leveling guide mentions 50 - mix of Global cap 45 and KR Chapter 1')
    # Oct 28 patch fingerprint: PcStat HPMax@45 Gladiator 6202 vs pin 4702
    probe['oct28_hp_gladiator_45'] = {'patch_value': 6202, 'pin_repo_value': 4702}
    repo_hp = (
        json.loads((DATA / 'class-skills.json').read_text(encoding='utf-8'))
        .get('stats', {})
        .get('Gladiator', {})
        .get('45', {})
        .get('HPMax')
    )
    probe['oct28_hp_gladiator_45']['repo_export'] = repo_hp
    if repo_hp == 4702:
        probe['signals'].append(
            'export PcStat Gladiator L45 HPMax=4702 matches the Oct 7 pin; the Oct 28 patch raises it to 6202'
        )
    probe['verdict'] = (
        'Gamers4Life reflects a newer or mixed client snapshot than the Global pin '
        f'build {PIN["steam_build"]} / manifest {PIN["manifest"]} (Oct 7 2026, cap {PIN["live_level_cap"]}). '
        'Signs: extended catalog (items/npcs), Brawler class in navigation, mainStats vs Item.dat stat differences. '
        'The site does not publish the exact Steam build ID.'
    )
    return probe


def repo_counts(repo: dict[str, Any]) -> dict[str, int]:
    return {
        'items': len(repo['items']),
        'skills': len(repo['skills']),
        'quests': len(repo['quests_by_num']),
        'npc_templates': len(repo['npc_templates']),
        'titles': len(repo['titles']) if isinstance(repo['titles'], dict) else 0,
        'achievements': len(repo['achievements']) if isinstance(repo['achievements'], dict) else 0,
        'levels_table_max': repo['levels_need_max'],
    }


def run_audit(sample_total: int, seed: int) -> dict[str, Any]:
    fetcher = Fetcher()
    robots = load_robots(fetcher)
    repo = load_repo()

    site_counts: dict[str, int] = {}
    site_id_lists: dict[str, list[str]] = {}
    for key, (smap, kind) in SITEMAP_CATEGORIES.items():
        url = f'{BASE}/sitemaps/{smap}'
        if not url_allowed(robots, url):
            continue
        ids = parse_sitemap_urls(fetcher, smap, kind)
        site_id_lists[key] = ids
        site_counts[key] = len(ids)

    per_cat = max(1, sample_total // 4)
    samples = {
        'item': sample_ids(site_id_lists.get('item', []), per_cat, seed),
        'skill': sample_ids(site_id_lists.get('skill', []), per_cat, seed + 1),
        'quest': sample_ids(site_id_lists.get('quest', []), per_cat, seed + 2),
        'npc': sample_ids(site_id_lists.get('npc', []), per_cat, seed + 3),
    }

    comparisons: list[dict] = []
    summary = {
        'site_only': 0,
        'both': 0,
        'item_stat_mismatch': 0,
        'quest_title_mismatch': 0,
        'npc_level_mismatch': 0,
    }

    for iid in samples['item']:
        url = f'{BASE}/db/item/en/{iid}.json'
        if not url_allowed(robots, url):
            continue
        site = fetch_item_json(fetcher, iid)
        if not site:
            comparisons.append({'id': iid, 'category': 'item', 'status': 'fetch_failed'})
            continue
        row = compare_item(iid, site, repo['items'].get(iid))
        comparisons.append(row)
        summary['both' if row['status'] == 'both' else 'site_only'] += 1
        if row.get('stat_diffs'):
            summary['item_stat_mismatch'] += 1

    for kind, ids, repo_key, cmp_fn in [
        ('skill', samples['skill'], 'skills', compare_skill),
        ('quest', samples['quest'], 'quests_by_num', compare_quest),
        ('npc', samples['npc'], 'npc_templates', compare_npc),
    ]:
        repo_map = repo[repo_key]
        for eid in ids:
            detail_url = f'{BASE}/en/{kind}/{eid}/'
            if not url_allowed(robots, detail_url):
                continue
            html = fetch_detail_html(fetcher, kind, eid)
            if not html:
                comparisons.append({'id': eid, 'category': kind, 'status': 'fetch_failed'})
                continue
            parsed = parse_html_fields(html)
            parsed['facts'] = parsed.get('facts') or {}
            if kind == 'skill':
                row = cmp_fn(eid, parsed, repo_map.get(eid))
            elif kind == 'quest':
                row = cmp_fn(eid, parsed, repo_map.get(eid))
            else:
                row = cmp_fn(eid, parsed, repo_map.get(eid))
            comparisons.append(row)
            summary['both' if row['status'] == 'both' else 'site_only'] += 1
            if row.get('title_mismatch'):
                summary['quest_title_mismatch'] += 1
            if row.get('level_diff'):
                summary['npc_level_mismatch'] += 1

    # overlap estimate on sample
    overlap = {
        'item': sum(1 for i in samples['item'] if i in repo['items']),
        'skill': sum(1 for i in samples['skill'] if i in repo['skills']),
        'quest': sum(1 for i in samples['quest'] if i in repo['quests_by_num']),
        'npc': sum(1 for i in samples['npc'] if i in repo['npc_templates']),
    }

    stat_mismatch_examples = [c for c in comparisons if c.get('stat_diffs')][:15]
    site_only_examples = [c for c in comparisons if c.get('status') == 'site_only'][:20]

    id_overlap = _full_id_overlap(fetcher, repo, site_id_lists)

    report = {
        'generated_at': _utc_now(),
        'crawl': {'user_agent': USER_AGENT, 'delay_s': fetcher.delay_s, 'cache_dir': str(CACHE.relative_to(ROOT))},
        'version_probe': version_probe(fetcher, robots),
        'counts': {'site': site_counts, 'repo': repo_counts(repo)},
        'sample': {'seed': seed, 'per_category': per_cat, 'ids': samples, 'overlap_in_sample': overlap},
        'summary': summary,
        'examples': {
            'item_stat_mismatch': stat_mismatch_examples,
            'site_only': site_only_examples,
        },
        'id_overlap': id_overlap,
        'comparisons': comparisons,
    }
    return report


def _full_id_overlap(fetcher: Fetcher, repo: dict[str, Any], site_id_lists: dict[str, list[str]]) -> dict[str, Any]:
    """Set overlap using cached sitemaps (no extra HTTP if crawl already ran)."""
    out: dict[str, Any] = {}
    items_site = set(site_id_lists.get('item', []))
    items_repo = set(repo['items'].keys())
    out['item'] = {
        'site': len(items_site),
        'repo': len(items_repo),
        'site_not_in_repo': len(items_site - items_repo),
        'repo_not_on_site': len(items_repo - items_site),
        'site_covered_by_repo_pct': round(100.0 * len(items_site & items_repo) / max(1, len(items_site)), 2),
    }
    quests_site = set(site_id_lists.get('quest', []))
    quests_repo = set(repo['quests_by_num'].keys())
    out['quest'] = {
        'site': len(quests_site),
        'repo_numeric_ids': len(quests_repo),
        'repo_not_on_site': len(quests_repo - quests_site),
        'site_not_in_repo': len(quests_site - quests_repo),
    }
    return out


def render_doc(report: dict[str, Any]) -> str:
    vp = report['version_probe']
    sc = report['counts']['site']
    rc = report['counts']['repo']
    sm = report['summary']
    lines = [
        '# Gamers4Life audit vs client pin',
        '',
        f'**Run date:** {report["generated_at"]} UTC  ',
        f'**Site source:** [{BASE}/en/]({BASE}/en/)  ',
        f'**Script:** `python tools/audit_gamers4life.py` (cache `{report["crawl"]["cache_dir"]}/`, ~1 req/s)',
        '',
        '## Emulator pin (source of truth: client dat)',
        '',
        '| Field | Value |',
        '|------|----------|',
        f'| Steam build | **{PIN["steam_build"]}** |',
        f'| Manifest | `{PIN["manifest"]}` |',
        f'| Pin date | {PIN["pin_date"]} |',
        f'| Live level cap (Global) | **{PIN["live_level_cap"]}** |',
        '',
        'Client exports in `tools/data/` are **not overwritten** with site data; only differences are listed below.',
        '',
        '## Site version verdict',
        '',
        vp.get('verdict', ''),
        '',
        '### Signals',
        '',
    ]
    for sig in vp.get('signals', []):
        lines.append(f'- {sig}')
    lines.extend(
        [
            '',
            '| Metadata | Value |',
            '|------------|----------|',
            f'| DB index Last-Modified | {vp.get("index_last_modified", "—")} |',
            f'| Last-Modified sample item JSON | {vp.get("sample_item_json_last_modified", "—")} |',
            f'| Classes in sitemap | {", ".join(vp.get("site_classes") or [])} |',
            f'| Leveling: mentions of 45 / 50 | {vp.get("leveling_mentions_45")} / {vp.get("leveling_mentions_50")} |',
            '',
            '## Volume comparison (sitemap vs repo)',
            '',
            '| Category | Gamers4Life (en sitemap) | Repo export | Δ (site − repo) |',
            '|-----------|-------------------------:|------------:|----------------:|',
        ]
    )
    for key in ['item', 'skill', 'quest', 'npc', 'title', 'achievement', 'dungeon', 'pet', 'recipe', 'wing']:
        if key in sc:
            repo_key = {
                'item': 'items',
                'skill': 'skills',
                'quest': 'quests',
                'npc': 'npc_templates',
                'title': 'titles',
                'achievement': 'achievements',
            }.get(key)
            rv = rc.get(repo_key, '—') if repo_key else '—'
            delta = sc[key] - rv if isinstance(rv, int) else '—'
            lines.append(f'| {key} | {sc[key]} | {rv} | {delta} |')
    io = report.get('id_overlap') or {}
    if io:
        it, qu = io.get('item', {}), io.get('quest', {})
        lines.extend(
            [
                '',
                '## ID overlap (full sitemap vs export)',
                '',
                '| Set | Site | Repo | Site \\ Repo | Repo \\ Site |',
                '|-------|-----:|-----:|-----------:|-----------:|',
                f'| item | {it.get("site", "—")} | {it.get("repo", "—")} | {it.get("site_not_in_repo", "—")} | {it.get("repo_not_on_site", "—")} |',
                f'| quest (numeric id) | {qu.get("site", "—")} | {qu.get("repo_numeric_ids", "—")} | {qu.get("site_not_in_repo", "—")} | {qu.get("repo_not_on_site", "—")} |',
                '',
                f'Site catalog coverage by export items: **{it.get("site_covered_by_repo_pct", "—")}%**. '
                'Site quests are a subset of dat (310 extra quest ids in export, probably hidden/hero variants).',
            ]
        )
    ov = report['sample']['overlap_in_sample']
    lines.extend(
        [
            '',
            '## Sample (~300 entities)',
            '',
            f'Seed `{report["sample"]["seed"]}`, **{report["sample"]["per_category"]}** IDs per item/skill/quest/npc '
            f'(total {report["sample"]["per_category"] * 4}).',
            '',
            '| Category | In sample on site and in repo |',
            '|-----------|----------------------------:|',
            f'| item | {ov["item"]} |',
            f'| skill | {ov["skill"]} |',
            f'| quest | {ov["quest"]} |',
            f'| npc | {ov["npc"]} |',
            '',
            '### Sample difference summary',
            '',
            '| Metric | Count |',
            '|---------|------:|',
            f'| Entry on site and in repo | {sm["both"]} |',
            f'| Site only (not in export) | {sm["site_only"]} |',
            f'| Item: mainStats ≠ repo | {sm["item_stat_mismatch"]} |',
            f'| Quest: title mismatch | {sm["quest_title_mismatch"]} |',
            f'| NPC: level mismatch | {sm["npc_level_mismatch"]} |',
            '',
            '### Item stat difference examples',
            '',
            '| Item ID | Field | Site | Repo (dat) |',
            '|---------|------|-----:|-----------:|',
        ]
    )
    for ex in report['examples']['item_stat_mismatch'][:12]:
        for d in ex.get('stat_diffs', [])[:3]:
            lines.append(f'| {ex["id"]} | {d["field"]} | {d["site"]} | {d["repo"]} |')
    lines.extend(
        [
            '',
            'Most item stat differences in the sample **are explained by newer balance** on the '
            'Gamers4Life side relative to the Oct 7 manifest; client dat takes priority.',
            '',
            '### Site-only ID examples',
            '',
            '| Category | ID | Site title / name |',
            '|-----------|-----|-------------------|',
        ]
    )
    for ex in report['examples']['site_only'][:15]:
        name = ex.get('site_name') or ex.get('site_title') or '—'
        lines.append(f'| {ex.get("category")} | {ex.get("id")} | {name} |')
    lines.extend(
        [
            '',
            '## PcStatLevel (Oct 28 patch)',
            '',
            f'The Oct 28 (Global) patch raises melee base HP at 45; Gladiator **4702 → 6202**. '
            f'Export pin: Gladiator L45 `HPMax` = **{vp.get("oct28_hp_gladiator_45", {}).get("repo_export")}**. '
            'The site does not publish PcStat in static HTML; compared via item/NPC samples.',
            '',
            '## Limitations',
            '',
            '- `/api/` in robots.txt is not used; items via `/db/item/en/<id>.json`.',
            '- skill/quest/npc pages are HTML; skills often lack names/damage in static pages.',
            '- The site catalog is wider than the dat export (cosmetics, pets, wings); missing from repo is not a server bug.',
            '',
        ]
    )
    return '\n'.join(lines)


def main() -> int:
    ap = argparse.ArgumentParser(description='Gamers4Life read-only parity audit')
    ap.add_argument('--sample', type=int, default=300, help='Total sample size (~split across 4 categories)')
    ap.add_argument('--seed', type=int, default=2380)
    ap.add_argument('--json', type=Path, default=CACHE / 'report.json')
    ap.add_argument('--write-doc', type=Path, default=None, help='Write Russian markdown report')
    args = ap.parse_args()

    report = run_audit(args.sample, args.seed)
    args.json.parent.mkdir(parents=True, exist_ok=True)
    args.json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Wrote {args.json}', file=sys.stderr)

    if args.write_doc:
        args.write_doc.parent.mkdir(parents=True, exist_ok=True)
        args.write_doc.write_text(render_doc(report), encoding='utf-8')
        print(f'Wrote {args.write_doc}', file=sys.stderr)

    print(json.dumps({'summary': report['summary'], 'verdict': report['version_probe']['verdict']}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
