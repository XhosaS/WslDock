"""Small, dependency-free WSL bridge. JSON in/out, never shell-evaluates Exec."""
import base64
import configparser
import ctypes as C
import ctypes.util
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
from pathlib import Path


def exec_args(command, name="", icon="", desktop=""):
    # Desktop Entry Exec quoting is double-quote based, not a shell command.
    lexer = shlex.shlex(command, posix=True)
    lexer.whitespace_split = True
    lexer.commenters = ""
    lexer.quotes = '"'
    result = []
    for arg in lexer:
        if arg in ("%f", "%F", "%u", "%U", "%d", "%D", "%n", "%N", "%v", "%m"):
            continue
        if arg == "%i":
            if icon:
                result.extend(["--icon", icon])
            continue
        arg = arg.replace("%%", "\x01").replace("%c", name).replace("%k", desktop)
        if re.search(r"%[A-Za-z]", arg):
            raise ValueError("不支持嵌入式 Desktop Entry 字段代码：" + arg)
        result.append(arg.replace("\x01", "%"))
    if not result:
        raise ValueError("启动命令为空")
    return result


_icon_libraries = None


def native_icons():
    global _icon_libraries
    if _icon_libraries is not None:
        return _icon_libraries
    gtk = C.CDLL(ctypes.util.find_library("gtk-3") or "libgtk-3.so.0")
    pix = C.CDLL(ctypes.util.find_library("gdk_pixbuf-2.0") or "libgdk_pixbuf-2.0.so.0")
    glib = C.CDLL(ctypes.util.find_library("glib-2.0") or "libglib-2.0.so.0")
    obj = C.CDLL(ctypes.util.find_library("gobject-2.0") or "libgobject-2.0.so.0")
    gtk.gtk_init_check.argtypes = [C.c_void_p, C.c_void_p]
    gtk.gtk_init_check.restype = C.c_int
    gtk.gtk_icon_theme_get_default.restype = C.c_void_p
    gtk.gtk_icon_theme_new.restype = C.c_void_p
    gtk.gtk_icon_theme_set_custom_theme.argtypes = [C.c_void_p, C.c_char_p]
    gtk.gtk_icon_theme_load_icon.argtypes = [C.c_void_p, C.c_char_p, C.c_int, C.c_int, C.POINTER(C.c_void_p)]
    gtk.gtk_icon_theme_load_icon.restype = C.c_void_p
    pix.gdk_pixbuf_new_from_file_at_scale.argtypes = [C.c_char_p, C.c_int, C.c_int, C.c_int, C.POINTER(C.c_void_p)]
    pix.gdk_pixbuf_new_from_file_at_scale.restype = C.c_void_p
    pix.gdk_pixbuf_save_to_buffer.argtypes = [C.c_void_p, C.POINTER(C.c_void_p), C.POINTER(C.c_size_t), C.c_char_p, C.POINTER(C.c_void_p)]
    pix.gdk_pixbuf_save_to_buffer.restype = C.c_int
    glib.g_free.argtypes = [C.c_void_p]
    glib.g_error_free.argtypes = [C.c_void_p]
    obj.g_object_unref.argtypes = [C.c_void_p]
    if gtk.gtk_init_check(None, None):
        theme = gtk.gtk_icon_theme_get_default()
    else:
        theme = gtk.gtk_icon_theme_new()
        gtk.gtk_icon_theme_set_custom_theme(theme, b"Adwaita")
    _icon_libraries = gtk, pix, glib, obj, theme
    return _icon_libraries


def icon_data(icon):
    if not icon:
        return ""
    image = None
    buffer, error, size = C.c_void_p(), C.c_void_p(), C.c_size_t()
    try:
        gtk, pix, glib, obj, theme = native_icons()
        if os.path.isabs(icon):
            image = pix.gdk_pixbuf_new_from_file_at_scale(os.fsencode(icon), 128, 128, 1, C.byref(error))
        else:
            # GTK resolves the active theme, inheritance, XDG paths and scalable SVG.
            image = gtk.gtk_icon_theme_load_icon(theme, icon.encode(), 128, 16, C.byref(error))
        if error.value:
            glib.g_error_free(error); error = C.c_void_p()
        if image and pix.gdk_pixbuf_save_to_buffer(image, C.byref(buffer), C.byref(size), b"png", C.byref(error), C.c_void_p()):
            if 0 < size.value < 2000000:
                return base64.b64encode(C.string_at(buffer, size.value)).decode("ascii")
    except (OSError, AttributeError):
        # Minimal distros can still use a PNG specified by its absolute path.
        roots = [Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))]
        roots += [Path(x) for x in os.environ.get("XDG_DATA_DIRS", "/usr/local/share:/usr/share").split(os.pathsep) if x]
        filename = icon if icon.endswith(".png") else icon + ".png"
        paths = [Path(icon)] if os.path.isabs(icon) else [
            Path.home() / ".icons" / filename,
            *[root / "icons" / filename for root in roots],
            *[root / "icons/hicolor" / size / "apps" / filename for root in roots
              for size in ("128x128", "256x256", "64x64", "48x48", "32x32")],
            *[root / "pixmaps" / filename for root in roots],
        ]
        for path in paths:
            try:
                if path.suffix.lower() == ".png" and 0 < path.stat().st_size < 2000000:
                    return base64.b64encode(path.read_bytes()).decode("ascii")
            except OSError:
                pass
    finally:
        if _icon_libraries is not None:
            _, _, glib, obj, _ = _icon_libraries
            if image: obj.g_object_unref(image)
            if buffer.value: glib.g_free(buffer)
            if error.value: glib.g_error_free(error)
    return ""


def refresh_icons(request):
    result = []
    for app in request.get("apps", []):
        icon = app.get("iconName", "")
        if not icon and app.get("desktopPath"):
            parser = configparser.ConfigParser(interpolation=None, strict=False)
            try:
                parser.read(app["desktopPath"], encoding="utf-8")
                icon = parser.get("Desktop Entry", "Icon", fallback="")
            except (OSError, configparser.Error):
                pass
        result.append({"id": app["id"], "iconPng": icon_data(icon)})
    return {"icons": result}


class SecretBus:
    """GIO session D-Bus. Secrets stay in memory; no shell or gdbus argv."""
    def __init__(self):
        self.gio = C.CDLL(ctypes.util.find_library("gio-2.0") or "libgio-2.0.so.0")
        self.glib = C.CDLL(ctypes.util.find_library("glib-2.0") or "libglib-2.0.so.0")
        self.obj = C.CDLL(ctypes.util.find_library("gobject-2.0") or "libgobject-2.0.so.0")
        self.variants = []
        ptr = C.c_void_p
        for name, args, result in [
            ("g_variant_parse", [ptr, C.c_char_p, ptr, ptr, C.POINTER(ptr)], ptr),
            ("g_variant_ref_sink", [ptr], ptr),
            ("g_variant_get_child_value", [ptr, C.c_size_t], ptr),
            ("g_variant_get_string", [ptr, ptr], C.c_char_p),
            ("g_variant_get_variant", [ptr], ptr),
            ("g_variant_get_boolean", [ptr], C.c_int),
            ("g_variant_unref", [ptr], None),
            ("g_error_free", [ptr], None),
        ]:
            fn = getattr(self.glib, name); fn.argtypes = args; fn.restype = result
        self.obj.g_object_unref.argtypes = [ptr]
        self.gio.g_bus_get_sync.argtypes = [C.c_int, ptr, C.POINTER(ptr)]
        self.gio.g_bus_get_sync.restype = ptr
        self.gio.g_dbus_connection_call_sync.argtypes = [ptr, C.c_char_p, C.c_char_p, C.c_char_p, C.c_char_p, ptr, ptr, C.c_int, C.c_int, ptr, C.POINTER(ptr)]
        self.gio.g_dbus_connection_call_sync.restype = ptr
        error = ptr()
        self.connection = self.gio.g_bus_get_sync(2, None, C.byref(error))
        self.check(self.connection, error)

    def check(self, value, error):
        if error.value:
            self.glib.g_error_free(error)
        if not value:
            # Never forward daemon/parse diagnostics which could contain secret bytes.
            raise ValueError("默认密钥环解锁失败，请检查密钥环密码和会话 D-Bus。")
        return value

    def keep(self, value):
        self.variants.append(value)
        return value

    def child(self, value, index):
        return self.keep(self.glib.g_variant_get_child_value(value, index))

    def string(self, value):
        return self.glib.g_variant_get_string(value, None).decode()

    def call(self, interface, method, parameters=None, path="/org/freedesktop/secrets"):
        value = None
        if parameters is not None:
            error = C.c_void_p()
            value = self.glib.g_variant_parse(None, parameters.encode(), None, None, C.byref(error))
            self.check(value, error)
            self.keep(self.glib.g_variant_ref_sink(value))
        error = C.c_void_p()
        reply = self.gio.g_dbus_connection_call_sync(self.connection, b"org.freedesktop.secrets", path.encode(),
            interface.encode(), method.encode(), value, None, 0, 5000, None, C.byref(error))
        return self.keep(self.check(reply, error))

    def locked(self, collection):
        reply = self.call("org.freedesktop.DBus.Properties", "Get",
            "('org.freedesktop.Secret.Collection', 'Locked')", collection)
        value = self.keep(self.glib.g_variant_get_variant(self.child(reply, 0)))
        return bool(self.glib.g_variant_get_boolean(value))

    def close(self):
        for value in reversed(self.variants): self.glib.g_variant_unref(value)
        self.obj.g_object_unref(self.connection)


def unlock_keyring(password):
    if not isinstance(password, str) or len(password) > 1024 or "\x00" in password:
        raise ValueError("密钥环密码格式无效。")
    bus = None
    session = None
    service = "org.freedesktop.Secret.Service"
    try:
        bus = SecretBus()
        collection = bus.string(bus.child(bus.call(service, "ReadAlias", "('default',)"), 0))
        if collection == "/":
            raise ValueError("未找到默认密钥环。请先在 Linux 中设置默认密钥环。")
        if not bus.locked(collection):
            return {"alreadyUnlocked": True}
        session = bus.string(bus.child(bus.call(service, "OpenSession", "('plain', <''>)"), 1))
        secret = ", ".join("byte 0x%02x" % b for b in password.encode("utf-8"))
        parameters = "(objectpath '%s', (objectpath '%s', @ay [], @ay [%s], 'text/plain'))" % (collection, session, secret)
        bus.call("org.gnome.keyring.InternalUnsupportedGuiltRiddenInterface", "UnlockWithMasterPassword", parameters)
        if bus.locked(collection):
            raise ValueError("默认密钥环仍被锁定，请检查密码。")
        return {"alreadyUnlocked": False}
    except (OSError, AttributeError):
        raise ValueError("密钥环解锁需要 GNOME Keyring、GIO 和会话 D-Bus。") from None
    finally:
        if bus:
            if session:
                try: bus.call("org.freedesktop.Secret.Session", "Close", path=session)
                except ValueError: pass
            bus.close()


def discover():
    roots = [Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share"))) / "applications"]
    roots += [Path(x) / "applications" for x in os.environ.get("XDG_DATA_DIRS", "/usr/local/share:/usr/share").split(os.pathsep) if x]
    roots += [Path("/var/lib/snapd/desktop/applications")]
    seen, names, apps = set(), set(), []
    for root in roots:
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("*.desktop")):
            desktop_id = str(path.relative_to(root)).replace("/", "-")
            if desktop_id in seen:
                continue
            seen.add(desktop_id)  # Hidden user entries must mask system entries too.
            try:
                parser = configparser.ConfigParser(interpolation=None, strict=False)
                parser.read(path, encoding="utf-8")
                entry = parser["Desktop Entry"]
                if entry.get("Type") != "Application" or any(entry.getboolean(x, fallback=False) for x in ("Hidden", "NoDisplay", "Terminal")):
                    continue
                command, name = entry.get("Exec", ""), entry.get("Name", path.stem)
                args = exec_args(command, name, entry.get("Icon", ""), str(path))
                if not shutil.which(entry.get("TryExec", args[0])):
                    continue
                # Some packages expose both a legacy and reverse-DNS desktop entry.
                name_key = name.casefold()
                if name_key in names:
                    continue
                names.add(name_key)
                is_codex = "codex-pinyin" in command or ("chatgpt" in command and "codex-launcher" in os.path.realpath(shutil.which("chatgpt") or ""))
                apps.append({"desktopId": desktop_id, "sourceName": name,
                             "name": "Codex" if is_codex else name, "command": command,
                             "desktopPath": str(path), "workingDirectory": entry.get("Path", ""),
                             "iconName": entry.get("Icon", ""), "iconPng": icon_data(entry.get("Icon", "")),
                             "wmClass": entry.get("StartupWMClass", "")})
            except (OSError, ValueError, KeyError, configparser.Error):
                continue
    return {"apps": apps, "distro": os.environ.get("WSL_DISTRO_NAME", "")}


def build_launch(request):
    app = request["app"]
    args = exec_args(app["command"], app.get("sourceName", ""), app.get("iconName", ""), app.get("desktopPath", ""))
    env = os.environ.copy()
    env["WSLDOCK_APP_ID"] = app["id"]
    return args, env


def launch(request):
    app = request["app"]
    args, env = build_launch(request)
    if request.get("keyringPassword") is not None:
        unlock_keyring(request["keyringPassword"])
    if not shutil.which(args[0]):
        raise ValueError("找不到可执行文件：" + args[0])
    state = Path.home() / ".local/state/wsldock"
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    log = state / (re.sub(r"[^A-Za-z0-9_.-]", "_", app["id"]) + ".log")
    with log.open("wb") as output:
        process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                                   env=env, cwd=app.get("workingDirectory") or str(Path.home()), start_new_session=True)
    try:
        code = process.wait(timeout=0.7)
        if code:
            raise ValueError("启动失败（退出码 %s）。%s" % (code, log.read_text(errors="replace")[-1800:]))
    except subprocess.TimeoutExpired:
        pass
    return {"pid": process.pid, "log": str(log), "args": args}


def parse_window_apps(text, distro):
    # IDs belong to one compositor session; discard associations from old sessions.
    starts = list(re.finditer(r"(?m)^.*\bweston [0-9]+\.[0-9]+\.[0-9]+", text))
    if starts:
        text = text[starts[-1].start():]
    providers = re.findall(r"appListProviderName:([^\r\n]+)\s+\[[^\]]+\]\s+appListProviderUniqueId:([0-9a-fA-F]{8})-[0-9a-fA-F-]+", text)
    if not providers or providers[-1][0].strip() != distro:
        return {}
    prefix = int(providers[-1][1], 16) << 32
    result = {}
    for line in text.splitlines():
        match = re.search(r"ClientGetAppidReq: pid:[0-9]+ appId:(\S+) WindowId:0x([0-9a-fA-F]+)", line)
        if match:
            result[str(prefix | int(match[2], 16))] = match[1]
        else:
            missing = re.search(r"ClientGetAppidReq: WindowId:0x([0-9a-fA-F]+) does not have appId", line)
            if missing:
                result.pop(str(prefix | int(missing[1], 16)), None)
    return result


def window_apps():
    try:
        with Path("/mnt/wslg/weston.log").open("rb") as log:
            # Bound memory and fail closed if the provider/session header is unavailable.
            data = log.read(32000001)
            if len(data) > 32000000:
                return {}
            return parse_window_apps(data.decode("utf-8", errors="replace"), os.environ.get("WSL_DISTRO_NAME", ""))
    except OSError:
        return {}


def status(request):
    # Only inspect markers installed by this launcher; never fuzzy-match process names.
    ids = set(request.get("ids", []))
    running = set()
    for proc in Path("/proc").iterdir():
        if not proc.name.isdigit():
            continue
        try:
            for item in (proc / "environ").read_bytes().split(b"\0"):
                if item.startswith(b"WSLDOCK_APP_ID="):
                    key = item.split(b"=", 1)[1].decode()
                    if key in ids:
                        running.add(key)
        except (OSError, UnicodeError):
            pass
    return {"running": sorted(running), "windowApps": window_apps()}


def close_x11_window(request):
    """Graceful WM_DELETE_WINDOW fallback for WSLg clients that swallow SC_CLOSE.

    Require distro (selected by wsl.exe), verified app class, exact title and a
    unique XID. Never kill a PID, destroy a window, or guess between duplicates.
    """
    lib = ctypes.util.find_library("X11")
    if not lib:
        raise ValueError("此应用无法通过 X11 关闭，请使用应用自身的关闭按钮。")
    x = C.CDLL(lib)
    display_t, xid = C.c_void_p, C.c_ulong
    x.XOpenDisplay.argtypes = [C.c_char_p]; x.XOpenDisplay.restype = display_t
    x.XDefaultRootWindow.argtypes = [display_t]; x.XDefaultRootWindow.restype = xid
    x.XInternAtom.argtypes = [display_t, C.c_char_p, C.c_int]; x.XInternAtom.restype = xid
    x.XGetWindowProperty.argtypes = [display_t, xid, xid, C.c_long, C.c_long, C.c_int, xid, C.POINTER(xid), C.POINTER(C.c_int), C.POINTER(xid), C.POINTER(xid), C.POINTER(C.c_void_p)]
    x.XQueryTree.argtypes = [display_t, xid, C.POINTER(xid), C.POINTER(xid), C.POINTER(C.POINTER(xid)), C.POINTER(C.c_uint)]
    x.XFree.argtypes = [C.c_void_p]
    x.XCloseDisplay.argtypes = [display_t]
    x.XFlush.argtypes = [display_t]
    x.XSync.argtypes = [display_t, C.c_int]
    x.XSendEvent.argtypes = [display_t, xid, C.c_int, C.c_long, C.c_void_p]
    error_handler = C.CFUNCTYPE(C.c_int, display_t, C.c_void_p)(lambda d, e: 0)
    x.XSetErrorHandler.argtypes = [C.c_void_p]; x.XSetErrorHandler(error_handler)
    display = x.XOpenDisplay(None)
    if not display:
        raise ValueError("X11 显示服务不可用，请使用应用自身的关闭按钮。")
    try:
        def atom(name):
            return x.XInternAtom(display, name.encode(), 0)

        def prop(window, name):
            actual, count, remaining = xid(), xid(), xid()
            fmt, pointer = C.c_int(), C.c_void_p()
            result = x.XGetWindowProperty(display, window, atom(name), 0, 2048, 0, 0,
                                          C.byref(actual), C.byref(fmt), C.byref(count), C.byref(remaining), C.byref(pointer))
            if result or not pointer.value:
                return b"" if fmt.value != 32 else []
            try:
                if fmt.value == 32:
                    return list(C.cast(pointer, C.POINTER(xid * count.value)).contents)
                return C.string_at(pointer, count.value * fmt.value // 8)
            finally:
                x.XFree(pointer)

        app = request["app"]
        classes = {app.get("wmClass", "").casefold(), app.get("iconName", "").casefold(),
                   app.get("sourceName", "").casefold(), app.get("desktopId", "").removesuffix(".desktop").casefold()}
        classes.discard("")
        target_title = request["title"]
        suffix = " (" + os.environ.get("WSL_DISTRO_NAME", "") + ")"
        if target_title.endswith(suffix):
            target_title = target_title[:-len(suffix)]
        target_title = target_title.removeprefix("[WARN:COPY MODE] ")
        matches, queue, seen = [], [(x.XDefaultRootWindow(display), 0)], set()
        while queue and len(seen) < 10000:
            window, depth = queue.pop()
            if window in seen:
                continue
            seen.add(window)
            wm_class = prop(window, "WM_CLASS")
            name = prop(window, "_NET_WM_NAME") or prop(window, "WM_NAME")
            if isinstance(wm_class, bytes) and isinstance(name, bytes):
                actual_classes = {s.decode("utf-8", errors="replace").casefold() for s in wm_class.split(b"\0") if s}
                if classes & actual_classes and name.rstrip(b"\0").decode("utf-8", errors="replace") == target_title:
                    matches.append((window, wm_class, name, prop(window, "_NET_WM_PID")))
            if depth >= 4:
                continue
            root, parent = xid(), xid(); children = C.POINTER(xid)(); count = C.c_uint()
            if x.XQueryTree(display, window, C.byref(root), C.byref(parent), C.byref(children), C.byref(count)):
                if children:
                    queue.extend((children[i], depth + 1) for i in range(count.value))
                    x.XFree(children)
        if len(matches) != 1:
            raise ValueError("无法唯一识别 Linux 窗口，请使用应用自身的关闭按钮。")
        window, wm_class, name, pid = matches[0]
        if (wm_class, name, pid) != (prop(window, "WM_CLASS"), prop(window, "_NET_WM_NAME") or prop(window, "WM_NAME"), prop(window, "_NET_WM_PID")):
            raise ValueError("窗口已改变，请重新打开菜单。")
        delete = atom("WM_DELETE_WINDOW")
        protocols = prop(window, "WM_PROTOCOLS")
        if not isinstance(protocols, list) or delete not in protocols:
            raise ValueError("应用没有公开正常关闭协议，请使用应用自身的关闭按钮。")

        class Message(C.Structure):
            _fields_ = [("type", C.c_int), ("serial", xid), ("send_event", C.c_int), ("display", display_t),
                        ("window", xid), ("message_type", xid), ("format", C.c_int), ("data", C.c_long * 5)]
        class Event(C.Union):
            _fields_ = [("message", Message), ("padding", C.c_long * 24)]
        event = Event(); event.message.type = 33; event.message.send_event = 1; event.message.display = display
        event.message.window = window; event.message.message_type = atom("WM_PROTOCOLS"); event.message.format = 32
        event.message.data[0] = delete; event.message.data[1] = 0
        sent = x.XSendEvent(display, window, 0, 0, C.byref(event)); x.XSync(display, 0)
        if not sent:
            raise ValueError("窗口未接受关闭请求。")
        return {"requested": True, "method": "WM_DELETE_WINDOW"}
    finally:
        x.XCloseDisplay(display)



def main():
    try:
        request = json.loads(sys.stdin.read())
        action = request.get("action")
        if action == "discover":
            result = discover()
        elif action == "icons":
            result = refresh_icons(request)
        elif action == "unlock_keyring":
            result = unlock_keyring(request["password"])
        elif action == "launch":
            result = launch(request)
        elif action == "status":
            result = status(request)
        elif action == "close_window":
            result = close_x11_window(request)
        elif action == "log":
            path = Path.home() / ".local/state/wsldock" / (re.sub(r"[^A-Za-z0-9_.-]", "_", request["id"]) + ".log")
            result = {"text": path.read_text(errors="replace")[-20000:] if path.is_file() else "暂无启动日志，请先从 WslDock 启动应用。"}
        else:
            raise ValueError("未知请求")
        print(json.dumps(result, ensure_ascii=False))
    except Exception as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=False))
        sys.exit(1)


if __name__ == "__main__":
    main()
