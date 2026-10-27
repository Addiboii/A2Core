"""Summarize a completed AION2 observer JSONL report without network queries."""

import argparse
import ipaddress
import json
from pathlib import Path


def summarize(path):
    records = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    sessions = [row for row in records if row.get("kind") == "session"]
    samples = [row for row in records if row.get("kind") == "sample"]
    if len(sessions) != 1 or not samples:
        raise ValueError("Expected one session header and at least one sample")
    processes, endpoints = {}, {}
    for sample in samples:
        timestamp = sample["collected_utc"]
        for process in sample["processes"]:
            key = (process["pid"], process.get("started_utc"))
            if key not in processes:
                processes[key] = {"pid": key[0], "started_utc": key[1], "name": process["name"],
                                  "first_seen_utc": timestamp, "match_bases": set(), "installed_modules": set()}
            current = processes[key]
            current["last_seen_utc"] = timestamp
            current["match_bases"].add(process.get("match_basis", "installation_path"))
            current["installed_modules"].update(process.get("installed_modules", []))
        observed = set()
        for connection in sample["connections"]:
            key = tuple(connection.get(field) for field in ("protocol", "pid", "local_endpoint", "remote_endpoint", "state"))
            if key in observed:
                continue
            observed.add(key)
            if key not in endpoints:
                endpoints[key] = {**connection, "first_seen_utc": timestamp, "sample_observations": 0}
            endpoints[key]["last_seen_utc"] = timestamp
            endpoints[key]["sample_observations"] += 1
    for process in processes.values():
        process["match_bases"] = sorted(process["match_bases"])
        process["installed_modules"] = sorted(process["installed_modules"])
    peers = set()
    for endpoint in endpoints.values():
        remote = endpoint.get("remote_endpoint")
        if endpoint["protocol"] != "TCP" or not remote:
            continue
        address, _ = remote.rsplit(":", 1)
        parsed = ipaddress.ip_address(address.strip("[]"))
        if not parsed.is_loopback and not parsed.is_unspecified:
            peers.add(remote)
    return {"schema_version": 1, "source": str(path), "samples": len(samples),
            "complete": records[-1].get("kind") == "summary",
            "first_sample_utc": samples[0]["collected_utc"], "last_sample_utc": samples[-1]["collected_utc"],
            "processes": list(processes.values()), "endpoint_states": list(endpoints.values()),
            "non_loopback_tcp_peers": sorted(peers),
            "issues": sorted({issue for sample in samples for issue in sample.get("issues", [])}),
            "limitations": sessions[0].get("limitations", [])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = summarize(args.input)
    rendered = json.dumps(result, indent=2) + "\n"
    if args.output:
        if args.output.resolve() == args.input.resolve():
            parser.error("Output must differ from input")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered)


if __name__ == "__main__":
    main()
