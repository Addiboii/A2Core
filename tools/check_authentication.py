"""Exercise authentication and HTTP endpoints in an isolated disposable database."""
import hashlib
import json
import re
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import uuid
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
try:
    from .postgres_dev import ROOT, HOME, sql, migration_script
    from .run_account_server import host_environment, connection_string
except ImportError:
    from postgres_dev import ROOT, HOME, sql, migration_script
    from run_account_server import host_environment, connection_string


def main():
    credentials = json.loads((HOME / 'credentials.json').read_text())
    name = 'aion_auth_test_' + uuid.uuid4().hex
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    env = host_environment(credentials, name, port)
    env['AION_TEST_ADMIN'] = connection_string(name, 'aion_admin', credentials['aion_admin'])
    # A synthetic wardrobe export: served types plus files that must stay hidden.
    wardrobe = tempfile.TemporaryDirectory()
    for relative, content in {'wardrobe.json': '{"items":[]}', 'icons/a.webp': 'RIFF', 'vendor/three/x.js': 'export {};',
                              'notes.txt': 'hidden', '.hidden.json': '{}'}.items():
        (Path(wardrobe.name) / relative).parent.mkdir(parents=True, exist_ok=True)
        (Path(wardrobe.name) / relative).write_text(content, encoding='utf-8')
    (Path(wardrobe.name).parent / 'outside.json').write_text('{}', encoding='utf-8')
    env['Aion__WardrobePath'] = wardrobe.name
    host = None
    log_path = HOME / 'authentication-check-host.log'
    sql(f'CREATE DATABASE {name} OWNER aion_owner;', database='postgres')
    try:
        sql(f'REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO aion_app,aion_auth;', database=name)
        sql(migration_script(sorted((ROOT / 'database/migrations').glob('*.sql'))), database=name)
        assembly = ROOT / 'tests/Aion.Authentication.Checks/bin/Debug/net10.0/Aion.Authentication.Checks.dll'
        host_assembly = ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'
        if os.environ.get('AION_ARTIFACTS_PATH'):
            artifacts = ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin'
            assembly = artifacts / 'Aion.Authentication.Checks/debug/Aion.Authentication.Checks.dll'
            host_assembly = artifacts / 'Aion.Host/debug/Aion.Host.dll'
        result = subprocess.run(['dotnet', str(assembly)], env=env, cwd=ROOT, capture_output=True,
                                text=True, encoding='utf-8', timeout=90)
        output = result.stdout + result.stderr
        for password in credentials.values():
            output = output.replace(password, '[redacted]')
        print(output.strip())
        if result.returncode:
            raise RuntimeError('Authentication database checks failed')
        # The web host receives no administrative connection string.
        del env['AION_TEST_ADMIN']

        def request(path, method='GET', body=None, token=None, bridge=False):
            headers = {}
            if bridge:
                headers['X-Aion-Bridge'] = env['Aion__BridgeSecret']
            if token:
                headers['Authorization'] = 'Bearer ' + token
            if body is not None:
                headers['Content-Type'] = 'application/json'
            data = json.dumps(body).encode() if body is not None else None
            try:
                response = urlopen(Request(f'http://127.0.0.1:{port}' + path, data=data, headers=headers, method=method), timeout=10)
            except HTTPError as error:
                response = error
            with response:
                raw = response.read()
                return response.status, (json.loads(raw) if 'json' in response.headers.get('Content-Type', '') else raw), response.headers

        def launch(log):
            process = subprocess.Popen(['dotnet', str(host_assembly)],
                cwd=ROOT / 'server/Aion.Host', env=env, stdout=log, stderr=subprocess.STDOUT,
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            try:
                for _ in range(100):
                    if process.poll() is not None:
                        raise RuntimeError('Test host exited; inspect authentication-check-host.log')
                    try:
                        if request('/health/live')[0] == 200:
                            return process
                    except (URLError, OSError):
                        pass
                    time.sleep(0.1)
                raise RuntimeError('Test host startup timed out')
            except BaseException:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)
                raise

        checks = 0
        def check(condition, label):
            nonlocal checks
            if not condition:
                raise AssertionError(label)
            checks += 1

        with log_path.open('w', encoding='utf-8') as log:
            host = launch(log)
            # React pages built from web/ (npm run build): every referenced bundle is served, no inline script.
            def page_assets(page):
                return re.findall(rb'(?:src|href)="(/assets/[^"]+)"', page)
            status, page, headers = request('/')
            check(status == 200 and (b' Account</title>' in page or b'Sign in</title>' in page) and page_assets(page), 'account page')
            check("frame-ancestors 'none'" in headers.get('Content-Security-Policy', ''), 'browser security headers')
            check(all(request(a.decode())[0] == 200 for a in page_assets(page)), 'page resources')
            check(b'<script>' not in page and b'<style' not in page, 'no inline scripts or styles')
            check(request('/health/ready')[0] == 503, 'game readiness remains honest')
            status, page, headers = request('/admin.html')
            check(status == 200 and page_assets(page) and "script-src 'self'" in headers.get('Content-Security-Policy', ''), 'admin page served under CSP')
            check(all(request(a.decode())[0] == 200 for a in page_assets(page)) and request('/wardrobe-viewer.js')[0] == 200, 'admin page resources')
            check(request('/wardrobe/data')[1] == {'items': []}, 'wardrobe index served')
            status, _, headers = request('/wardrobe/files/icons/a.webp')
            check(status == 200 and headers.get('Content-Type') == 'image/webp', 'wardrobe texture served with type')
            check(request('/wardrobe/files/vendor/three/x.js')[2].get('Content-Type', '').startswith('text/javascript'), 'vendored module served')
            for hidden in ('/wardrobe/files/notes.txt', '/wardrobe/files/.hidden.json', '/wardrobe/files/../outside.json', '/wardrobe/files/%2e%2e/outside.json'):
                check(request(hidden)[0] == 404, 'wardrobe hides ' + hidden)
            check(request('/api/admin/me')[0] == 401 and request('/api/admin/characters')[0] == 401, 'anonymous staff access denied')
            check(request('/api/characters')[0] == 401, 'anonymous character access denied')
            check(request('/api/accounts/register', 'POST', {'username': 'web', 'password': 'short'})[0] == 400, 'registration validation')
            password = secrets.token_urlsafe(24)
            first = {'username': 'web_first', 'password': password, 'role': 'admin'}
            second = {'username': 'web_second', 'password': password}
            check(request('/api/accounts/register', 'POST', first)[0] == 201, 'HTTP registration')
            check(request('/api/accounts/register', 'POST', second)[0] == 201, 'second HTTP account')
            check(sql("SELECT count(*) FROM auth.account_roles r JOIN auth.accounts a USING(account_id) WHERE a.subject IN ('web_first','web_second');", database=name).stdout.strip() == '0', 'client cannot assign admin role')
            check(request('/api/accounts/login', 'POST', {'username': 'web_first', 'password': 'wrong password'})[0] == 401, 'HTTP wrong password')
            status, login, headers = request('/api/accounts/login', 'POST', first)
            check(status == 200 and headers.get('Cache-Control') == 'no-store', 'login response not cached')
            token = login['token']
            second_token = request('/api/accounts/login', 'POST', second)[1]['token']
            check(request('/api/accounts/me', token=token)[1]['accountId'] == login['accountId'], 'authenticated identity')
            character = str(uuid.uuid4())
            account = str(uuid.UUID(login['accountId']))
            sql(f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{character}','{account}',1204,'WebCharacter',1,1,1);", database=name)
            check(len(request('/api/characters', token=token)[1]) == 1, 'own character listing')
            check(request('/api/characters', token=second_token)[1] == [], 'other account list isolated')
            check(request('/api/characters/' + character, token=second_token)[0] == 404, 'other account details hidden')
            check(request('/api/characters/' + character, token=token)[0] == 200, 'own character details')
            check(request('/api/game-tickets', 'POST', {'worldId': 999}, token)[0] == 400, 'unknown world rejected')
            check(request('/api/game-tickets', 'POST', {'worldId': 1204}, token)[0] == 200, 'authenticated ticket issue')
            check(request('/internal/client/launch-token', 'POST', {'authorization': 'A'*64})[0] == 401, 'machine bridge denies public calls')
            status, launch_approval, _ = request('/api/client-launches', 'POST', token=token)
            check(status == 200, 'explicit client launch authorization')
            try:
                from .client_bridge import ClientBridge
                from .client_identity import parse_identity
                from .world_store import WorldStoreError
            except ImportError:
                from client_bridge import ClientBridge
                from client_identity import parse_identity
                from world_store import WorldStoreError
            adapter = ClientBridge(port)
            ticket = adapter.call('launch-token', {'authorization': launch_approval['authorization']})['token']
            def packet(opcode, fields, tail=b''):
                return opcode + b''.join(bytes([len(v.encode())]) + v.encode() for v in fields) + tail
            login_packet = packet(b'\x05\x39', ['', ticket, 'secondary-test', '', '', 'en'])
            parsed = parse_identity(login_packet, 'login')
            check(parsed.authn_token == ticket, 'observed login layout carries token')
            native_login = adapter.login(login_packet)
            check(native_login is not None and native_login['authenticated_account_id'] == account, 'login listener bridge maps authenticated account')
            check(isinstance(native_login['characters'], list) and all(set(c) == {'name', 'level'} for c in native_login['characters']),
                  'login bridge lists the account world characters for the server count')
            check(adapter.login(login_packet) is not None, 'login bridge accepts the ticket again (return to login screen)')
            account_session = native_login['account_id'] + ':' + native_login['guid']
            game_packet = packet(b'\x13\x36', [account_session, '', ticket, ''], bytes(9))
            native_game = adapter.game(game_packet)
            check(native_game is not None and native_game['authenticated_account_id'] == account, 'game listener bridge binds same account')
            check(adapter.game(game_packet) is None, 'game bridge rejects replay')
            world_store = native_game['world_store']
            check(len(world_store.characters()) == 1, 'world bridge lists owned characters')
            state = world_store.load(character)
            progress = state['progress']
            progress['level'] = 2
            saved = world_store.save(character, state['revision'], progress)
            check(saved['revision'] == state['revision'] + 1, 'world bridge saves progression')
            try:
                world_store.save(character, state['revision'], progress)
                raise AssertionError('stale world save accepted')
            except WorldStoreError as error:
                check(error.status == 409, 'world bridge surfaces stale save conflict')
            check(request('/internal/world/characters')[0] == 401, 'world endpoints require machine secret')
            check(request('/internal/world/characters', bridge=True)[0] == 401, 'world endpoints also require connection token')
            other_world = str(uuid.uuid4())
            sql(f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{other_world}','{account}',1205,'OtherWorld',1,1,1);", database=name)
            check(len(world_store.characters()) == 1, 'world listing excludes other worlds')
            try:
                world_store.load(other_world)
                raise AssertionError('foreign world loaded')
            except WorldStoreError as error:
                check(error.status == 404, 'world detail denies other world')
            host.terminate()
            host.wait(timeout=10)
            host = launch(log)
            check(request('/api/accounts/me', token=token)[0] == 200, 'session survives host restart')
            check(len(request('/api/characters', token=token)[1]) == 2, 'characters survive host restart')
            check(world_store.load(character)['progress']['level'] == 2, 'world connection and save survive host restart')
            check(request('/api/characters/' + character + '/inventory', token=second_token)[0] == 404, 'HTTP inventory owner scope')
            check(request('/api/characters/' + character + '/inventory', token=token)[0] == 200, 'HTTP inventory read')
            command = {'requestId': str(uuid.uuid4()), 'characterId': character, 'command': 'level', 'level': 8}
            check(request('/api/admin/commands', 'POST', command, token)[0] == 403, 'HTTP ordinary user denied GM command')
            check(request('/api/admin/me', token=token)[0] == 403, 'HTTP ordinary user has no staff profile')
            check(request('/api/admin/characters', token=token)[0] == 403, 'HTTP ordinary user cannot search characters')
            check(request('/api/admin/characters/' + character, token=token)[0] == 403, 'HTTP ordinary user cannot inspect characters')
            sql(f"INSERT INTO auth.account_roles VALUES('{account}','admin');", database=name)
            check(request('/api/admin/me', token=token)[1]['roles'] == ['admin'], 'HTTP staff profile lists role')
            check(request('/api/admin/commands', 'POST', command, token)[0] == 200, 'HTTP admin changes level')
            check(request('/api/characters/' + character, token=token)[1]['progress']['level'] == 8, 'HTTP level persisted')
            sql("""
                INSERT INTO catalog.builds VALUES('http-test','{}',now());
                INSERT INTO catalog.items VALUES('http-test',101,'Sword','weapon','{}');
                INSERT INTO catalog.skills VALUES('http-test',201,'Strike','skill','{}');
                INSERT INTO game.equipment_rules VALUES('http-test',101,1,1,ARRAY[1],'HTTP test');
                INSERT INTO game.starter_profiles VALUES('http-test','http-test',1,1,1,true,
                  '{"worldId":1204,"mapId":1000,"position":{"x":1,"y":2,"z":3},"resources":{"hp":1500},"skills":[201],"slots":[[0,201]],"items":[{"templateId":101,"quantity":1,"equipmentSlot":1}]}');
                """, database=name)
            creation = {'requestId': str(uuid.uuid4()), 'profileId': 'http-test', 'name': 'HttpStarter', 'appearance': 'AAH/'}
            created_status, created, _ = request('/api/characters', 'POST', creation, token)
            check(created_status == 200, 'HTTP atomic starter creation')
            check(request('/api/characters', 'POST', creation, token)[1] == created, 'HTTP creation retry')
            starter_path = '/api/characters/' + created['characterId']
            inventory = request(starter_path + '/inventory', token=token)[1]
            check(len(inventory['items']) == 1 and inventory['skills'][0]['slot'] == 0, 'HTTP starter inventory and skills')
            item = inventory['items'][0]
            check(request('/api/admin/me', token=token)[1]['buildId'] == 'http-test', 'HTTP staff profile names catalogue build')
            found = request('/api/admin/characters?q=httpstar', token=token)[1]
            check([row['name'] for row in found] == ['HttpStarter'] and found[0]['accountName'] == 'web_first', 'HTTP staff character search')
            check(request('/api/admin/characters?q=' + 'a' * 65, token=token)[0] == 400, 'HTTP staff search length bound')
            sql("""INSERT INTO catalog.items VALUES('http-test',102,'Sword of Tests','Greatsword','{"facts":{"ItemGrade":"EItemGrade::Unique","ItemLevel":45}}');""", database=name)
            catalog = request('/api/admin/items?q=sword', token=token)[1]
            check(catalog['total'] == 2 and [i['templateId'] for i in catalog['items']] == [101, 102], 'HTTP staff catalogue search')
            check(catalog['items'][1] == {'templateId': 102, 'name': 'Sword of Tests', 'category': 'Greatsword', 'grade': 'Unique', 'level': 45}
                  and catalog['categories'] == ['Greatsword', 'weapon'], 'HTTP catalogue grade, level and categories')
            check(request('/api/admin/items?q=102', token=token)[1]['items'][0]['templateId'] == 102, 'HTTP catalogue ID first')
            paged = request('/api/admin/items?category=Greatsword&offset=0&limit=1', token=token)[1]
            check(paged['total'] == 1 and paged['items'][0]['templateId'] == 102, 'HTTP catalogue category filter')
            check(request('/api/admin/items?limit=500', token=token)[0] == 400, 'HTTP catalogue page bound')
            check(request('/api/admin/items', token=second_token)[0] == 403, 'HTTP ordinary user cannot browse the catalogue')
            new_account = {'username': 'made_by_admin', 'password': 'admin-made-password-1'}
            check(request('/api/admin/accounts', 'POST', new_account, second_token)[0] == 403, 'HTTP ordinary user cannot create accounts')
            check(request('/api/admin/accounts', 'POST', new_account, token)[0] == 201, 'HTTP admin creates an account')
            check(request('/api/admin/accounts', 'POST', new_account, token)[1] == {'error': 'username_taken'}, 'HTTP admin account name taken')
            check(request('/api/admin/accounts', 'POST', {'username': 'x', 'password': 'short'}, token)[1] == {'error': 'invalid_account'}, 'HTTP admin account validation')
            check(request('/api/accounts/login', 'POST', new_account)[0] == 200, 'HTTP admin-made account signs in')
            grant = {'requestId': str(uuid.uuid4()), 'characterId': created['characterId'], 'command': 'grant',
                     'buildId': 'http-test', 'templateId': 101, 'quantity': 3}
            granted = request('/api/admin/commands', 'POST', grant, token)
            check(granted[0] == 200 and request('/api/admin/commands', 'POST', grant, token)[1] == granted[1], 'HTTP grant and idempotent retry')
            detail = request('/api/admin/characters/' + created['characterId'], token=token)[1]
            check(len(detail['inventory']['items']) == 2 and detail['character']['name'] == 'HttpStarter', 'HTTP staff detail shows granted item')
            check(request('/api/admin/characters/' + str(uuid.uuid4()), token=token)[0] == 404, 'HTTP staff detail of unknown character')
            world_created = world_store.create(uuid.uuid4(), 'http-test', 'WorldStarter', b'\x00\xff\x01', voice_type=4, lobby_slot=2)
            check(world_store.load(world_created['characterId'])['appearance'] == 'AP8B', 'world bridge preserves appearance bytes')
            check(len(world_store.inventory(world_created['characterId'])['items']) == 1, 'world bridge loads starter inventory')
            native_state = world_store.load(world_created['characterId'])
            check(native_state['voiceType'] == 4 and native_state['lobbySlot'] == 2, 'world bridge retains voice and slot')
            check(world_store.select(native_state['wireId'])['summary']['characterId'] == world_created['characterId'], 'native numeric key resolves persistent character')
            check(any(row['wireId'] == native_state['wireId'] for row in world_store.lobby()), 'persistent lobby lists native metadata')
            check(world_store.inventory(world_created['characterId'])['items'][0]['wireId'] > 0, 'inventory has stable native item key')
            try:
                from .persistent_characters import PersistentCharacters
                from .aion_protocol import encode_varint
            except ImportError:
                from persistent_characters import PersistentCharacters
                from aion_protocol import encode_varint
            import struct
            sql("INSERT INTO game.starter_profiles SELECT 'dev-v1-1:1',build_id,creation_id,race,gender,enabled,definition FROM game.starter_profiles WHERE profile_id='http-test';", database=name)
            native_adapter = PersistentCharacters(world_store, account)
            creation_packet = b'\x1b\x36\x0cNativeSaved1\x01' + struct.pack('<i',1) + b'\x02\x00\xff\x03\x04\x01'
            native_reply = native_adapter.create(creation_packet, now_ms=1)
            native_key = int.from_bytes(native_reply[6:10], 'little')
            check(native_key > 0 and world_store.select(native_key)['voiceType'] == 3, 'native create adapter persists before reply')
            check(PersistentCharacters(world_store, account).create(creation_packet, now_ms=1) == native_reply, 'native create retry survives new adapter')
            selected_replies = native_adapter.select(b'\x1f\x36' + encode_varint(native_key) + struct.pack('<i3f',999,999,999,999))
            check(native_adapter.selected['progress']['mapId'] == 1000 and len(selected_replies) == 2, 'native select uses stored map')
            check(request(starter_path + '/items/' + item['instanceId'] + '/move', 'POST', {'revision': item['revision'], 'equipmentSlot': None}, token)[0] == 204, 'HTTP unequip')
            check(request(starter_path + '/items/' + item['instanceId'] + '/move', 'POST', {'revision': item['revision'], 'equipmentSlot': 1}, token)[0] == 409, 'HTTP stale equipment move rejected')
            check(request('/api/accounts/password', 'POST', {'currentPassword': password, 'newPassword': password + 'new'}, second_token)[0] == 204, 'HTTP password change')
            check(request('/api/accounts/me', token=second_token)[0] == 401, 'HTTP password revokes session')
            check(request('/api/accounts/logout', 'POST', token=token)[0] == 204, 'HTTP logout')
            check(request('/api/accounts/me', token=token)[0] == 401, 'logged-out bearer denied')
            check(request('/api/game-tickets', 'POST', {'worldId': 1204}, token)[0] == 401, 'logged-out ticket issue denied')
            try:
                world_store.characters()
                raise AssertionError('revoked world session remained usable')
            except WorldStoreError as error:
                check(error.status == 401, 'browser logout revokes world persistence access')
            # A public-style host: closed registration and a staff-only wardrobe (cookie pass).
            host.terminate()
            host.wait(timeout=10)
            env['Aion__Wardrobe__StaffOnly'] = 'true'
            env['Aion__Accounts__Registration'] = 'false'
            env['Aion__Web__StaffPath'] = '/deskx7k2m9q4'
            (Path(wardrobe.name).parent / 'aion-downloads').mkdir(exist_ok=True)
            (Path(wardrobe.name).parent / 'aion-downloads' / 'A2Core Launcher.exe').write_bytes(b'MZ launcher')
            env['Aion__Web__Downloads'] = str(Path(wardrobe.name).parent / 'aion-downloads')
            host = launch(log)
            check(request('/api/status')[1]['launcherAvailable'] is True, 'status reports the launcher download')
            check(request('/download/launcher')[0] == 404, 'launcher download hidden without a ticket')
            check(request('/download/launcher?ticket=' + 'A' * 64)[0] == 404, 'launcher download refuses an unknown ticket')
            check(request('/api/downloads/launcher', 'POST')[0] == 401, 'launcher link needs a signed-in account')
            player = request('/api/accounts/login', 'POST', new_account)[1]['token']   # an ordinary account
            status, link, _ = request('/api/downloads/launcher', 'POST', token=player)
            check(status == 200 and link['url'].startswith('/download/launcher?ticket='), 'signed-in player gets a download link')
            status, body, headers = request(link['url'])
            check(status == 200 and body == b'MZ launcher' and 'Aion%20Horizon.exe' in headers.get('Content-Disposition', ''), 'A2Core Launcher.exe downloaded as an attachment with the link')
            status, manifest, _ = request('/api/downloads/launcher/manifest')
            check(status == 200 and manifest == {'sha256': hashlib.sha256(b'MZ launcher').hexdigest(), 'size': 11, 'version': None},
                  'launcher manifest (hash and size) for the launcher\'s own update check, no sign-in needed')
            (Path(wardrobe.name).parent / 'aion-downloads' / 'A2Core Launcher.exe.version').write_text('0.4.0\n')
            check(request('/api/downloads/launcher/manifest')[1]['version'] == '0.4.0', 'launcher manifest reports the version file')
            check(request('/admin.html')[0] == 404, 'dashboard hidden at /admin.html')
            status, page, headers = request('/deskx7k2m9q4')
            check(status == 200 and b'/assets/' in page and headers.get('X-Robots-Tag') == 'noindex, nofollow', 'dashboard served at its private address')
            check(request('/api/status')[1]['registrationOpen'] is False, 'status reports closed registration')
            check(request('/api/accounts/register', 'POST', {'username': 'late_user', 'password': password})[0] == 403, 'closed registration refuses sign-up')
            check(request('/wardrobe/data')[0] == 404 and request('/wardrobe/files/icons/a.webp')[0] == 404, 'staff-only wardrobe hidden anonymously')
            check(request('/wardrobe-viewer.js')[0] == 200, 'viewer module stays public')
            staff = request('/api/accounts/login', 'POST', first)[1]['token']
            check(request('/api/admin/wardrobe-access', 'POST', token=second_token)[0] in (401, 403), 'ordinary user gets no wardrobe pass')
            granted = urlopen(Request(f'http://127.0.0.1:{port}/api/admin/wardrobe-access', method='POST', headers={'Authorization': 'Bearer ' + staff}), timeout=10)
            cookie = granted.headers.get('Set-Cookie', '')
            check(granted.status == 204 and cookie.startswith('aion_wardrobe=') and 'httponly' in cookie.lower() and 'samesite=strict' in cookie.lower(), 'staff wardrobe pass is an HttpOnly SameSite cookie')
            pass_cookie = cookie.split(';', 1)[0]
            def with_pass(path, method='GET'):
                try:
                    with urlopen(Request(f'http://127.0.0.1:{port}' + path, method=method, headers={'Cookie': pass_cookie}), timeout=10) as response:
                        return response.status, response.read()
                except HTTPError as error:
                    return error.status, b''
            check(with_pass('/wardrobe/data') == (200, b'{"items":[]}') and with_pass('/wardrobe/files/icons/a.webp')[0] == 200, 'staff pass opens the wardrobe')
            check(with_pass('/wardrobe/files/notes.txt')[0] == 404, 'staff pass keeps unserved files hidden')
            check(with_pass('/api/admin/wardrobe-access', 'DELETE')[0] == 204 and with_pass('/wardrobe/data')[0] == 404, 'wardrobe pass revoked')
            limited = [request('/api/accounts/login', 'POST', {'username': 'unknown_web', 'password': password})[0] for _ in range(16)]
            check(429 in limited, 'HTTP attempt rate limit')
        print(f'{checks} HTTP/account-page checks passed.')
    finally:
        if host is not None and host.poll() is None:
            host.terminate()
            host.wait(timeout=10)
        sql(f'DROP DATABASE {name} WITH (FORCE);', database='postgres')
        (Path(wardrobe.name).parent / 'outside.json').unlink(missing_ok=True)
        shutil.rmtree(Path(wardrobe.name).parent / 'aion-downloads', ignore_errors=True)
        wardrobe.cleanup()
    print('Disposable authentication database removed; development users unchanged.')


if __name__ == '__main__':
    main()
