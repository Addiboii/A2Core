import argparse
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from tools import run_game_chain
from tools import elevated_dotnet_host


def args(**overrides):
    values = dict(lime_port=8091, chat_backend='dotnet', pre_generated_intro=False, character_level=5,
                  policy_fixture=Path('policy-dir'), geolocation='59.9139,10.7522,NO', gm_name=['Player1'],
                  launch_authorization=Path('launch.json'))
    values.update(overrides)
    return argparse.Namespace(**values)


class DotnetHostEnvironmentTests(unittest.TestCase):
    def test_admin_launcher_rejected_before_account_login(self):
        with mock.patch.object(run_game_chain, 'is_elevated', return_value=True), \
                mock.patch.object(run_game_chain, '_with_account_login') as login:
            with self.assertRaises(SystemExit):
                run_game_chain.main(['--login'])
            login.assert_not_called()

    def test_elevated_server_configuration_and_cancel_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            def launch(command, **kwargs):
                config = json.loads((directory / 'dotnet-start.json').read_text())
                self.assertEqual(config['environment'], {'Aion__LocalPort': '8781'})
                self.assertNotIn('unrelated-secret', str(command))
                self.assertIn('-Verb RunAs', command[-1])
                return mock.Mock(returncode=1)
            with mock.patch.object(elevated_dotnet_host.subprocess, 'run', side_effect=launch):
                with self.assertRaises(RuntimeError):
                    elevated_dotnet_host.start_elevated_host(Path('host.dll'),
                        {'Aion__LocalPort': '8781', 'OTHER_TOKEN': 'unrelated-secret'}, directory)
            self.assertFalse((directory / 'dotnet-start.json').exists())

    def test_elevated_host_waits_for_exit_and_stops_gracefully(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            host = elevated_dotnet_host.ElevatedHost(directory)
            self.assertIsNone(host.poll())
            with self.assertRaises(elevated_dotnet_host.subprocess.TimeoutExpired):
                host.wait(timeout=0)
            host.terminate()
            self.assertTrue((directory / 'dotnet-stop').exists())
            (directory / 'dotnet-exit.json').write_text('{"exit_code": 7}', encoding='utf-8-sig')
            self.assertEqual(host.wait(timeout=0), 7)

    def test_development_launch_serves_native_ports_and_chat_without_databases(self):
        with mock.patch.dict('os.environ', {'ConnectionStrings__Aion': 'x', 'ConnectionStrings__Auth': 'y'}):
            env = run_game_chain.dotnet_host_environment(args(), authenticated=False)
        self.assertEqual((env['Aion__Native__LoginPort'], env['Aion__Native__GamePort']), ('3700', '13328'))
        self.assertEqual(env['Aion__Native__Logins'], 'development')
        self.assertEqual(env['Aion__Native__CharacterLevel'], '5')
        self.assertEqual((env['Aion__ChatPort'], env['Aion__ChatDevelopmentLogins']), ('8091', 'true'))
        self.assertEqual(env['Aion__LocalPort'], '8781')  # an account server on 8780 keeps running
        self.assertNotIn('ConnectionStrings__Aion', env)

    def test_authenticated_launch_uses_account_logins_and_databases(self):
        credentials = {'aion_app': 'app-secret', 'aion_auth': 'auth-secret'}
        with mock.patch('tools.run_account_server.bridge_secret', return_value='0' * 64):
            env = run_game_chain.dotnet_host_environment(args(chat_backend='python'), True, credentials)
        self.assertEqual(env['Aion__Native__Logins'], 'accounts')
        self.assertIn('app-secret', env['ConnectionStrings__Aion'])
        self.assertEqual(env['Aion__LocalPort'], '8781')
        self.assertNotIn('Aion__ChatPort', env)  # the Python chat reference serves chat

    def test_bootstrap_routes_and_gm_names(self):
        with tempfile.TemporaryDirectory() as temp:
            env = run_game_chain.dotnet_host_environment(args(), False, directory=Path(temp))
            geolocation = json.loads(Path(env['Aion__Platform__GeolocationFile']).read_text(encoding='utf-8'))
        self.assertEqual(env['Aion__Platform__Port'], '8087')
        self.assertTrue(env['Aion__Platform__PolicyFile'].endswith('app-policy.json'))
        self.assertTrue(env['Aion__Platform__FixtureDirectory'].endswith('rsa-candidate-20261009'))
        self.assertEqual(geolocation['geolocation'], 'NO')
        self.assertNotIn('Aion__Platform__LaunchAuthorizationFile', env)
        self.assertEqual(env['Aion__Native__DevelopmentGmNames__0'], 'Player1')

    def test_ncguard_uses_same_host_and_graceful_stop_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            options = args(ncguard_fixture=directory / 'fixture.json')
            env = run_game_chain.dotnet_host_environment(options, False, directory=directory)
            self.assertEqual(env['Aion__NcGuard__Port'], '443')
            self.assertEqual(env['Aion__NcGuard__RewriteHosts'], 'true')
            self.assertEqual(Path(env['Aion__NcGuard__FixtureFile']), options.ncguard_fixture.resolve())
            host = mock.Mock()
            host.poll.return_value = None
            run_game_chain.stop_dotnet_host(host, options, directory)
            self.assertTrue(Path(env['Aion__NcGuard__StopFile']).exists())
            host.wait.assert_called_once_with(timeout=45)
            host.terminate.assert_not_called()


if __name__ == '__main__':
    unittest.main()
