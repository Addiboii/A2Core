"""Correlate decoded login/game reports without reading or emitting opaque bodies."""

import argparse
from collections import Counter
from datetime import datetime
import json
from pathlib import Path
from statistics import median


def seconds(value):
    return datetime.fromisoformat(value).timestamp()


def statistics(values):
    if not values:
        return {"count": 0}
    return {"count": len(values), "min": min(values), "median": median(values), "max": max(values)}


def game_summary(flow):
    application = flow["directions"].get("server_to_client", {}).get("application_framing", {})
    frames = application.get("frames", [])
    events, clocks, entities, state_prefixes = [], [], [], []
    tags = Counter()
    for frame in frames:
        envelope = frame.get("compressed_envelope")
        if envelope:
            tags.update(envelope["inner_header_u16_le_counts"])
            state_items = envelope["inner_frames"]
            events.append({"available_in_capture_utc": frame["available_in_capture_utc"],
                           "seconds_after_syn": seconds(frame["available_in_capture_utc"]) - seconds(flow["first_packet_utc"]) if flow["client_syn_observed"] else None,
                           "wire_bytes": frame["wire_length"],
                           "uncompressed_bytes": envelope["uncompressed_bytes"],
                           "inner_frame_count": envelope["inner_frame_count"],
                           "inner_tags": [f["header_u16_le"] for f in envelope["inner_frames"]]})
        else:
            tags[frame["header_u16_le"]] += 1
            state_items = [frame]
        for item in state_items:
            decoded = item.get("decoded_fields", {})
            if "position_xyz_candidate" in decoded:
                entities.append({**decoded, "available_in_capture_utc": frame["available_in_capture_utc"]})
            if "primary_unix_ms_candidate" in decoded:
                state_prefixes.append({**decoded, "available_in_capture_utc": frame["available_in_capture_utc"],
                                       "capture_minus_primary_ms": seconds(frame["available_in_capture_utc"]) * 1000 - decoded["primary_unix_ms_candidate"]})
        fields = frame.get("decoded_fields", {})
        if frame["header_u16_le"] == "0x3600" and "unix_time_ms_candidate" in fields:
            clocks.append((fields["unix_time_ms_candidate"], seconds(frame["available_in_capture_utc"]) * 1000))
    return {"server_address": flow["server_address"], "server_port": flow["server_port"],
            "first_packet_utc": flow["first_packet_utc"], "last_packet_utc": flow["last_packet_utc"],
            "client_syn_observed": flow["client_syn_observed"],
            "outer_server_frames": len(frames), "expanded_server_frames": sum(tags.values()),
            "expanded_server_tag_counts": dict(sorted(tags.items())), "compressed_events": events,
            "early_state_prefixes": state_prefixes,
            "entity_position_candidates": {
                "records": len(entities),
                "distinct_reference_hashes": len({item["entity_reference_sha256"] for item in entities}),
                "axis_ranges": [[min(item["position_xyz_candidate"][axis] for item in entities),
                                 max(item["position_xyz_candidate"][axis] for item in entities)] for axis in range(3)] if entities else [],
                "first_available_utc": entities[0]["available_in_capture_utc"] if entities else None,
                "last_available_utc": entities[-1]["available_in_capture_utc"] if entities else None,
                "note": "Repeated numeric layout only; entity types, coordinate units, and message semantics remain unverified."},
            "clock_0x3600": {
                "server_clock_step_ms": statistics([b[0] - a[0] for a, b in zip(clocks, clocks[1:])]),
                "capture_minus_server_clock_ms": statistics([capture - server for server, capture in clocks]),
                "note": "Outer notifications only. Clock difference includes clock skew and capture/network delay; it is not an RTT measurement."}}


def summarize(login, game):
    handoffs = []
    for flow in login["flows"]:
        for frame in flow["directions"].get("server_to_client", {}).get("application_framing", {}).get("frames", []):
            if "decoded_handoff" not in frame:
                continue
            destination = frame["decoded_handoff"]
            announced = frame["available_in_capture_utc"]
            candidates = [f for f in game["flows"] if f["client_syn_observed"]
                          and f["server_address"] == destination["destination_address"]
                          and f["server_port"] == destination["destination_port"]
                          and f["client_address"] == flow["client_address"]
                          and seconds(f["first_packet_utc"]) >= seconds(announced)]
            next_flow = min(candidates, key=lambda f: seconds(f["first_packet_utc"])) if candidates else None
            handoffs.append({**destination, "announced_utc": announced,
                             "next_matching_syn_utc": next_flow["first_packet_utc"] if next_flow else None,
                             "announcement_to_syn_ms": (seconds(next_flow["first_packet_utc"]) - seconds(announced)) * 1000 if next_flow else None})
    return {"schema_version": 1, "handoffs": handoffs,
            "game_connections": [game_summary(flow) for flow in game["flows"]],
            "limitations": ["Matches use endpoint and timestamp evidence, not decrypted session credentials.",
                            "Frame availability is reconstructed from packet timestamps, not measured inside the application.",
                            "Compression is decoded; payload field schemas and most numeric tag meanings remain unresolved."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Contains report-13700.json and report-13328.json")
    args = parser.parse_args()
    login, game = [json.loads((args.directory / f"report-{port}.json").read_text(encoding="utf-8")) for port in (13700, 13328)]
    result = summarize(login, game)
    output = args.directory / "session-analysis.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
