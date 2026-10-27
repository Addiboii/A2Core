"""Chained loopback login + game-server + HTTP-bootstrap runner.

Runs the full original-client path in one process:

1. Start the login server on 127.0.0.1:3700 (the dev client's configured
   LoginServerIP) and the game server on 127.0.0.1:13328 (the 0x390f redirect).
2. Start the reversible Saved_AionDev HTTP bootstrap with the accepted fixtures
   whose policy custom_data already points LoginServerIP at 127.0.0.1:3700.
3. The user drives the client's region screen (select Europe, Continue) once
   it appears; from there login proceeds automatically through the game-port
   handshake and the candidate 0x3615 game-login reply.

Constraints: loopback only; all crypto and protocol data are locally generated;
no official capture bytes are replayed and no key material is logged. The
development client is launched normally (not copied) and its configuration is
restored when it exits, as in tools/probe_dev_bootstrap.py.

Usage (Windows only, from the project root):

    python tools/run_game_chain.py --seconds 240 --followup-seconds 300
    python tools/run_game_chain.py --login --seconds 600 --followup-seconds 1200   # saved characters

Stop early by creating a file named `stop` in the run's output directory.
"""
import argparse
import csv
import io
from datetime import datetime
import json
import os
import socket
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "research/local"

try:
    from .elevated_dotnet_host import is_elevated, start_elevated_host
    from . import login_server, game_server, lime_service
    from .run_diagnostics import record_error, write_manifest
except ImportError:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from elevated_dotnet_host import is_elevated, start_elevated_host
    import login_server, game_server, lime_service
    from run_diagnostics import record_error, write_manifest

FIXTURES = ROOT / "research/local/bootstrap-fixtures"


def wait_for_file(path, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            return path.read_text(encoding="ascii").strip()
        time.sleep(0.1)
    return None


def wait_for_server(port, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with socket.socket() as probe:
            probe.settimeout(0.5)
            try:
                probe.connect(("127.0.0.1", port))
                probe.close()
                return True
            except OSError:
                time.sleep(0.2)
    return False


def run_server(target, kwargs, stop_event):
    try:
        target(**kwargs)
    except Exception as error:
        if kwargs.get('save_dir') is not None:
            record_error(kwargs['save_dir'], error)
        print("server thread error: %s" % error, flush=True)
    finally:
        stop_event.set()


def aion2_processes():
    """Return live AION2.exe PIDs via tasklist (works unelevated)."""
    result = subprocess.run(
        ['tasklist.exe', '/FI', 'IMAGENAME eq AION2.exe', '/FO', 'CSV', '/NH'],
        check=True, capture_output=True)
    text = result.stdout.decode('utf-8', errors='replace')
    pids = []
    for row in csv.reader(io.StringIO(text)):
        if row and row[0].strip().lower() == 'aion2.exe':
            pids.append(int(row[1]))
    return pids


def geolocation_response(text):
    """Our own GET geolocations/v1.0/me body for `LAT,LON,CC`.

    Layout from the client's parser (live read, research/region-availability.md):
    `geolocation` must be a JSON string (EJson type 2; CANDIDATE: the country code),
    `coordinates` an object (type 6), and its `latitude`/`longitude` strings (type 2).
    Numbers fail those checks and leave the position at 0,0 (all regions red).
    """
    try:
        lat, lon, country = text.split(',')
        lat, lon, country = float(lat), float(lon), country.strip().upper()
    except ValueError:
        raise ValueError('--geolocation must be LAT,LON,CC') from None
    if not (-90 <= lat <= 90 and -180 <= lon <= 180 and len(country) == 2 and country.isalpha()):
        raise ValueError('--geolocation must be LAT,LON,CC')
    return {'geolocation': country,
            'coordinates': {'latitude': f'{lat:.4f}', 'longitude': f'{lon:.4f}'}}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seconds", type=int, default=240)
    parser.add_argument("--followup-seconds", type=int, default=300)
    parser.add_argument('--launch-authorization', type=Path,
                        help='Account-page authorization file; verifies both login hops and disables raw captures')
    parser.add_argument('--skip-firewall', action='store_true',
                        help="Don't check/install the dev client's local-only firewall rules")
    parser.add_argument('--geolocation', default='59.9139,10.7522,NO', metavar='LAT,LON,CC',
                        help="Location our bootstrap reports for the region screen's distance bars "
                             "(default Oslo; 'off' keeps the old 503)")
    parser.add_argument('--login', nargs='?', const='', metavar='USERNAME',
                        help='Sign in to the account service at launch (window or prompt) instead of a file')
    parser.add_argument("--region-probe", action="store_true",
                        help="Run the read-only region-screen probe once the client reaches the region screen")
    parser.add_argument("--region-probe-delay", type=int, default=45,
                        help="Seconds to wait after the client process appears before probing")
    parser.add_argument("--pre-generated-intro", action="store_true",
                        help="Seed one '$'-named generic intro character in 0x390b so the client skips character creation")
    parser.add_argument("--character-level", type=int, default=1,
                        help="Development character level (1-100); skills follow it. Stored levels are not wired in yet")
    parser.add_argument("--layered-region", action="store_true",
                        help="Use the layered per-element GlobalRegionKeys removal + single Potatoe World row wrapper")
    parser.add_argument("--lime-port", type=int, default=8091,
                        help="Port for tools/lime_service.py (our chat backend; 0 disables). "
                             "Use with a policy fixture whose ChatServerURL points at it")
    parser.add_argument("--chat-backend", choices=("dotnet", "python"), default="dotnet",
                        help="Chat API on --lime-port: the .NET host (default, needs a built "
                             "server/Aion.Host) or the Python reference tools/lime_service.py")
    parser.add_argument("--gm-name", action="append", metavar="CHARACTER",
                        help="Development logins: character name allowed to use /gm chat commands (repeatable)")
    parser.add_argument("--native-backend", choices=("dotnet", "python"), default="dotnet",
                        help="Login (3700) and game (13328) ports: the .NET host (default, needs a built "
                             "server/Aion.Host; also serves chat) or the Python reference servers")
    parser.add_argument('--ncguard-fixture', type=Path,
                        help='Serve this NCGuard TLS fixture on 443; requests UAC for the server only')
    parser.add_argument("--policy-fixture", type=Path, default=FIXTURES / "policy-login-route-20261009-1230",
                        help="Directory whose app-policy.json the bootstrap serves (e.g. policy-chat-20261014 "
                             "adds the ChatServerURL/CommunityUIEnable custom_data candidates)")
    args = parser.parse_args(argv)
    if args.ncguard_fixture and args.native_backend != 'dotnet':
        parser.error('--ncguard-fixture requires --native-backend dotnet')
    if args.ncguard_fixture and not args.ncguard_fixture.is_file():
        parser.error('--ncguard-fixture must name an existing fixture file')
    if is_elevated():
        parser.error('Run the launcher from normal PowerShell so the game is not elevated. '
                     'The NCGuard server requests its own UAC elevation.')
    return _with_account_login(args, parser)


def _with_account_login(args, parser):
    """--login: sign in, request the launch authorization, run, then sign out.

    The authorization is written to a private file under research/local only
    because the bootstrap process reads it from a path; it is single-use, valid
    for five minutes and deleted when the run ends.
    """
    if args.login is None:
        return _run(args, parser)
    if args.launch_authorization:
        parser.error('Use either --login or --launch-authorization')
    try:
        from .account_login import AccountSession, sign_in_for_launch
    except ImportError:
        from account_login import AccountSession, sign_in_for_launch
    account = AccountSession()
    authorization = sign_in_for_launch(account, args.login)
    if authorization is None:
        account.sign_out()
        print('Sign-in cancelled; nothing launched.', flush=True)
        return 1
    path = LOCAL / ('launch-' + uuid.uuid4().hex + '.json')
    path.write_text(json.dumps(authorization), encoding='utf-8')
    args.launch_authorization = path
    print('Signed in; launching.', flush=True)
    try:
        return _run(args, parser)
    finally:
        path.unlink(missing_ok=True)
        account.sign_out()  # after the game connection's final save


FIREWALL_GROUP = 'AION2 dev client - local only'


def ensure_local_only_firewall():
    """Make sure every dev-client program is blocked from non-loopback addresses.

    The client has NCSoft hosts compiled in (NCGPA telemetry, NCTinyUpdater CDN), which
    no server reply can switch off, so each player's PC needs the block. Reading rules
    needs no elevation; when any program lacks its rules, tools/dev_client_firewall.ps1
    runs elevated once (a UAC prompt). Returns True when the rules are in place.
    """
    programs = sorted(str(p.resolve()).lower() for p in (LOCAL / 'dev-client').rglob('*.exe'))
    query = ("$f = Get-NetFirewallRule -Group '" + FIREWALL_GROUP + "' -ErrorAction SilentlyContinue | "
             "Get-NetFirewallApplicationFilter; ($f | Select-Object -ExpandProperty Program) -join \"`n\"")

    def covered():
        result = subprocess.run(['powershell', '-NoProfile', '-Command', query], capture_output=True, text=True)
        present = {line.strip().lower() for line in result.stdout.splitlines() if line.strip()}
        return all(program in present for program in programs)

    if not programs or covered():
        return True
    print('Installing local-only firewall rules for the dev client (approve the UAC prompt)...', flush=True)
    script = str(ROOT / 'tools/dev_client_firewall.ps1')
    subprocess.run(['powershell', '-NoProfile', '-Command',
                    "Start-Process powershell -Verb RunAs -Wait -ArgumentList "
                    "'-NoProfile','-ExecutionPolicy','Bypass','-File','" + script + "'"])
    return covered()


def _run(args, parser):
    if os.name != "nt":
        parser.error("Windows required")
    if not args.skip_firewall and not ensure_local_only_firewall():
        parser.error('Dev client firewall rules are missing (UAC declined?); '
                     'run tools/dev_client_firewall.ps1 as Administrator or pass --skip-firewall')
    bridge = None
    if args.launch_authorization:
        if args.pre_generated_intro or args.layered_region:
            parser.error('Authenticated launches cannot use placeholder intro or the historical layered probe')
        try:
            from .client_bridge import ClientBridge, LaunchTokenProvider
        except ImportError:
            from client_bridge import ClientBridge, LaunchTokenProvider
        LaunchTokenProvider(args.launch_authorization)  # Validate file before any server/config changes.
        bridge = ClientBridge()

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    directory = LOCAL / ("chain-" + stamp + "-" + uuid.uuid4().hex[:8])
    directory.mkdir(parents=True, exist_ok=False)
    login_dir = directory / "login"
    game_dir = directory / "game"
    bootstrap_dir = directory / "bootstrap"
    login_dir.mkdir()
    game_dir.mkdir()
    write_manifest(directory, ROOT, {
        'seconds': args.seconds, 'followup_seconds': args.followup_seconds,
        'pre_generated_intro': args.pre_generated_intro,
        'character_level': args.character_level,
        'authenticated_launch': bridge is not None,
        'spawn_map': game_server.SPAWN_MAP_ID,
        'spawn_position': game_server.SPAWN_POSITION,
        'hp': game_server.SPAWN_HP, 'mp': game_server.SPAWN_MP,
        'sp': game_server.SPAWN_SP})
    print("run directory: %s" % directory.relative_to(ROOT), flush=True)

    stop_event = threading.Event()
    login_ready = directory / "login-ready.txt"
    game_ready = directory / "game-ready.txt"
    if args.native_backend == "dotnet":
        if args.layered_region:
            parser.error("--layered-region needs --native-backend python")
        host = start_dotnet_host(args, directory, authenticated=bridge is not None)
        if host is None:
            return 2
        try:
            return _run_bootstrap(args, directory, stop_event, None, None, host)
        finally:
            stop_dotnet_host(host, args, directory)
            host.wait(timeout=15)

    intro_rows = (login_server.build_pre_generated_intro(
                      login_server.DEFAULT_WORLDS[0]["id"])
                  if args.pre_generated_intro else None)
    login_thread = threading.Thread(target=run_server, daemon=True, args=(
        login_server.serve, {
            "worlds": login_server.DEFAULT_WORLDS,
            "account_id": "1000001",
            "authenticate": bridge.login if bridge else None,
            "port": 3700,
            "save_dir": login_dir,
            "seconds": args.seconds + args.followup_seconds,
            "followup_seconds": args.followup_seconds,
            "characters": intro_rows,
            "redirect_host": "127.0.0.1",
            "redirect_port": 13328,
            "on_ready": lambda port: login_ready.write_text(str(port), encoding="ascii"),
        }, stop_event))
    game_thread = threading.Thread(target=run_server, daemon=True, args=(
        game_server.serve, {
            "port": 13328,
            "save_dir": game_dir,
            "seconds": args.seconds + args.followup_seconds,
            "followup_seconds": args.followup_seconds,
            "intro_character": (intro_rows[0] if intro_rows else None),
            "authenticate": bridge.game if bridge else None,
            "character_level": args.character_level,
            "on_ready": lambda port: game_ready.write_text(str(port), encoding="ascii"),
        }, stop_event))
    login_thread.start()
    game_thread.start()

    if not wait_for_file(login_ready, 30) or not wait_for_file(game_ready, 30):
        print("servers failed to become ready", flush=True)
        stop_event.set()
        return 2
    print("login server on 3700 and game server on 13328 are listening", flush=True)
    return _run_bootstrap(args, directory, stop_event, login_thread, game_thread, None)


HOST_DLL = (ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Host/debug/Aion.Host.dll'
            if os.environ.get('AION_ARTIFACTS_PATH') else
            ROOT / "server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll")


BOOTSTRAP_PORT = 8087
KEY_FIXTURES = FIXTURES / "rsa-candidate-20261009"


def dotnet_host_environment(args, authenticated, credentials=None, directory=None):
    """Environment for one .NET host serving the native ports and (optionally) chat.

    Own local HTTP port 8781 so an account server on 8780 keeps running. Authenticated
    launches use the account databases (run_account_server.host_environment), so tickets
    are checked and characters stored; otherwise development logins, nothing saved.
    """
    if authenticated:
        try:
            from .run_account_server import host_environment
        except ImportError:
            from run_account_server import host_environment
        env = host_environment(credentials, port=8781)
    else:
        env = dict(os.environ, Aion__LocalPort="8781")
        env.pop("ConnectionStrings__Aion", None)
        env.pop("ConnectionStrings__Auth", None)
    env.update({"Aion__Native__LoginPort": "3700", "Aion__Native__GamePort": "13328",
                "Aion__Native__Logins": "accounts" if authenticated else "development",
                "Aion__Native__IntroCharacter": "true" if args.pre_generated_intro else "false",
                "Aion__Native__CharacterLevel": str(args.character_level)})
    if args.lime_port and args.chat_backend == "dotnet":
        env.update({"Aion__ChatPort": str(args.lime_port), "Aion__ChatDevelopmentLogins": "true"})
    if directory is not None:
        if getattr(args, 'ncguard_fixture', None):
            env.update({'Aion__NcGuard__Port': '443',
                        'Aion__NcGuard__FixtureFile': str(args.ncguard_fixture.resolve()),
                        'Aion__NcGuard__DataDirectory': str(directory.resolve()),
                        'Aion__NcGuard__RewriteHosts': 'true',
                        'Aion__NcGuard__StopFile': str(directory.resolve() / 'dotnet-stop')})
            env.pop('Aion__NcGuard__DurationSeconds', None)
        # The platform bootstrap (GamePlatform.ini ConfigurationServerUrl) with the same
        # fixtures the Python bootstrap served; probe_dev_bootstrap only edits and launches.
        # Research: per-connection client message traces next to the run's other files (ClientTrace).
        env["Aion__Native__TraceDirectory"] = str(directory.resolve())
        env.update({"Aion__Platform__Port": str(BOOTSTRAP_PORT),
                    "Aion__Platform__FixtureDirectory": str(KEY_FIXTURES),
                    "Aion__Platform__PolicyFile": str(args.policy_fixture.resolve() / "app-policy.json"),
                    "Aion__Platform__RequestLog": str(directory / "bootstrap-requests.jsonl")})
        if args.geolocation != "off":
            geolocation = directory / "geolocation-response.json"
            geolocation.write_text(json.dumps(geolocation_response(args.geolocation)), encoding="utf-8")
            env["Aion__Platform__GeolocationFile"] = str(geolocation)
        if authenticated:
            env["Aion__Platform__LaunchAuthorizationFile"] = str(args.launch_authorization.resolve())
    for index, name in enumerate(getattr(args, "gm_name", None) or []):
        env["Aion__Native__DevelopmentGmNames__%d" % index] = name
    return env


def stop_dotnet_host(host, args, directory):
    if getattr(args, 'ncguard_fixture', None) and host.poll() is None:
        # Windows terminate() bypasses .NET finally/StopAsync and would leave hosts rewritten.
        (directory / 'dotnet-stop').touch()
        host.wait(timeout=45)
    else:
        if host.poll() is None:
            host.terminate()


def start_dotnet_host(args, directory, authenticated):
    if not HOST_DLL.exists():
        print("build the host first: dotnet build server/Aion.Host/Aion.Host.csproj", flush=True)
        return None
    credentials = None
    if authenticated:
        try:
            from .postgres_dev import HOME
        except ImportError:
            from postgres_dev import HOME
        credentials = json.loads((HOME / "credentials.json").read_text())
    environment = dotnet_host_environment(args, authenticated, credentials, directory)
    if getattr(args, 'ncguard_fixture', None):
        print('Approve UAC for the .NET server; launcher and game remain normal users.', flush=True)
        try:
            host = start_elevated_host(HOST_DLL, environment, directory)
        except RuntimeError as error:
            print(str(error), flush=True)
            return None
    else:
        with (directory / "host.log").open("w", encoding="utf-8") as log:
            host = subprocess.Popen(["dotnet", str(HOST_DLL)], cwd=HOST_DLL.parent,
                                    env=environment, stdout=log, stderr=subprocess.STDOUT)
    ports = [3700, 13328, BOOTSTRAP_PORT] + ([args.lime_port] if args.lime_port and args.chat_backend == "dotnet" else [])
    if getattr(args, 'ncguard_fixture', None):
        ports.append(443)
    if not all(wait_for_server(port, 30) for port in ports):
        print("the .NET host did not open %s (see host.log)" % ports, flush=True)
        stop_dotnet_host(host, args, directory)
        return None
    print(".NET host: bootstrap %d, login 3700, game 13328%s (%s logins)" % (
        BOOTSTRAP_PORT, ", chat %d" % args.lime_port if len(ports) > 3 else "",
        "account" if authenticated else "development"), flush=True)
    return host


def _run_bootstrap(args, directory, stop_event, login_thread, game_thread, native_host):
    login_dir = directory / "login"
    game_dir = directory / "game"
    chat_host = None
    if native_host is not None and args.chat_backend == "dotnet":
        pass  # chat (if any) is served by the native host itself
    elif args.lime_port and args.chat_backend == "dotnet":
        # The .NET host's Lime chat API (server/Aion.Host/Chat). Own local port so an account
        # server already on 8780 keeps running; development chat logins only.
        host_dll = HOST_DLL
        if not host_dll.exists():
            print("chat: build the host first: dotnet build server/Aion.Host/Aion.Host.csproj", flush=True)
            stop_event.set()
            return 2
        chat_env = dict(os.environ, Aion__LocalPort="8781", Aion__ChatPort=str(args.lime_port),
                        Aion__ChatDevelopmentLogins="true")
        chat_log = (directory / "chat-host.log").open("w", encoding="utf-8")
        chat_host = subprocess.Popen(["dotnet", str(host_dll)], cwd=host_dll.parent, env=chat_env,
                                     stdout=chat_log, stderr=subprocess.STDOUT)
        print("lime chat service on %d (.NET host)" % args.lime_port, flush=True)
    elif args.lime_port:
        # Python reference implementation (tools/lime_service.py), logs every request.
        threading.Thread(target=run_server, daemon=True, args=(lime_service.serve, {
            "port": args.lime_port, "log_path": directory / "lime.jsonl",
            "stop_event": stop_event}, stop_event)).start()
        print("lime chat service on %d (python)" % args.lime_port, flush=True)

    fixture = FIXTURES / "rsa-candidate-20261009"
    policy = args.policy_fixture.resolve()
    bootstrap_cmd = [
        sys.executable, str(ROOT / "tools/probe_dev_bootstrap.py"),
        "--seconds", str(args.seconds),
        "--key-response", str(fixture / "app-key.json"),
        "--policy-response", str(policy / "app-policy.json"),
        "--maintenance-response", str(fixture / "app-maintenance.json"),
        "--session-response", str(fixture / "session-response.candidate.json"),
        "--authn-response", str(fixture / "authn-response.candidate.json"),
        "--secondary-start-response", str(fixture / "secondary-start-response.candidate.json"),
        "--secondary-finish-response", str(fixture / "secondary-finish-response.candidate.json"),
        "--record-route-paths",
    ]
    if args.layered_region:
        bootstrap_cmd = [sys.executable, str(LOCAL / "probe_potatoe_world_layered.py"),
                         "--seconds", str(args.seconds),
                         "--key-response", str(fixture / "app-key.json"),
                         "--policy-response", str(policy / "app-policy.json"),
                         "--maintenance-response", str(fixture / "app-maintenance.json"),
                         "--session-response", str(fixture / "session-response.candidate.json"),
                         "--authn-response", str(fixture / "authn-response.candidate.json"),
                         "--secondary-start-response", str(fixture / "secondary-start-response.candidate.json"),
                         "--secondary-finish-response", str(fixture / "secondary-finish-response.candidate.json"),
                         "--record-route-paths"]
    if native_host is not None:
        # The host serves the bootstrap routes; the launcher edits the config, launches, restores.
        bootstrap_cmd = [sys.executable, str(ROOT / "tools/probe_dev_bootstrap.py"), "--seconds", str(args.seconds),
                         "--port", str(BOOTSTRAP_PORT), "--external-server"]
    elif args.launch_authorization:
        bootstrap_cmd += ['--launch-authorization', str(args.launch_authorization.resolve())]
    if args.geolocation != 'off' and not args.layered_region and native_host is None:
        geolocation = directory / 'geolocation-response.json'
        geolocation.write_text(json.dumps(geolocation_response(args.geolocation)), encoding='utf-8')
        bootstrap_cmd += ['--geolocation-response', str(geolocation)]
    print("launching bootstrap: " + " ".join(bootstrap_cmd[3:]), flush=True)
    bootstrap = subprocess.Popen(bootstrap_cmd, cwd=ROOT)
    probe_status = "not_requested"
    try:
        if args.region_probe:
            # AION2.exe launches as a parent/launcher that later spawns the game
            # process whose code pages are unpacked. Poll for every AION2 PID and
            # probe each until one returns decrypted UE markers, which identifies
            # the real game image and lets the anchored follow-up run.
            def run_region_probe(pid):
                output = LOCAL / ("region-screen-" + datetime.now().strftime("%Y%m%d-%H%M%S")
                                  + "-" + uuid.uuid4().hex[:8] + "-%d.json" % pid)
                probe_cmd = [
                    sys.executable, str(LOCAL / "inspect_region_screen.py"),
                    "--pid", str(pid),
                    "--install", str(ROOT / "research/local/dev-client/AION2"),
                    "--output", str(output),
                    "--profile", "startup",
                    "--followup-plan", str(LOCAL / "region-screen-plan.json"),
                ]
                print("region probe: %s" % " ".join(probe_cmd[1:]), flush=True)
                result = subprocess.run(probe_cmd, cwd=ROOT)
                print("region probe pid=%s exit=%d report=%s" % (pid, result.returncode, output.relative_to(ROOT)), flush=True)
                report = None
                try:
                    report = json.loads(output.read_text(encoding="utf-8"))
                except (OSError, ValueError):
                    pass
                return report

            first_deadline = time.monotonic() + 120
            seen = set()
            marker_report = None
            # Phase 1: wait for the first AION2 process to appear.
            while time.monotonic() < first_deadline and not aion2_processes():
                time.sleep(1)
            print("client process(es) observed: %s" % aion2_processes(), flush=True)
            # Phase 2: poll for the game process and probe new PIDs until one
            # shows decrypted UE markers, or the delay window closes.
            poll_deadline = time.monotonic() + args.region_probe_delay
            attempts = {}
            while time.monotonic() < poll_deadline:
                pids = [p for p in aion2_processes() if p not in seen]
                if pids:
                    print("new client process(es): %s" % pids, flush=True)
                for pid in pids:
                    seen.add(pid)
                    attempts[pid] = 0
                for pid in seen:
                    if marker_report is not None:
                        break
                    attempts[pid] = attempts.get(pid, 0) + 1
                    if attempts[pid] > 3:
                        continue
                    report = run_region_probe(pid)
                    if report and len(report.get("runtime_scan", {}).get("markers", [])) > 0:
                        marker_report = report
                        probe_status = "completed"
                        break
                if marker_report is not None:
                    break
                time.sleep(4)
            if marker_report is not None:
                print("region probe: decrypted image found (markers=%d, followup=%s)" % (
                    len(marker_report["runtime_scan"]["markers"]),
                    marker_report.get("followup", {}).get("status")), flush=True)
            else:
                print("region probe: no decrypted image observed; status=no_marker_image", flush=True)
                probe_status = "no_marker_image"
        bootstrap.wait()
    except KeyboardInterrupt:
        bootstrap.terminate()
    finally:
        stop_event.set()
        if chat_host is not None:
            chat_host.terminate()
            chat_host.wait(timeout=10)
        for thread in (login_thread, game_thread):
            if thread is not None:
                thread.join(timeout=5)

    login_report = login_dir / "report.json"
    game_report = game_dir / "report.json"
    summary = {"login": json.loads(login_report.read_text(encoding="utf-8"))
               if login_report.exists() else None,
               "game": json.loads(game_report.read_text(encoding="utf-8"))
               if game_report.exists() else None,
               "region_probe": probe_status}
    (directory / "summary.json").write_text(
        json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print("summary: %s" % (directory / "summary.json").relative_to(ROOT), flush=True)
    if summary["game"]:
        for report in summary["game"]:
            headers = report.get("decrypted_body_headers_rc4_body_continuing", [])
            print("game headers: %s" % ", ".join(headers), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
