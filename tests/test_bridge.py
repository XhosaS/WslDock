import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("bridge", Path(__file__).parents[1] / "src/WslDock/Backend/wsl_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class BridgeTests(unittest.TestCase):
    def test_exec_field_codes_and_spaces(self):
        self.assertEqual(bridge.exec_args('"/path with spaces/app" --name %c %U %% %i %k', 'My App', 'icon', '/test/a.desktop'),
                         ['/path with spaces/app', '--name', 'My App', '%', '--icon', 'icon', '/test/a.desktop'])

    def test_no_shell_expansion(self):
        args = bridge.exec_args('app "$(touch /tmp/never)" ";" "|" "$HOME"')
        self.assertEqual(args, ['app', '$(touch /tmp/never)', ';', '|', '$HOME'])

    def test_rejects_ambiguous_field_code(self):
        with self.assertRaises(ValueError):
            bridge.exec_args('app --url=%U')

    def test_launch_preserves_native_command_and_environment(self):
        with patch.dict(os.environ, {'WAYLAND_DISPLAY': 'wayland-0', 'GDK_BACKEND': 'wayland'}):
            args, env = bridge.build_launch({'app': {'id': 'a', 'command': 'code-wsl --ozone-platform=wayland --new-window'}})
            self.assertEqual(args, ['code-wsl', '--ozone-platform=wayland', '--new-window'])
            self.assertEqual(env['WAYLAND_DISPLAY'], 'wayland-0')
            self.assertEqual(env['GDK_BACKEND'], 'wayland')
            self.assertEqual(env['WSLDOCK_APP_ID'], 'a')

    def test_old_scaling_metadata_has_no_effect(self):
        args, env = bridge.build_launch({'app': {'id': 'a', 'command': 'ghostty', 'scaleProfile': 'gtk', 'scalePercent': 200}, 'scale': 2, 'appScalingEnabled': True})
        self.assertEqual(args, ['ghostty'])
        self.assertEqual(env.get('GDK_SCALE'), os.environ.get('GDK_SCALE'))
        self.assertEqual(env.get('GDK_BACKEND'), os.environ.get('GDK_BACKEND'))

    def test_hidden_user_entry_masks_system(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            user = root / 'user/applications'
            system = root / 'system/applications'
            user.mkdir(parents=True); system.mkdir(parents=True)
            (system / 'hidden.desktop').write_text('[Desktop Entry]\nType=Application\nName=Hidden\nExec=app\n')
            (user / 'hidden.desktop').write_text('[Desktop Entry]\nType=Application\nHidden=true\n')
            (system / 'visible.desktop').write_text('[Desktop Entry]\nType=Application\nName=Visible\nExec=app %U\n')
            with patch.dict(os.environ, {'XDG_DATA_HOME': str(user.parent), 'XDG_DATA_DIRS': str(system.parent)}), patch.object(bridge.shutil, 'which', return_value='/usr/bin/app'):
                result = bridge.discover()['apps']
                names = [a['name'] for a in result]
                self.assertNotIn('Hidden', names)
                self.assertIn('Visible', names)


class KeyringLaunchTests(unittest.TestCase):
    def test_unlock_failure_prevents_application_start(self):
        request = {'app': {'id': 'fixture', 'command': 'app'}, 'keyringPassword': 'test-secret'}
        with patch.object(bridge, 'unlock_keyring', side_effect=ValueError('locked')) as unlock, patch.object(bridge.subprocess, 'Popen') as spawn:
            with self.assertRaises(ValueError): bridge.launch(request)
            unlock.assert_called_once_with('test-secret')
            spawn.assert_not_called()

    def test_icon_refresh_uses_desktop_icon_name(self):
        with patch.object(bridge, 'icon_data', return_value='native-pixels') as icon:
            result = bridge.refresh_icons({'apps': [{'id': 'fixture', 'iconName': 'org.gnome.Nautilus'}]})
            self.assertEqual(result, {'icons': [{'id': 'fixture', 'iconPng': 'native-pixels'}]})
            icon.assert_called_once_with('org.gnome.Nautilus')


class WindowIdentityTests(unittest.TestCase):
    header = "weston 9.0.0\n[00:00] appListProviderName:Ubuntu\n[00:00] appListProviderUniqueId:00000001-FACB-11E6-BD58-64006A7986D3\n"
    window = "[00:01] Client: ClientGetAppidReq: pid:123 appId:org.gnome.Nautilus WindowId:0x172\n"

    def test_exact_provider_and_full_desktop_id(self):
        self.assertEqual(bridge.parse_window_apps(self.header + self.window, 'Ubuntu'),
                         {str((1 << 32) | 0x172): 'org.gnome.Nautilus'})

    def test_other_distro_and_missing_header_are_rejected(self):
        self.assertEqual(bridge.parse_window_apps(self.header + self.window, 'Debian'), {})
        self.assertEqual(bridge.parse_window_apps(self.window, 'Ubuntu'), {})

    def test_new_session_discards_old_windows(self):
        self.assertEqual(bridge.parse_window_apps(self.header + self.window + self.header, 'Ubuntu'), {})

    def test_reused_window_id_uses_latest_identity(self):
        text = self.header + self.window + self.window.replace('org.gnome.Nautilus', 'other.App')
        self.assertEqual(bridge.parse_window_apps(text, 'Ubuntu')[str((1 << 32) | 0x172)], 'other.App')

    def test_missing_app_id_invalidates_previous_identity(self):
        text = self.header + self.window + 'ClientGetAppidReq: WindowId:0x172 does not have appId, or not top level window.'
        self.assertEqual(bridge.parse_window_apps(text, 'Ubuntu'), {})


if __name__ == '__main__':
    unittest.main()
