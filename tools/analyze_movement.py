"""Correlate manual movement cues with observed frame sizes, without decrypting bodies."""

import argparse
from collections import Counter
from datetime import datetime
import json
import math
from pathlib import Path
from statistics import median
import struct

try:
    from .inspect_capture import inspect
    from .aion_protocol import split_frames
except ImportError:
    from inspect_capture import inspect
    from aion_protocol import split_frames


def timestamp(value):
    return datetime.fromisoformat(value).timestamp()


def analyze_frames(client, server, cues):
    if not cues:
        raise ValueError("Movement analysis requires cue records")
    cue_times = [timestamp(cue["cue_utc"]) for cue in cues]
    if cue_times != sorted(cue_times):
        raise ValueError("Cue records are not chronological")
    movement_cues = [cue for cue in cues if cue["action"] in ("walk_forward", "walk_backward")]
    updates = [frame for frame in client if frame["wire_length"] == 31]
    stops = [frame for frame in client if frame["wire_length"] == 29]
    groups = []
    for frame in updates:
        if not groups or timestamp(frame["available_in_capture_utc"]) - timestamp(groups[-1][-1]["available_in_capture_utc"]) > 0.5:
            groups.append([])
        groups[-1].append(frame)
    bursts = []
    for group in groups:
        first, last = [timestamp(frame["available_in_capture_utc"]) for frame in (group[0], group[-1])]
        candidates = [cue for cue in movement_cues if 0 <= first - timestamp(cue["cue_utc"]) <= 10]
        cue = max(candidates, key=lambda c: timestamp(c["cue_utc"])) if candidates else None
        stop_candidates = [frame for frame in stops if 0 <= timestamp(frame["available_in_capture_utc"]) - last <= 1]
        stop = min(stop_candidates, key=lambda f: timestamp(f["available_in_capture_utc"])) if stop_candidates else None
        intervals = [(timestamp(b["available_in_capture_utc"]) - timestamp(a["available_in_capture_utc"])) * 1000
                     for a, b in zip(group, group[1:])]
        positive = [value for value in intervals if value > 0]
        bursts.append({"movement_cue_candidate": cue["action"] if cue else None,
                       "cue_utc": cue["cue_utc"] if cue else None, "update_frames": len(group),
                       "first_update_utc": group[0]["available_in_capture_utc"],
                       "last_update_utc": group[-1]["available_in_capture_utc"],
                       "cue_to_first_update_ms": (first - timestamp(cue["cue_utc"])) * 1000 if cue else None,
                       "update_span_ms": (last - first) * 1000,
                       "median_positive_update_interval_ms": median(positive) if positive else None,
                       "same_capture_timestamp_intervals": len(intervals) - len(positive),
                       "following_29_byte_frame_utc": stop["available_in_capture_utc"] if stop else None,
                       "last_update_to_29_byte_frame_ms": (timestamp(stop["available_in_capture_utc"]) - last) * 1000 if stop else None})
    windows = []
    for index, cue in enumerate(cues):
        begin = timestamp(cue["cue_utc"])
        end = cue_times[index + 1] if index + 1 < len(cues) else float("inf")
        counts = Counter(frame["wire_length"] for frame in client if begin <= timestamp(frame["available_in_capture_utc"]) < end)
        windows.append({"cue": cue["action"], "begin_utc": cue["cue_utc"],
                        "next_cue_utc": cues[index + 1]["cue_utc"] if index + 1 < len(cues) else None,
                        "client_wire_length_counts": dict(sorted(counts.items()))})
    return {"client_frames": len(client), "server_frames": len(server), "bursts": bursts,
            "client_wire_length_counts": dict(sorted(Counter(frame["wire_length"] for frame in client).items())),
            "server_tag_counts": dict(sorted(Counter(frame["header_u16_le"] for frame in server).items())),
            "cue_windows": windows,
            "interpretation": "31-byte movement-update and 29-byte stop messages are candidates based on timing/size correlation; client tags and fields are not decoded."}


def body_structure(frames):
    bodies = [frame.body for frame in frames if frame.wire_length == 31]
    if not bodies:
        return {"samples": 0}
    offsets = []
    for offset in range(2, 19):
        triples = [struct.unpack_from("<3f", body, offset) for body in bodies]
        if all(all(math.isfinite(value) and 1 < abs(value) < 10000000 for value in values) for values in triples):
            offsets.append(offset)
    return {"samples": len(bodies), "distinct_two_byte_headers": len({body[:2] for body in bodies}),
            "per_byte_distinct_values": [len({body[index] for body in bodies}) for index in range(30)],
            "consistent_float_triplet_offsets": offsets,
            "note": "Variability and failure of this float heuristic do not establish a cipher or rule out other field encodings."}


def analyze(path, cues):
    structures = {}

    def observe(flow, direction, prefix):
        if direction == "client_to_server" and "application_framing" in flow["directions"][direction]:
            key = (flow["client_port"], flow["server_address"], flow["first_packet_utc"])
            structures[key] = body_structure(split_frames(prefix)[0])

    report = inspect(path, 13328, stream_observer=observe)
    results = []
    for flow in report["flows"]:
        streams = flow["directions"]
        result = {"server_address": flow["server_address"], "client_port": flow["client_port"],
                  "client_syn_observed": flow["client_syn_observed"]}
        results.append(result)
        if any("application_framing" not in streams.get(direction, {}) for direction in ("client_to_server", "server_to_client")):
            result["analysis_error"] = "Both directions require complete observed framing"
            continue
        if any(stream["conflicting_bytes"] or stream["gap_after_prefix"] or stream["retention_limit_reached"] or stream.get("prefix_limit_reached") for stream in streams.values()):
            result["analysis_error"] = "Incomplete/conflicting stream is outside movement-analysis scope"
            continue
        result.update(analyze_frames(streams["client_to_server"]["application_framing"]["frames"],
                                     streams["server_to_client"]["application_framing"]["frames"], cues))
        result["update_body_structure"] = structures[(flow["client_port"], flow["server_address"], flow["first_packet_utc"])]
    return {"schema_version": 1, "source": str(path), "packet_count": report["packet_count"], "flows": results,
            "limitations": ["Cue timestamps are instructions, not measured keypresses. Human reaction and capture buffering affect timing.",
                            "No SYN was expected for a capture begun inside the game; record boundaries remain an inference from complete framing.",
                            "The size/burst heuristic can match unrelated messages; actual visible actions need separate confirmation.",
                            "Counts describe outer frames; compressed server messages are not expanded by this timing summary."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    capture = args.directory / "capture.pcapng"
    if not capture.exists():
        capture = args.directory / "analysis.pcapng"
    cues = [json.loads(line) for line in (args.directory / "actions.jsonl").read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    result = analyze(capture, cues)
    (args.directory / "movement-analysis.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
