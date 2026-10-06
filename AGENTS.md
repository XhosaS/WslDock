# WslDock 开发指南

本文件面向开发者和编码代理；README.md 面向最终用户。

## 范围与结构

- Windows 11 x64，C# / WPF、.NET 8；Linux 侧通过 WSLg 和发行版 Python 3 工作。
- `src/WslDock/App.xaml.cs`：生命周期、托盘、设置窗口复用。
- `SingleInstance.cs`：当前 Windows 会话的命名互斥锁与仅限当前用户的命名管道；二次启动只转发激活意图，`--startup` 静默退出。锁必须在获得它的线程释放。
- `DockWindow.cs` / `DesktopHost.cs`：Dock 渲染、轮询、桌面挂载与拖动。保持桌面层行为，不改为置顶窗口。
- `Native.cs`：通过 WSLg `RAIL_WINDOW` 的公开属性读取身份。缺失图标时，`WslgCompatibility.cs` 使用 `WslgServerWindowId` 与当前会话日志的 provider/window ID 精确映射，按 desktop ID 唯一匹配后补窗口图标；退出时恢复。控制 HWND 前重新校验进程、应用和发行版，禁止通过任意标题模糊匹配操控窗口。
- `Backend/WslService.cs` / `wsl_bridge.py`：通过 stdin/stdout JSON 调用嵌入的 Python 桥，负责发现、启动、状态和日志。
- `SettingsWindow.cs` / `Theme.cs` / `AppMenus.cs`：设置、动态主题与菜单。
- `KeyringPassword.cs`：按发行版使用当前用户 DPAPI 加密密码。明文只经 stdin JSON 传入桥，禁止放入参数、日志或异常文本；解锁默认集合，不能仅解锁 login 集合。
- 图标仅取 Linux PNG/SVG 和 GTK 图标主题，不使用带 WSL 角标的 Windows ICO；旧缓存只在发行版运行后迁移。
- `StartupRegistration.cs`：仅修改当前用户 Run 键下的 WslDock 值；默认不启用自启。
- `assets/branding/icon-spec.json`：当前图标几何源。`src/WslDock/Assets` 是编译所需图标，必须提交。
- `installer/WslDock.iss` / `scripts/package.ps1`：当前用户安装程序和自包含 ZIP。

修改前检查 `git status --short`，保留已有用户改动。不要提交本地 SDK、诊断日志、用户设置、构建缓存或旧设计原型。

## 环境与构建

需要 .NET 8 SDK（`global.json` 指定最低 8.0.425，允许后续 feature band）、Python 3 和 Windows 11。构建脚本优先使用 `.tools/dotnet/dotnet.exe`，否则使用 PATH 中的 dotnet。

```powershell
./scripts/build.ps1
./scripts/build.ps1 -Publish
./artifacts/WslDock/WslDock.exe --settings
```

发布为 `win-x64` 自包含目录，包含 .NET Desktop Runtime，不启用裁剪。输出位于 `artifacts/WslDock`；每次发布会清空该输出目录，先退出其中运行的程序。

重新生成图标：

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File ./scripts/export-icons.ps1
```

脚本从当前几何源生成程序资源，额外的 SVG/PNG 预览写入忽略的 `artifacts/branding`。

## 测试

提交前至少运行构建与以下测试；只有本地 SDK 时，将 dotnet 替换为 `./.tools/dotnet/dotnet.exe`。

```powershell
python -m unittest discover -s tests -v
dotnet run --project tests/SingleInstance/SingleInstance.Tests.csproj -c Release
dotnet run --project tests/WslRequests/WslRequests.Tests.csproj -c Release
dotnet run --project tests/KeyringPasswords/KeyringPasswords.Tests.csproj -c Release
```

单实例测试使用随机命名空间和真实子进程，覆盖启动期间激活、多次转发、正常释放和异常退出恢复，不影响运行中的应用。GUI 变更还需实际检查重复启动、最小化恢复、设置窗口复用及深浅主题，并用 Computer Use 更新 `docs/screenshots`。

在有 WSLg、Codex/Chrome/Alacritty 的交互式测试机器可运行：

```powershell
./artifacts/WslDock/WslDock.exe --diagnose ./artifacts/diagnostics.json
./artifacts/WslDock/WslDock.exe --smoke-test ./artifacts/verification
```

诊断和显式 smoke-test 不受普通 GUI 单实例限制。设置 `WSLDOCK_CAPTURE_UI=1` 可在配置应用页暂停 60 秒，供 Computer Use 截图。Smoke-test 会显示测试界面、创建唯一标题的 Alacritty 终端，验证桌面挂载、主题、应用身份、窗口恢复与清理；只关闭自己创建的测试窗口。它还使用隔离注册表键验证自启动，不能替代注销登录测试。不要把完整桌面集成测试放入缺少 WSLg 的 CI。

## 实现边界

- 启动命令按参数解析，不通过 shell 拼接执行；保留已有包装器和输入法配置。
- 应用启动参数和环境由已有命令、包装器和 WSLg 处理，WslDock 不注入缩放参数或强制显示后端。不写 `.wslgconfig`、Xresources、xrandr 或全局环境。不得自动终止用户应用或执行 `wsl --shutdown`；托盘“关闭 WSL”仅在用户点击并确认后执行，暂停后台桥请求以避免重新唤醒。
- 状态灯只查询运行发行版，查询失败显示未知状态；不为轮询唤醒停止的发行版。
- 普通应用图标没有右键关闭/预览菜单。窗口关闭桥仅供显式集成测试清理使用。
- 退出 WslDock 不关闭 WSL 应用；设置关闭后保持托盘和 Dock。
- 安装包使用固定 AppId 和当前用户目录；卸载只移除指向自身 EXE 的启动项，保留用户设置。

## 打包与发布

安装 Inno Setup 6，然后执行：

```powershell
./scripts/package.ps1
# 自定义编译器位置：./scripts/package.ps1 -Iscc 'C:/path/to/ISCC.exe'
```

版本来自 `src/WslDock/WslDock.csproj` 的 Version；设置页与打包脚本自动读取。发布前同步 `app.manifest`、README 和 CHANGELOG。

生成 `artifacts/release/WslDock-<version>-win-x64.zip` 和 `WslDock-Setup-v<version>.exe`。验证解压启动、安装/卸载、资源完整性与版本后再发布。安装升级前退出已有 WslDock。

`.github/workflows/build.yml` 在推送/PR 上运行测试并构建，手动触发可打包；推送 `v*` 标签时构建安装包并发布 GitHub Release。标签必须与 csproj 版本一致。Release 上传两个二进制资产；GitHub 自动提供 Source code ZIP/TAR.GZ，共四个下载项。
