"""Optional native Linux tests; run with WSL Python, isolated from user secrets."""
import base64
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import unittest

spec = importlib.util.spec_from_file_location('bridge', Path(__file__).parents[1] / 'src/WslDock/Backend/wsl_bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


@unittest.skipUnless(sys.platform == 'linux', 'Linux GTK/GIO integration')
class NativeBridgeTests(unittest.TestCase):
    def test_original_svg_and_icon_theme_inheritance(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            theme = root / 'FixtureTheme'
            icons = theme / 'scalable/apps'
            icons.mkdir(parents=True)
            (theme / 'index.theme').write_text('[Icon Theme]\nName=Fixture\nDirectories=scalable/apps\nInherits=Adwaita\n[scalable/apps]\nSize=128\nType=Scalable\nMinSize=1\nMaxSize=256\nContext=Applications\n')
            svg = icons / 'fixture-native.svg'
            svg.write_text('<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128"><rect width="128" height="128" fill="#0088cc"/></svg>')
            gtk, pix, glib, obj, original = bridge.native_icons()
            theme_ptr = gtk.gtk_icon_theme_new()
            gtk.gtk_icon_theme_set_search_path.argtypes = [bridge.C.c_void_p, bridge.C.POINTER(bridge.C.c_char_p), bridge.C.c_int]
            paths = (bridge.C.c_char_p * 2)(str(root).encode(), b'/usr/share/icons')
            gtk.gtk_icon_theme_set_search_path(theme_ptr, paths, 2)
            gtk.gtk_icon_theme_set_custom_theme(theme_ptr, b'FixtureTheme')
            bridge._icon_libraries = gtk, pix, glib, obj, theme_ptr
            try:
                for icon in [str(svg), 'fixture-native', 'folder']:
                    png = base64.b64decode(bridge.icon_data(icon))
                    self.assertEqual(png[:8], b'\x89PNG\r\n\x1a\n', icon)
                    self.assertEqual(struct.unpack('>II', png[16:24]), (128, 128), icon)
                self.assertEqual(bridge.icon_data('fixture-definitely-missing'), '')
            finally:
                bridge._icon_libraries = gtk, pix, glib, obj, original
                obj.g_object_unref(theme_ptr)

    @unittest.skipUnless(shutil.which('dbus-run-session') and shutil.which('gnome-keyring-daemon'), 'GNOME Keyring required')
    def test_default_keyring_in_isolated_session(self):
        with tempfile.TemporaryDirectory() as temp:
            env = os.environ.copy()
            env.update(HOME=temp, XDG_DATA_HOME=temp + '/data', XDG_CONFIG_HOME=temp + '/config',
                       XDG_RUNTIME_DIR=temp, GNOME_KEYRING_CONTROL=temp + '/keyring')
            result = subprocess.run(['dbus-run-session', '--', sys.executable, __file__, '--keyring-child'],
                                    env=env, capture_output=True, text=True, timeout=45)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


def keyring_child():
    # Never connect to the user's session or lock their default collection.
    assert os.environ['XDG_RUNTIME_DIR'] == os.environ['HOME']
    daemon = subprocess.Popen(['gnome-keyring-daemon', '--foreground', '--components=secrets',
                               '--control-directory=' + os.environ['GNOME_KEYRING_CONTROL']],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    bus = None
    service = 'org.freedesktop.Secret.Service'
    password = 'fixture 中文 " $(ignored) ;\npassword'
    try:
        for _ in range(30):
            try:
                bus = bridge.SecretBus()
                session = bus.string(bus.child(bus.call(service, 'OpenSession', "('plain', <''>)"), 1))
                break
            except ValueError:
                if bus: bus.close(); bus = None
                time.sleep(.1)
        assert bus
        secret = ', '.join('byte 0x%02x' % b for b in password.encode())
        params = "(@a{sv} {'org.freedesktop.Secret.Collection.Label': <'Default Keyring'>}, (objectpath '%s', @ay [], @ay [%s], 'text/plain'))" % (session, secret)
        result = bus.call('org.gnome.keyring.InternalUnsupportedGuiltRiddenInterface', 'CreateWithMasterPassword', params)
        collection = bus.string(bus.child(result, 0))
        bus.call(service, 'SetAlias', "('default', objectpath '%s')" % collection)
        bus.call(service, 'Lock', "([objectpath '%s'],)" % collection)
        assert bus.locked(collection)
        try:
            bridge.unlock_keyring('wrong sensitive fixture password')
            raise AssertionError('wrong password accepted')
        except ValueError as error:
            assert 'sensitive' not in str(error)
        assert bus.locked(collection)
        assert bridge.unlock_keyring(password) == {'alreadyUnlocked': False}
        assert not bus.locked(collection)
        assert bridge.unlock_keyring(password) == {'alreadyUnlocked': True}
        bus.call(service, 'Lock', "([objectpath '%s'],)" % collection)
        assert bridge.unlock_keyring(password) == {'alreadyUnlocked': False}
        print('PASS isolated Default Keyring: wrong password, Unicode, unlock, already unlocked, relock')
    finally:
        if bus: bus.close()
        daemon.terminate()
        try: daemon.wait(timeout=5)
        except subprocess.TimeoutExpired: daemon.kill(); daemon.wait()


if __name__ == '__main__':
    if '--keyring-child' in sys.argv: keyring_child()
    else: unittest.main()
