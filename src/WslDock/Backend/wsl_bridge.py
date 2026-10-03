"""Small, dependency-free WSL bridge. JSON in/out, never shell-evaluates Exec."""
import base64
import configparser
import ctypes as C
import ctypes.util
import json
import math
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


def profile_for(command):
    lower = command.lower()
    if any(x in lower for x in ("google-chrome", "chromium", "codex-pinyin", "chatgpt", "electron")):
        return "chromium"
    if "alacritty" in lower:
        return "alacritty"
    return "none"


def icon_data(icon):
    if not icon:
        return ""
    paths = [Path(icon)] if icon.startswith("/") else [
        Path.home() / ".local/share/icons" / (icon + ".png"),
        *[Path("/usr/share/icons/hicolor") / size / "apps" / (icon + ".png")
          for size in ("128x128", "256x256", "64x64", "48x48", "32x32")],
        Path("/usr/share/pixmaps") / (icon + ".png"),
    ]
    for path in paths:
        try:
            if path.suffix.lower() == ".png" and 0 < path.stat().st_size < 2000000:
                return base64.b64encode(path.read_bytes()).decode("ascii")
        except OSError:
            pass
    return ""


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
                             "wmClass": entry.get("StartupWMClass", ""), "scaleProfile": profile_for(command)})
            except (OSError, ValueError, KeyError, configparser.Error):
                continue
    return {"apps": apps, "distro": os.environ.get("WSL_DISTRO_NAME", "")}


def build_launch(request):
    app = request["app"]
    args = exec_args(app["command"], app.get("sourceName", ""), app.get("iconName", ""), app.get("desktopPath", ""))
    env = os.environ.copy()
    env["WSLDOCK_APP_ID"] = app["id"]
    scale = float(request.get("scale", 1))
    if not math.isfinite(scale) or not 1 <= scale <= 3:
        raise ValueError("缩放比例应为 100%–300%")
    profile = app.get("scaleProfile", "none")
    if profile == "chromium":
        clean, skip = [], False
        for arg in args:
            if skip:
                skip = False
                continue
            if arg == "--force-device-scale-factor":
                skip = True
            elif not arg.startswith("--force-device-scale-factor="):
                clean.append(arg)
        args = clean + ["--force-device-scale-factor=" + format(scale, "g")]
    elif profile == "alacritty":
        # winit's native X11 render scale, not compositor bitmap stretching.
        env["WINIT_X11_SCALE_FACTOR"] = format(scale, "g")
        env["WINIT_UNIX_BACKEND"] = "x11"
        env.pop("WAYLAND_DISPLAY", None)
    elif profile != "none":
        raise ValueError("不支持的缩放适配器")
    return args, env


def launch(request):
    app = request["app"]
    args, env = build_launch(request)
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
    return {"running": sorted(running)}


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



def parse_display_health(text):
    # Only the latest compositor session counts; old failures must not poison recovery.
    starts = list(re.finditer(r"(?m)^.*\bweston [0-9]+\.[0-9]+\.[0-9]+", text))
    if starts:
        text = text[starts[-1].start():]
    failure = next((line for line in reversed(text.splitlines())
                    if "rdp_allocate_shared_memory" in line and "Failed" in line), None)
    if failure:
        return {"state": "broken", "message": "WSLg 无法创建窗口共享内存，应用可能只有图标而没有可用画面。",
                "detail": failure[:1000]}
    return {"state": "ready" if starts else "unknown",
            "message": "未发现已知 WSLg 显示故障" if starts else "无法确认 WSLg 显示状态", "detail": ""}


def display_health():
    try:
        text = Path("/mnt/wslg/weston.log").read_text(errors="replace")[-2000000:]
        return parse_display_health(text)
    except OSError:
        return {"state": "unknown", "message": "无法读取 WSLg 显示日志", "detail": ""}


def main():
    try:
        request = json.loads(sys.stdin.read())
        action = request.get("action")
        if action == "discover":
            result = discover()
        elif action == "launch":
            result = launch(request)
        elif action == "display_health":
            result = display_health()
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
