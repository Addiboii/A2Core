"""Find exact shared float triplets across decoded server-message families.

This is a bounded pattern search, not a packet schema or a coordinate decoder.
Opaque message bodies and candidate reference bytes are omitted from its report.
"""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import struct

try:
    from .aion_protocol import lz4_block, split_frames, state_message_fields
    from .inspect_capture import inspect
except ImportError:
    from aion_protocol import lz4_block, split_frames, state_message_fields
    from inspect_capture import inspect

BODY_BUDGET = 2 * 1024 * 1024
WINDOW_BUDGET = 100000


def shared_float_triplets(messages, body_budget=BODY_BUDGET):
    if sum(len(message["body"]) for message in messages) > body_budget:
        raise ValueError("State search exceeds body-byte budget")
    windows = defaultdict(list)
    candidates = 0
    for message in messages:
        body = message["body"]
        for offset in range(2, len(body) - 11):
            encoded = body[offset:offset + 12]
            values = struct.unpack("<3f", encoded)
            if not all(math.isfinite(value) and 1 < abs(value) < 10000000 for value in values):
                continue
            candidates += 1
            if candidates > WINDOW_BUDGET:
                raise ValueError("State search exceeds candidate-window budget")
            windows[encoded].append({key: value for key, value in message.items() if key != "body"} | {"body_offset": offset})
    result = []
    for encoded, occurrences in windows.items():
        distinct_tags = {item["tag_candidate"] for item in occurrences}
        if len(distinct_tags) >= 3:
            result.append({"float_triplet_candidate": list(struct.unpack("<3f", encoded)),
                           "distinct_tags": len(distinct_tags), "occurrences": occurrences})
    return result


def expand_messages(prefix, metadata):
    frames, consumed = split_frames(prefix)
    if consumed != len(prefix) or len(frames) != len(metadata):
        raise ValueError("State analysis requires complete matching frame metadata")
    messages = []
    total = 0
    for index, (outer, record) in enumerate(zip(frames, metadata)):
        compressed = outer.body[:2] == b"\xff\xff"
        if compressed:
            if "compressed_envelope" not in record:
                raise ValueError("Unvalidated compression in state-analysis input")
            expected = int.from_bytes(outer.body[2:6], "little")
            if expected > BODY_BUDGET - total:
                raise ValueError("State analysis exceeds expansion budget")
            data = lz4_block(outer.body[6:], expected)
            inner, used = split_frames(data)
            if used != len(data):
                raise ValueError("Incomplete frames in compressed state data")
        else:
            inner = [outer]
        for inner_index, frame in enumerate(inner):
            total += len(frame.body)
            if total > BODY_BUDGET:
                raise ValueError("State analysis exceeds body-byte budget")
            messages.append({"outer_frame_index": index, "inner_frame_index": inner_index if compressed else None,
                             "tag_candidate": f"0x{int.from_bytes(frame.body[:2], 'little'):04x}",
                             "body_bytes": len(frame.body), "body": frame.body,
                             "available_in_capture_utc": record["available_in_capture_utc"]})
    return messages


def candidate_reference_links(messages):
    references = Counter()
    for message in messages:
        if message["tag_candidate"] == "0x3634":
            state_message_fields(message["body"])  # Require the observed layout.
            references[message["body"][2:10]] += 1
    hits = []
    if len(references) * len(messages) > 1000000:
        raise ValueError("Reference search exceeds comparison budget")
    for reference in references:
        for message in messages:
            if message["tag_candidate"] == "0x3634":
                continue
            offset = message["body"].find(reference, 2)
            if offset >= 0:
                hits.append({"reference_sha256": hashlib.sha256(reference).hexdigest(),
                             "tag_candidate": message["tag_candidate"], "body_offset": offset,
                             "available_in_capture_utc": message["available_in_capture_utc"]})
    return {"reference_records": sum(references.values()), "distinct_references": len(references),
            "matches_in_other_tags": hits,
            "note": "Eight bytes after 0x3634 are only a candidate reference; they may contain several fields."}


def analyze(path):
    results = []

    def observe(flow, direction, prefix):
        if direction != "server_to_client":
            return
        stream = flow["directions"][direction]
        item = {"server_address": flow["server_address"], "server_port": flow["server_port"],
                "client_port": flow["client_port"], "client_syn_observed": flow["client_syn_observed"]}
        results.append(item)
        try:
            if stream["conflicting_bytes"] or stream["gap_after_prefix"] or stream["retention_limit_reached"] or stream.get("prefix_limit_reached"):
                raise ValueError("Incomplete/conflicting stream is outside the state-search scope")
            application = stream.get("application_framing")
            if not application:
                raise ValueError("No complete observed frame sequence")
            messages = expand_messages(prefix, application["frames"])
            item.update({"expanded_messages": len(messages), "shared_float_triplets": shared_float_triplets(messages),
                         "candidate_reference_links": candidate_reference_links(messages)})
        except ValueError as error:
            item["analysis_error"] = str(error)

    inspect(path, 13328, stream_observer=observe)
    return {"schema_version": 1, "source": str(path), "flows": results,
            "search_filter": "Three finite float32 little-endian values, each with 1 < absolute value < 1e7; exact bytes shared across at least three tags.",
            "limitations": ["A shared numeric pattern does not prove position, identity, units, or message names.",
                            "The filter intentionally misses zero/small/out-of-range coordinates and some valid vectors.",
                            "One compression layer is expanded; timestamps measure captured byte availability."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = analyze(args.input)
    rendered = json.dumps(report, indent=2) + "\n"
    if args.output:
        if args.output.resolve() == args.input.resolve():
            parser.error("Output must differ from input")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered)


if __name__ == "__main__":
    main()
