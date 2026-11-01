"""Pretty-printed JSON for committed tools/data/*.json (2-space indent, trailing newline)."""
import json
from pathlib import Path


def write_data_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
