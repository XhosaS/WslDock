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

    def test_scale_replaces_existing_flag(self):
        request = {'app': {'id': 'a', 'command': 'chrome --force-device-scale-factor=2 --new-window', 'scaleProfile': 'chromium'}, 'scale': 1.5}
        args, env = bridge.build_launch(request)
        self.assertEqual(args, ['chrome', '--new-window', '--force-device-scale-factor=1.5'])
        self.assertEqual(env['WSLDOCK_APP_ID'], 'a')

    def test_alacritty_has_local_native_scale(self):
        with patch.dict(os.environ, {'WAYLAND_DISPLAY': 'wayland-0'}):
            args, env = bridge.build_launch({'app': {'id': 'a', 'command': 'alacritty', 'scaleProfile': 'alacritty'}, 'scale': 2})
            self.assertNotIn('WAYLAND_DISPLAY', env)
            self.assertEqual(os.environ['WAYLAND_DISPLAY'], 'wayland-0')
            self.assertEqual(env['WINIT_X11_SCALE_FACTOR'], '2')

    def test_invalid_scales(self):
        for scale in [0, 5, float('nan'), float('inf')]:
            with self.assertRaises(ValueError):
                bridge.build_launch({'app': {'id': 'a', 'command': 'app'}, 'scale': scale})

    def test_unknown_framework_keeps_command(self):
        args, _ = bridge.build_launch({'app': {'id': 'a', 'command': 'unknown --flag', 'scaleProfile': 'none'}, 'scale': 2})
        self.assertEqual(args, ['unknown', '--flag'])

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


class DisplayHealthTests(unittest.TestCase):
    def test_shared_memory_failure(self):
        health = bridge.parse_display_health("[00:00] weston 9.0.0\n[00:01] rdp_allocate_shared_memory: Failed to open shared memory: Input/output error")
        self.assertEqual(health['state'], 'broken')

    def test_old_failure_does_not_poison_new_session(self):
        log = "weston 9.0.0\nrdp_allocate_shared_memory: Failed to open\nweston 9.0.0\nSession started"
        self.assertEqual(bridge.parse_display_health(log)['state'], 'ready')

    def test_missing_session_is_unknown(self):
        self.assertEqual(bridge.parse_display_health('')['state'], 'unknown')

    def test_unrelated_error_is_not_a_graphics_failure(self):
        self.assertEqual(bridge.parse_display_health('weston 9.0.0\nclipboard: Failed')['state'], 'ready')

    def test_latest_session_failure_still_counts(self):
        log = "weston 9.0.0\nReady\nweston 9.0.0\nrdp_allocate_shared_memory: Failed to open"
        self.assertEqual(bridge.parse_display_health(log)['state'], 'broken')


if __name__ == '__main__':
    unittest.main()
