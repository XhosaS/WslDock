
namespace WslDock;

public sealed class SettingsWindow : Window
{
    private readonly StartupRegistration startup;
    private CheckBox? startupToggle;
    private bool updatingStartup;
    private readonly StackPanel body = new();
    private readonly TextBlock notice = Ui.Text("", 12, "#666666");
    private readonly Button appsNav;
    private readonly Button keyringNav;
    private bool keyringPage;
    private string selectedDistro = "";
    private List<string> distros = new();

    public SettingsWindow() : this(new StartupRegistration()) { }
    internal SettingsWindow(StartupRegistration startup)
    {
        this.startup = startup;
        Style = (Style)App.Current.FindResource(typeof(Window));
        Icon = Branding.AppIcon;
        Title = "WslDock 设置"; Width = 1040; Height = 760; MinWidth = 820; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(12, 24, 12, 16) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 26) };
        brand.Children.Add(new Image { Source = Branding.AppIcon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        var brandName = Ui.Text("WslDock", 19); brandName.FontWeight = FontWeights.SemiBold; brand.Children.Add(brandName);
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var foot = Ui.Text("WSL 应用 · v" + typeof(App).Assembly.GetName().Version!.ToString(3), 12, "#7B7B7B"); foot.Margin = new Thickness(14, 0, 0, 0); DockPanel.SetDock(foot, Dock.Bottom); sidebar.Children.Add(foot);
        var nav = new StackPanel();
        appsNav = Nav("\uE80A", "配置应用"); keyringNav = Ui.Button("密钥环密码", () => { keyringPage = true; SetNotice(""); Render(); }, true);
        keyringNav.Height = 42; keyringNav.Margin = new Thickness(0, 0, 0, 4);
        keyringNav.HorizontalContentAlignment = HorizontalAlignment.Left;
        var keyringLabel = new StackPanel { Orientation = Orientation.Horizontal, Width = 156 };
        var keyringGlyph = Ui.Glyph("\uE72E", 17); keyringGlyph.Margin = new Thickness(0, 0, 14, 0);
        keyringLabel.Children.Add(keyringGlyph); keyringLabel.Children.Add(Ui.Text("密钥环密码")); keyringNav.Content = keyringLabel;
        nav.Children.Add(appsNav); nav.Children.Add(keyringNav); sidebar.Children.Add(nav);
        grid.Children.Add(new Border { Background = Ui.Brush("#EEEEEE"), Child = sidebar });
        var right = new DockPanel { Margin = new Thickness(32, 24, 32, 24) }; Grid.SetColumn(right, 1);
        notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 14, 0, 0); DockPanel.SetDock(notice, Dock.Bottom); right.Children.Add(notice);
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); grid.Children.Add(right); Content = grid;
        SourceInitialized += (_, _) => Native.Style(this);
        Activated += (_, _) => RefreshStartupToggle();
        Loaded += async (_, _) => { Render(); try { distros = await App.Current.Wsl.DistrosAsync(); selectedDistro = distros.FirstOrDefault() ?? ""; Render(); } catch (Exception ex) { SetNotice(ex.Message, true); } };
    }
    private Button Nav(string glyph, string text)
    {
        var b = Ui.Button("", () => { keyringPage = false; SetNotice(""); Render(); }, true); b.Height = 42; b.Margin = new Thickness(0, 0, 0, 4); b.HorizontalContentAlignment = HorizontalAlignment.Left;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Width = 156 }; var icon = Ui.Glyph(glyph, 17); icon.Margin = new Thickness(0, 0, 14, 0); row.Children.Add(icon); row.Children.Add(Ui.Text(text)); b.Content = row; return b;
    }
    private void SetNotice(string message, bool error = false) { notice.Text = message; notice.Foreground = Ui.Brush(error ? "#B3261E" : "#666666"); }
    private void Render()
    {
        body.Children.Clear(); startupToggle = null;
        keyringNav.Background = Ui.Brush(keyringPage ? "#E1E1E1" : "#EEEEEE");
        appsNav.Background = Ui.Brush(keyringPage ? "#EEEEEE" : "#E1E1E1");
        var heading = Ui.Text(keyringPage ? "密钥环密码" : "配置应用", 28); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(heading);
        var intro = Ui.Text(keyringPage ? "启动 Linux 应用前自动解锁默认密钥环。" : "选择在桌面 Dock 中显示的 WSL 应用。", 13, "#666666"); intro.Margin = new Thickness(0, 0, 0, 26); body.Children.Add(intro);
        if (keyringPage) RenderKeyring(); else RenderApps();
    }
    private void RenderApps()
    {
        RenderStartup();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var refresh = Ui.Button("重新发现应用", () => _ = RefreshApps()); DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh);
        var select = new ComboBox { ItemsSource = distros, SelectedItem = selectedDistro, Width = 190, HorizontalAlignment = HorizontalAlignment.Left };
        select.SelectionChanged += (_, _) => selectedDistro = select.SelectedItem as string ?? ""; toolbar.Children.Add(select); body.Children.Add(toolbar);
        Label("我的应用");
        foreach (var app in App.Current.Prefs.Apps)
        {
            var stack = new StackPanel();
            var row = AppRow(app, out var details);
            var toggle = new CheckBox { Style = (Style)App.Current.FindResource("Toggle"), IsChecked = app.Visible, VerticalAlignment = VerticalAlignment.Center, ToolTip = "在 Dock 中显示" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "在 Dock 中显示 " + app.Name);
            toggle.Checked += (_, _) => { app.Visible = true; Save(); }; toggle.Unchecked += (_, _) => { app.Visible = false; Save(); };
            var edit = new StackPanel { Margin = new Thickness(50, 18, 0, 0), Visibility = Visibility.Collapsed };
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rowActions.Children.Add(toggle);
            var expand = Ui.Button("", () => { edit.Visibility = edit.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed; }, true);
            expand.Content = Ui.Glyph("\uE70D", 11); expand.Width = 26; expand.Height = 30; expand.Margin = new Thickness(12, 0, 0, 0); expand.ToolTip = "启动配置";
            rowActions.Children.Add(expand); Grid.SetColumn(rowActions, 2); row.Children.Add(rowActions); stack.Children.Add(row);
            var name = Field(edit, "显示名称", app.Name);
            var commandCaption = Ui.Text("自定义启动命令", 12, "#666666");
            commandCaption.Margin = new Thickness(0, 0, 0, 5); edit.Children.Add(commandCaption);
            var commandRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            commandRow.ColumnDefinitions.Add(new ColumnDefinition());
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var command = new TextBox { Text = app.Command };
            System.Windows.Automation.AutomationProperties.SetName(command, "自定义启动命令 " + app.Name);
            commandRow.Children.Add(command);
            var custom = new CheckBox { Style = (Style)App.Current.FindResource("Toggle"), IsChecked = app.CustomConfiguration,
                Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "自定义启动命令：开启后，重新发现应用会保留此应用的配置" };
            System.Windows.Automation.AutomationProperties.SetName(custom, "自定义启动命令开关 " + app.Name);
            Grid.SetColumn(custom, 1); commandRow.Children.Add(custom); edit.Children.Add(commandRow);
            var actions = new DockPanel { LastChildFill = false };
            var log = Ui.Button("查看启动日志", () => _ = ReadLog(app), true);
            DockPanel.SetDock(log, Dock.Right); actions.Children.Add(log); edit.Children.Add(actions);
            void UpdateEditing()
            {
                command.IsReadOnly = !app.CustomConfiguration;
                command.Opacity = app.CustomConfiguration ? 1 : 0.65;
            }
            void SaveName()
            {
                if (string.IsNullOrWhiteSpace(name.Text))
                { SetNotice("显示名称不能为空，当前更改尚未保存。", true); return; }
                if (app.Name == name.Text.Trim()) return;
                app.Name = name.Text.Trim(); app.CustomName = true;
                var labels = (StackPanel)row.Children[1]; ((TextBlock)labels.Children[0]).Text = app.Name;
                System.Windows.Automation.AutomationProperties.SetName(custom, "自定义启动命令开关 " + app.Name);
                System.Windows.Automation.AutomationProperties.SetName(command, "自定义启动命令 " + app.Name);
                System.Windows.Automation.AutomationProperties.SetName(toggle, "在 Dock 中显示 " + app.Name);
                Save();
            }
            void SaveCommand()
            {
                if (!app.CustomConfiguration) return;
                if (string.IsNullOrWhiteSpace(command.Text))
                { SetNotice("启动命令不能为空，当前更改尚未保存。", true); return; }
                if (app.Command == command.Text.Trim()) return;
                app.Command = command.Text.Trim(); app.BoundAppId = ""; Save();
            }
            custom.Checked += (_, _) => { app.CustomConfiguration = true; UpdateEditing(); Save(); };
            custom.Unchecked += (_, _) =>
            {
                app.CustomConfiguration = false; command.Text = app.Command;
                UpdateEditing(); Save();
            };
            name.TextChanged += (_, _) => SaveName(); command.TextChanged += (_, _) => SaveCommand();
            UpdateEditing();
            stack.Children.Add(edit); body.Children.Add(Ui.Card(stack));
        }
        if (App.Current.Prefs.Apps.Count == 0) body.Children.Add(Ui.Card(Ui.Text("选择 Ubuntu，然后点击“重新发现应用”。", 13, "#666666")));
        Note("隐藏图标不会关闭应用。启动命令按参数解析；管道、重定向和 shell 脚本请放在你自己的可执行包装器中。");
    }
    private void RenderKeyring()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("WSL 发行版", 13));
        var select = new ComboBox { ItemsSource = distros, SelectedItem = selectedDistro,
            HorizontalAlignment = HorizontalAlignment.Left, Width = 230, Margin = new Thickness(0, 8, 0, 18) };
        System.Windows.Automation.AutomationProperties.SetName(select, "密钥环发行版");
        panel.Children.Add(select);
        var status = Ui.Text("", 12, "#777777"); status.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(status);
        panel.Children.Add(Ui.Text("默认密钥环密码", 13));
        var password = new PasswordBox { MaxLength = 1024, Height = 36, Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 18), HorizontalAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(password, "默认密钥环密码"); panel.Children.Add(password);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Ui.Button("保存密码", () => { });
        var clear = Ui.Button("清除已保存密码", () => { }, true); clear.Margin = new Thickness(8, 0, 0, 0);
        var test = Ui.Button("测试解锁", () => { }, true); test.Margin = new Thickness(8, 0, 0, 0);
        void Update()
        {
            var saved = App.Current.Prefs.KeyringPasswords.ContainsKey(selectedDistro);
            status.Text = saved ? "已保存密码；输入新密码可替换。" : "尚未保存密码";
            save.IsEnabled = selectedDistro.Length > 0;
            clear.IsEnabled = test.IsEnabled = saved;
        }
        select.SelectionChanged += (_, _) => { selectedDistro = select.SelectedItem as string ?? ""; password.Clear(); Update(); };
        save.Click += (_, _) =>
        {
            if (password.Password.Length == 0) { SetNotice("请输入密钥环密码。", true); return; }
            try
            {
                var prefs = App.Current.Prefs;
                prefs.KeyringPasswords.TryGetValue(selectedDistro, out var previous);
                prefs.KeyringPasswords[selectedDistro] = KeyringPassword.Protect(selectedDistro, password.Password);
                try { Config.Save(prefs); }
                catch { if (previous == null) prefs.KeyringPasswords.Remove(selectedDistro); else prefs.KeyringPasswords[selectedDistro] = previous; throw; }
                password.Clear(); Update(); SetNotice("密码已加密保存，启动应用前会自动解锁默认密钥环。");
            }
            catch { SetNotice("密码保存失败，请重试。", true); }
        };
        clear.Click += (_, _) =>
        {
            var prefs = App.Current.Prefs;
            if (!prefs.KeyringPasswords.Remove(selectedDistro, out var previous)) return;
            try { Config.Save(prefs); password.Clear(); Update(); SetNotice("已清除密码，后续由 Linux 显示解锁提示。"); }
            catch { prefs.KeyringPasswords[selectedDistro] = previous; SetNotice("清除失败，请重试。", true); }
        };
        test.Click += async (_, _) =>
        {
            var distro = selectedDistro; test.IsEnabled = false; select.IsEnabled = false;
            SetNotice("正在解锁 " + distro + " 的默认密钥环…");
            try
            {
                var secret = KeyringPassword.Read(App.Current.Prefs, distro)!;
                var result = await App.Current.Wsl.UnlockKeyringAsync(distro, secret);
                SetNotice(result.GetProperty("alreadyUnlocked").GetBoolean() ? "密钥环已经解锁；下次冷启动时会使用保存的密码。" : "默认密钥环已成功解锁。");
            }
            catch { SetNotice("解锁失败。请确认保存的是密钥环密码，且发行版有 GNOME Keyring 和会话 D-Bus。", true); }
            finally { select.IsEnabled = true; Update(); }
        };
        actions.Children.Add(save); actions.Children.Add(clear); actions.Children.Add(test); panel.Children.Add(actions);
        body.Children.Add(Ui.Card(panel)); Update();
        Note("填写 Linux 的 Unlock Keyring 窗口所需的密码。此设置不会修改 Linux 的用户密码或密钥环密码。");
        Note("密码按发行版保存，并由当前 Windows 用户加密保护；不会写入命令行或启动日志。清除密码即可关闭自动解锁。");
        Note("“测试解锁”会启动选中的发行版；若密钥环已经解锁，本次测试无法验证密码是否正确。");
    }

    private void RenderStartup()
    {
        var row = new DockPanel();
        startupToggle = new CheckBox { Style = (Style)App.Current.FindResource("Toggle"), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(startupToggle, "开机自启动");
        DockPanel.SetDock(startupToggle, Dock.Right); row.Children.Add(startupToggle);
        var labels = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        labels.Children.Add(Ui.Text("开机自启动"));
        var caption = Ui.Text("登录 Windows 时自动启动 WslDock", 12, "#777777"); caption.Margin = new Thickness(0, 5, 0, 0); labels.Children.Add(caption);
        row.Children.Add(labels);
        var card = Ui.Card(row); card.Margin = new Thickness(0, 0, 0, 20); body.Children.Add(card);
        RefreshStartupToggle();
        startupToggle.Checked += (_, _) => ChangeStartup(true);
        startupToggle.Unchecked += (_, _) => ChangeStartup(false);
    }
    private void RefreshStartupToggle()
    {
        if (startupToggle == null) return;
        updatingStartup = true;
        try { startupToggle.IsChecked = startup.IsEnabled(); startupToggle.IsEnabled = true; }
        catch (Exception ex) { startupToggle.IsEnabled = false; SetNotice("无法读取自启动设置：" + ex.Message, true); }
        finally { updatingStartup = false; }
    }
    private void ChangeStartup(bool enabled)
    {
        if (updatingStartup) return;
        try { startup.SetEnabled(enabled); SetNotice(enabled ? "已开启开机自启动" : "已关闭开机自启动"); }
        catch (Exception ex) { RefreshStartupToggle(); SetNotice("无法修改自启动设置：" + ex.Message, true); Config.Log(ex.ToString()); }
    }
    private async Task RefreshApps()
    {
        if (selectedDistro.Length == 0) { SetNotice("请选择一个 WSL 发行版。", true); return; }
        SetNotice("正在读取 " + selectedDistro + " 中的应用…");
        try { await App.Current.Discover(selectedDistro); Render(); SetNotice("应用列表已更新。已同步桌面启动器，并保留自定义配置和显示选择。"); }
        catch (Exception ex) { SetNotice(ex.Message, true); Config.Log(ex.ToString()); }
    }
    private static Grid AppRow(DockApp app, out TextBlock details)
    {
        var row = new Grid { MinHeight = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Icon(app); icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) }; labels.Children.Add(Ui.Text(app.Name));
        details = Ui.Text(app.Distro, 12, "#777777"); details.Margin = new Thickness(0, 4, 0, 0); labels.Children.Add(details); Grid.SetColumn(labels, 1); row.Children.Add(labels); return row;
    }
    private static TextBox Field(StackPanel panel, string label, string value)
    {
        var caption = Ui.Text(label, 12, "#666666"); caption.Margin = new Thickness(0, 0, 0, 5); panel.Children.Add(caption);
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(input); return input;
    }
    private void Label(string text) { var label = Ui.Text(text, 13); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 19, 0, 12); body.Children.Add(label); }
    private void Note(string text) { var note = Ui.Text(text, 12, "#777777"); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 14, 0, 0); note.LineHeight = 20; body.Children.Add(note); }
    private void Save() { try { App.Current.Save(); SetNotice("已保存"); } catch (Exception ex) { SetNotice("无法保存设置：" + ex.Message, true); } }
    private async Task ReadLog(DockApp app)
    {
        try
        {
            var result = await App.Current.Wsl.RequestAsync(app.Distro, new { action = "log", id = app.Id });
            var text = new TextBox { Text = result.GetProperty("text").GetString(), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
            new Window { Title = app.Name + " · 启动日志", Width = 760, Height = 460, Owner = this, Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
        }
        catch (Exception ex) { SetNotice(ex.Message, true); }
    }
}
