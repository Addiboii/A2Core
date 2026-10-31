"""One-off helper: fill crafts.json conversion_recipes from aion2.gg HTML (sanity / gap-fill when table-017 is absent).

Re-run after client export: `python tools/craft_data.py` should replace this block from ConversionRecipe.dat.
"""
from __future__ import annotations

import json
import re
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
CRAFTS = ROOT / 'tools/data/crafts.json'
UA = {'User-Agent': 'Mozilla/5.0 (compatible; A2CoreDataExport/1.0)'}


def _fetch(recipe_id: int) -> str:
    url = f'https://aion2.gg/database/recipes/conversions/x-{recipe_id}'
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read().decode('utf-8', 'replace')


def _item_ids(html: str) -> list[tuple[int, int]]:
    """Primary material row: first slot + optional alternates (OR)."""
    block = re.search(r'craft-module__VsEHQa__matList(.*?</ul>)', html, re.S)
    if not block:
        return []
    chunk = block.group(1)
    primary = re.search(r'/database/items/[^"]+-(\d+)"[^>]*>[^<]+</a></span><span class="craft-module__VsEHQa__matCount">×(\d+)', chunk)
    if not primary:
        return []
    rows = [(int(primary.group(1)), int(primary.group(2)))]
    for alt in re.finditer(r'craft-module__VsEHQa__alt.*?/database/items/[^"]+-(\d+)".*?×(\d+)', chunk, re.S):
        item, count = int(alt.group(1)), int(alt.group(2))
        if (item, count) not in rows:
            rows.append((item, count))
    return rows


def _extra_components(html: str) -> list[list[int]]:
    """Additional required rows after the first (AND)."""
    block = re.search(r'craft-module__VsEHQa__matList(.*?</ul>)', html, re.S)
    if not block:
        return []
    chunks = re.findall(r'<li class="craft-module__VsEHQa__mat"(.*?)</li>', block.group(1), re.S)
    extras = []
    for row in chunks[1:]:
        item = re.search(r'/database/items/[^"]+-(\d+)"', row)
        count = re.search(r'matCount">×(\d+)', row)
        if item and count:
            extras.append([int(item.group(1)), int(count.group(1))])
    return extras


def parse(html: str, recipe_id: int) -> list | None:
    if 'Substance Morph' not in html and 'craft-module__VsEHQa__body' not in html:
        return None
    race = 0
    has_light = 'a2-tag--elyos' in html
    has_dark = 'a2-tag--asmod' in html or 'a2-tag--asmo' in html
    if has_light and not has_dark:
        race = 1
    elif has_dark and not has_light:
        race = 2

    count_m = re.search(r'craft-module__VsEHQa__count[^>]*>.*?×<!-- -->([0-9,]+)', html)
    if not count_m:
        count_m = re.search(r'craft-module__VsEHQa__count">×([0-9,]+)', html)
    product_count = int(count_m.group(1).replace(',', '')) if count_m else 1

    product_m = re.search(
        r'href="/database/items/[^"]+-(\d+)"[^>]*class="a2-btn a2-btn--secondary"', html)
    if not product_m:
        product_m = re.search(r'craft-module__VsEHQa__icon"[^>]*alt="[^"]*".*?href="/database/items/[^"]+-(\d+)"', html, re.S)
    if not product_m:
        return None
    product = int(product_m.group(1))

    or_row = _item_ids(html)
    if not or_row:
        return None
    components = _extra_components(html)
    alternates = or_row if len(or_row) > 1 else []

    fee = 0
    for row in re.findall(r'Kina[^0-9]*×([0-9,]+)', html):
        fee = max(fee, int(row.replace(',', '')))

    rate = 100_000_000
    if 'Success Guaranteed' not in html:
        pct = re.search(r'craft-module__VsEHQa__chanceValue">([0-9.]+)\s*%', html)
        if pct:
            rate = int(float(pct.group(1)) * 1_000_000)

    limit_target = limit_period = limit_count = 0
    if re.search(r'Per Server', html):
        limit_target = 3
        lim = re.search(r'Morph limit[^0-9]*(\d+)', html)
        limit_count = int(lim.group(1)) if lim else 0
        limit_period = 2 if 'Weekly' in html or 'weekly' in html else 0
    elif re.search(r'Per Character', html):
        limit_target = 1
        lim = re.search(r'Morph limit[^0-9]*(\d+)', html)
        limit_count = int(lim.group(1)) if lim else 0
        limit_period = 2 if 'Weekly' in html else (1 if 'Daily' in html else 0)
    elif re.search(r'Per Account', html) or re.search(r'per account', html, re.I):
        limit_target = 2
        lim = re.search(r'Morph limit[^0-9]*(\d+)', html)
        limit_count = int(lim.group(1)) if lim else 0
        limit_period = 2 if 'Weekly' in html else 0

    # [race, product, count, components, alternates, fee, rate, limit_target, limit_period, limit_count]
    return [race, product, product_count, components, alternates, fee, rate, limit_target, limit_period, limit_count]


def scrape_ids(ids: list[int], workers: int = 12) -> dict[str, list]:
    out: dict[str, list] = {}
    errors: list[int] = []

    def one(rid: int):
        try:
            html = _fetch(rid)
            row = parse(html, rid)
            return rid, row
        except Exception:
            return rid, None

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(one, i): i for i in ids}
        for fut in as_completed(futures):
            rid, row = fut.result()
            if row is None:
                errors.append(rid)
            else:
                out[str(rid)] = row
    if errors:
        print(f'warn: {len(errors)} recipes failed: {errors[:15]}{"..." if len(errors) > 15 else ""}')
    return out


def main():
    data = json.loads(CRAFTS.read_text(encoding='utf-8'))
    ids = sorted(set(data['conversions']['1'] + data['conversions']['2']))
    recipes = scrape_ids(ids)
    data['conversion_recipes'] = recipes
    data['conversion_recipe_sources'] = {rid: 'aion2.gg-scrape' for rid in recipes}
    from craft_data import MISSING_CONVERSION_SCRAPE
    data['conversion_recipe_missing'] = [rid for rid in MISSING_CONVERSION_SCRAPE if rid not in recipes]
    note = (
        'conversion_recipes: scraped from aion2.gg HTML for IDs in conversions[]; '
        'replace via ConversionRecipe.dat (tools/craft_data.py) when research/local tables exist'
    )
    data['source'] = data.get('source', '') + '; ' + note
    write_data_json(CRAFTS, data)
    print(f'wrote {len(recipes)}/{len(ids)} conversion_recipes -> {CRAFTS.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
