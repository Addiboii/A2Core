#!/usr/bin/env python3
"""Broker fee and duration defaults for build pin 25767555 (Oct 7 2026)."""
from __future__ import annotations

from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parent
DATA = ROOT / "data" / "trade.json"


def build() -> dict:
    return {
        "pin": {"steam_build": 25767555, "manifest": "1699273097574069541", "date": "2026-10-07"},
        "default_trade_server_id": 83,
        "listing_duration_ms": 7 * 24 * 60 * 60 * 1000,
        "listing_fee_rate_bp": 500,
        "listing_fee_min_kina": 10,
        "sale_tax_rate_bp": 500,
        "max_active_listings_per_character": 20,
        "player_mail_fee_kina": 100,
        "player_mail_expire_days": 30,
        "cod_post_data_id": 107,
        "staff_post_data_id": 106,
    }


if __name__ == "__main__":
    write_data_json(DATA, build())
