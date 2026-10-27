"""Prepare (but never launch) a network-disabled Windows Sandbox client test."""
import argparse
import hashlib
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED_SHA256 = "d93b050d623550fcf509888a9afe9cb8bd3864869f06dd3fe9ecaa697337f1ba"
PROJECT = Path(__file__).resolve().parents[1]


def configuration(install, scripts, results):
    root = ET.Element("Configuration")
    for key, value in (("Networking", "Disable"), ("ClipboardRedirection", "Disable"),
                       ("AudioInput", "Disable"), ("VideoInput", "Disable"),
                       ("PrinterRedirection", "Disable"), ("MemoryInMB", "16384")):
        ET.SubElement(root, key).text = value
    folders = ET.SubElement(root, "MappedFolders")
    for host, guest, readonly in ((install, "C:\\AionTest\\Client", True),
                                  (scripts, "C:\\AionTest\\Scripts", True),
                                  (results, "C:\\AionTest\\Results", False)):
        folder = ET.SubElement(folders, "MappedFolder")
        for key, value in (("HostFolder", str(host)), ("SandboxFolder", guest),
                           ("ReadOnly", str(readonly).lower())):
            ET.SubElement(folder, key).text = value
    command = ET.SubElement(root, "LogonCommand")
    ET.SubElement(command, "Command").text = (
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\\AionTest\\Scripts\\test.ps1"')
    ET.indent(root)
    return ET.tostring(root, encoding="unicode") + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    install = PROJECT / "research/local/dev-client/AION2"
    executable = install / "Aion2/Binaries/Win64/AION2.exe"
    with executable.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != EXPECTED_SHA256:
        parser.error("Development executable does not match the researched build")
    output = PROJECT / "research/local/dev-client/sandbox-test"
    scripts, results = output / "scripts", output / "results"
    scripts.mkdir(parents=True, exist_ok=True)
    results.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(PROJECT / "tools/sandbox_client_test.ps1", scripts / "test.ps1")
    path = output / "offline-client-test.wsb"
    path.write_text(configuration(install.resolve(), scripts.resolve(), results.resolve()), encoding="utf-8")
    print(f"Prepared {path.relative_to(PROJECT)}; nothing launched.")


if __name__ == "__main__":
    main()
