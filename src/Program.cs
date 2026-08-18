using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace SurgeGuestInformationKiosk;

internal static class Program
{
    private const string MutexName = "SurgeMobileEventKiosk.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Velopack must run before any UI is created so installation and update
        // hooks can finish without opening the kiosk window.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show(
                "The guest information kiosk is already running.",
                "Surge Guest Information Kiosk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        KioskUpdater.ApplyAvailableUpdateOnStartup();
        StartupShortcutMigration.PreserveStartupPreference();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var settings = KioskSettings.LoadOrCreate();
            if (settings is null)
                return;

            Application.Run(new KioskForm(settings));
        }
        catch (Exception ex)
        {
            KioskLog.Write("Fatal startup error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(
                "The guest information kiosk could not start.\n\n" + ex.Message,
                "Surge Guest Information Kiosk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal enum KioskUpdateStatus
{
    UpToDate,
    Applying,
    NotConfigured,
    NotInstalled,
    Failed
}

internal sealed record KioskUpdateResult(KioskUpdateStatus Status, string Message);

internal static class KioskUpdater
{
    private const string RepositoryMetadataKey = "UpdateRepositoryUrl";

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? "Unknown"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private static string RepositoryUrl =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, RepositoryMetadataKey, StringComparison.Ordinal))?
            .Value?.Trim() ?? string.Empty;

    public static void ApplyAvailableUpdateOnStartup()
    {
        try
        {
            var result = CheckDownloadAndApplyAsync().GetAwaiter().GetResult();
            KioskLog.Write("Automatic update check: " + result.Message);
        }
        catch (Exception ex)
        {
            // A failed update check must never prevent the built-in guest kiosk
            // from opening.
            KioskLog.Write("Automatic update error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    public static async Task<KioskUpdateResult> CheckDownloadAndApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotConfigured,
                "This build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return new KioskUpdateResult(
                    KioskUpdateStatus.UpToDate,
                    $"Version {CurrentVersion} is up to date.");
            }

            await manager.DownloadUpdatesAsync(update);
            KioskLog.Write("A kiosk update was downloaded and is being applied.");
            manager.ApplyUpdatesAndRestart(update);
            return new KioskUpdateResult(
                KioskUpdateStatus.Applying,
                "The update is installing. The kiosk will restart automatically.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotInstalled,
                "Automatic updates begin after the kiosk is installed with the Setup file.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Kiosk update error: " + ex.GetType().Name + " - " + ex.Message);
            return new KioskUpdateResult(
                KioskUpdateStatus.Failed,
                "The update check failed. Verify the internet connection and try again.");
        }
    }
}

internal static class StartupShortcutMigration
{
    public static void PreserveStartupPreference()
    {
        try
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var oldPath = Path.Combine(startupFolder, "Surge Mobile Event Kiosk.lnk");
            var newPath = Path.Combine(startupFolder, "Surge Guest Information Kiosk.lnk");
            var executablePath = Environment.ProcessPath;

            if ((!File.Exists(oldPath) && !File.Exists(newPath)) ||
                string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return;

            dynamic shortcut = shell.CreateShortcut(newPath);
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            shortcut.Description = "Automatically start the Surge guest information kiosk";
            shortcut.Save();

            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);

            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                File.Delete(oldPath);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Startup shortcut migration error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
    }
}

internal sealed class KioskForm : Form
{
    private const int WmHotKey = 0x0312;
    private const int StaffHotKeyId = 0x5347;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF12 = 0x7B;

    private readonly KioskSettings _settings;
    private readonly WebView2 _webView = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 1000 };

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _browserReady;
    private bool _allowExit;
    private bool _promptOpen;
    private bool _hotKeyRegistered;
    private bool _showingClosedPage;

    public KioskForm(KioskSettings settings)
    {
        _settings = settings;

        Text = "Surge Guest Information Kiosk";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(251, 245, 255);

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(251, 245, 255);
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeBrowserAsync();
        FormClosing += KioskForm_FormClosing;
        KeyDown += KioskForm_KeyDown;
        Deactivate += (_, _) =>
        {
            if (!_promptOpen)
                Activate();
        };

        _idleTimer.Tick += (_, _) =>
        {
            if (!_browserReady || _promptOpen || _showingClosedPage)
                return;

            var idleFor = DateTime.UtcNow - _lastActivityUtc;
            if (idleFor.TotalSeconds >= Math.Max(30, _settings.IdleResetSeconds))
                ShowGuestKiosk("inactivity reset");
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotKeyRegistered = RegisterHotKey(
            Handle,
            StaffHotKeyId,
            ModControl | ModAlt | ModShift,
            VkF12);
        if (!_hotKeyRegistered)
            KioskLog.Write("The staff hotkey could not be registered.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotKeyRegistered)
            UnregisterHotKey(Handle, StaffHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotKey && message.WParam.ToInt32() == StaffHotKeyId)
        {
            _ = OpenStaffSettingsAsync();
            return;
        }

        base.WndProc(ref message);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var webViewDataPath = Path.Combine(KioskSettings.DataFolder, "WebView2");
            Directory.CreateDirectory(webViewDataPath);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: webViewDataPath);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += Core_NavigationStarting;
            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    if (string.Equals(args.TryGetWebMessageAsString(), "activity", StringComparison.Ordinal))
                        MarkActivity();
                }
                catch
                {
                    // Ignore messages that are not plain strings.
                }
            };
            core.ProcessFailed += (_, _) =>
            {
                if (!_promptOpen)
                    BeginInvoke(new Action(() => ShowGuestKiosk("browser recovery")));
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(ActivityScript);
            _browserReady = true;
            _idleTimer.Start();

            if (_settings.StationClosed)
                ShowClosedPage();
            else
                ShowGuestKiosk("startup");
        }
        catch (Exception ex)
        {
            KioskLog.Write("WebView initialization error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(
                "Microsoft Edge WebView2 is required to run the kiosk. Install or repair WebView2 and try again.\n\n" + ex.Message,
                "Surge Guest Information Kiosk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _allowExit = true;
            Close();
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        MarkActivity();

        if (e.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        // The guest kiosk is intentionally self-contained. Attraction links are
        // informational citations and external browsing is disabled in kiosk mode.
        e.Cancel = true;
    }

    private void ShowGuestKiosk(string reason)
    {
        if (!_browserReady)
            return;

        _showingClosedPage = false;
        _lastActivityUtc = DateTime.UtcNow;
        _webView.CoreWebView2.NavigateToString(ReadEmbeddedText("GuestKiosk.html"));
        KioskLog.Write("Guest kiosk loaded: " + reason + ".");
    }

    private void ShowClosedPage()
    {
        if (!_browserReady)
            return;

        _showingClosedPage = true;
        _webView.CoreWebView2.NavigateToString(BuildClosedPageHtml());
        KioskLog.Write("Staff closed page displayed.");
    }

    private async Task OpenStaffSettingsAsync()
    {
        if (_promptOpen)
            return;

        _promptOpen = true;
        TopMost = false;

        try
        {
            using var pinDialog = new PinDialog(
                "Staff Settings",
                "Enter the 4–8 digit staff password.",
                requireConfirmation: false);

            if (pinDialog.ShowDialog(this) != DialogResult.OK ||
                !_settings.VerifyPin(pinDialog.Pin))
            {
                if (pinDialog.DialogResult == DialogResult.OK)
                {
                    MessageBox.Show(
                        "That password is not correct.",
                        "Staff Settings",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            using var menu = new StaffMenuDialog(_settings.StationClosed, KioskUpdater.CurrentVersion);
            if (menu.ShowDialog(this) != DialogResult.OK)
                return;

            switch (menu.SelectedAction)
            {
                case StaffMenuAction.Return:
                    break;

                case StaffMenuAction.ResetGuestScreen:
                    _settings.StationClosed = false;
                    _settings.Save();
                    ShowGuestKiosk("staff reset");
                    break;

                case StaffMenuAction.ToggleClosed:
                    _settings.StationClosed = !_settings.StationClosed;
                    _settings.Save();
                    if (_settings.StationClosed)
                        ShowClosedPage();
                    else
                        ShowGuestKiosk("station reopened");
                    break;

                case StaffMenuAction.CheckForUpdate:
                    Cursor = Cursors.WaitCursor;
                    var result = await KioskUpdater.CheckDownloadAndApplyAsync();
                    Cursor = Cursors.Default;
                    MessageBox.Show(
                        result.Message,
                        "Kiosk Update",
                        MessageBoxButtons.OK,
                        result.Status == KioskUpdateStatus.Failed
                            ? MessageBoxIcon.Warning
                            : MessageBoxIcon.Information);
                    break;

                case StaffMenuAction.ChangePassword:
                    using (var changeDialog = new PinDialog(
                        "Change Staff Password",
                        "Enter and confirm a new 4–8 digit staff password.",
                        requireConfirmation: true))
                    {
                        if (changeDialog.ShowDialog(this) == DialogResult.OK)
                        {
                            _settings.SetPin(changeDialog.Pin);
                            _settings.Save();
                            MessageBox.Show(
                                "The staff password was changed.",
                                "Staff Settings",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                    break;

                case StaffMenuAction.Exit:
                    _allowExit = true;
                    Close();
                    break;
            }
        }
        finally
        {
            Cursor = Cursors.Default;
            _promptOpen = false;
            TopMost = true;
            if (!_allowExit)
            {
                Activate();
                MarkActivity();
            }
        }
    }

    private void KioskForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Alt && e.KeyCode == Keys.F4)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }

        if (e.KeyCode is Keys.F5 or Keys.F11 ||
            (e.Control && e.KeyCode is Keys.L or Keys.N or Keys.O or Keys.P or Keys.R or Keys.S or Keys.T or Keys.U or Keys.W))
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }

    private void KioskForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
    }

    private void MarkActivity() => _lastActivityUtc = DateTime.UtcNow;

    private static string ReadEmbeddedText(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded kiosk resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string BuildClosedPageHtml()
    {
        var logoBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReadEmbeddedText("SurgeWordmarkWhite.svg")));
        return """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>Surge Guest Information Kiosk Closed</title>
              <style>
                :root{color-scheme:dark}*{box-sizing:border-box}html,body{height:100%}body{align-items:center;background:radial-gradient(circle at 75% 20%,rgba(183,68,255,.42),transparent 30%),linear-gradient(135deg,#0b0023,#31055e 62%,#7300bd);color:#fff;display:flex;font-family:Arial,Helvetica,sans-serif;justify-content:center;margin:0;overflow:hidden;padding:42px}.shell{max-width:960px;text-align:center;width:100%}.logo{height:auto;margin-bottom:54px;max-width:430px;width:58%}.card{background:rgba(255,255,255,.11);border:1px solid rgba(255,255,255,.28);border-radius:32px;box-shadow:0 28px 90px rgba(0,0,0,.3);padding:60px 70px}.status{color:#acd037;font-size:.82rem;font-weight:900;letter-spacing:.18em;text-transform:uppercase}.icon{align-items:center;background:#ff8a3c;border-radius:24px;color:#0b0023;display:flex;font-size:3rem;font-weight:900;height:96px;justify-content:center;margin:26px auto;width:96px}h1{font-size:clamp(2.5rem,6vw,5.4rem);letter-spacing:-.055em;line-height:.98;margin:0}p{color:#f1e8f6;font-size:clamp(1.15rem,2vw,1.55rem);line-height:1.6;margin:28px auto 0;max-width:710px}.help{background:#acd037;border-radius:18px;color:#0b0023;font-size:1.1rem;font-weight:900;margin:36px auto 0;padding:18px 24px;max-width:620px}@media(max-width:700px){body{padding:20px}.card{padding:40px 24px}.logo{margin-bottom:34px;width:75%}}
              </style>
            </head>
            <body>
              <main class="shell">
                <img class="logo" src="data:image/svg+xml;base64,@@LOGO@@" alt="Surge Entertainment">
                <section class="card">
                  <div class="status">Temporarily unavailable</div>
                  <div class="icon">!</div>
                  <h1>Guest Information Kiosk Closed</h1>
                  <p>This guest information kiosk is currently closed.</p>
                  <div class="help">Please see a staff member at the front desk for assistance.</div>
                </section>
              </main>
            </body>
            </html>
            """.Replace("@@LOGO@@", logoBase64, StringComparison.Ordinal);
    }

    private const string ActivityScript = """
        (() => {
          let lastSent = 0;
          const activity = () => {
            const now = Date.now();
            if (now - lastSent < 600) return;
            lastSent = now;
            window.chrome?.webview?.postMessage('activity');
          };
          ['pointerdown','pointermove','keydown','scroll','touchstart','input','change']
            .forEach(name => window.addEventListener(name, activity, {capture:true, passive:true}));
        })();
        """;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal sealed class KioskSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SurgeMobileEventKiosk",
        "Data");

    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public string StaffPinSalt { get; set; } = string.Empty;
    public string StaffPinHash { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public int IdleResetSeconds { get; set; } = 180;

    public static KioskSettings? LoadOrCreate()
    {
        Directory.CreateDirectory(DataFolder);
        KioskSettings settings;

        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<KioskSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                    ?? new KioskSettings()
                : new KioskSettings();
        }
        catch (Exception ex)
        {
            KioskLog.Write("Settings load error: " + ex.GetType().Name + " - " + ex.Message);
            settings = new KioskSettings();
        }

        settings.IdleResetSeconds = Math.Clamp(settings.IdleResetSeconds, 30, 3600);

        if (string.IsNullOrWhiteSpace(settings.StaffPinSalt) ||
            string.IsNullOrWhiteSpace(settings.StaffPinHash))
        {
            using var dialog = new PinDialog(
                "Create Staff Password",
                "Create and confirm a 4–8 digit staff password for this kiosk.",
                requireConfirmation: true);
            if (dialog.ShowDialog() != DialogResult.OK)
                return null;

            settings.SetPin(dialog.Pin);
        }

        settings.Save();
        return settings;
    }

    public bool VerifyPin(string pin)
    {
        try
        {
            var salt = Convert.FromBase64String(StaffPinSalt);
            var expected = Convert.FromBase64String(StaffPinHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                pin,
                salt,
                150_000,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    public void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            150_000,
            HashAlgorithmName.SHA256,
            32);
        StaffPinSalt = Convert.ToBase64String(salt);
        StaffPinHash = Convert.ToBase64String(hash);
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}

internal enum StaffMenuAction
{
    Return,
    ResetGuestScreen,
    ToggleClosed,
    CheckForUpdate,
    ChangePassword,
    Exit
}

internal sealed class StaffMenuDialog : Form
{
    public StaffMenuAction SelectedAction { get; private set; } = StaffMenuAction.Return;

    public StaffMenuDialog(bool stationClosed, string version)
    {
        Text = "Surge Guest Information Kiosk — Staff Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(251, 245, 255);
        ClientSize = new Size(520, 540);
        Font = new Font("Segoe UI", 10);

        var title = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(32, 24, 456, 44),
            Text = "Staff Settings",
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 0, 35)
        };

        var status = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(34, 73, 452, 54),
            Text = $"Version {version}  •  Guest content is built in\nStation: {(stationClosed ? "Closed" : "Open")}",
            ForeColor = Color.FromArgb(95, 87, 108)
        };

        var panel = new FlowLayoutPanel
        {
            Bounds = new Rectangle(32, 135, 456, 330),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };

        panel.Controls.Add(CreateButton("Return to kiosk", StaffMenuAction.Return, primary: true));
        panel.Controls.Add(CreateButton("Reset guest screen", StaffMenuAction.ResetGuestScreen));
        panel.Controls.Add(CreateButton(
            stationClosed ? "Open guest kiosk" : "Close guest kiosk",
            StaffMenuAction.ToggleClosed));
        panel.Controls.Add(CreateButton("Check for kiosk update", StaffMenuAction.CheckForUpdate));
        panel.Controls.Add(CreateButton("Change staff password", StaffMenuAction.ChangePassword));
        panel.Controls.Add(CreateButton("Exit kiosk", StaffMenuAction.Exit, danger: true));

        var hint = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(34, 484, 452, 34),
            Text = "Staff shortcut: Ctrl + Alt + Shift + F12",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(95, 87, 108)
        };

        Controls.Add(title);
        Controls.Add(status);
        Controls.Add(panel);
        Controls.Add(hint);
    }

    private Button CreateButton(
        string text,
        StaffMenuAction action,
        bool primary = false,
        bool danger = false)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(448, 47),
            Margin = new Padding(0, 0, 0, 8),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            BackColor = primary
                ? Color.FromArgb(183, 68, 255)
                : danger
                    ? Color.FromArgb(255, 232, 235)
                    : Color.White,
            ForeColor = primary
                ? Color.White
                : danger
                    ? Color.FromArgb(155, 28, 52)
                    : Color.FromArgb(11, 0, 35)
        };
        button.FlatAppearance.BorderColor = primary
            ? Color.FromArgb(183, 68, 255)
            : danger
                ? Color.FromArgb(235, 170, 183)
                : Color.FromArgb(224, 215, 234);
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            DialogResult = DialogResult.OK;
            Close();
        };
        return button;
    }
}

internal sealed class PinDialog : Form
{
    private readonly TextBox _pinBox = new();
    private readonly TextBox? _confirmBox;

    public string Pin => _pinBox.Text;

    public PinDialog(string title, string prompt, bool requireConfirmation)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        ClientSize = new Size(460, requireConfirmation ? 300 : 240);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(28, 24, 404, 32),
            Text = title,
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 0, 35)
        };
        var instructions = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(30, 65, 400, 44),
            Text = prompt,
            ForeColor = Color.FromArgb(95, 87, 108)
        };

        ConfigurePinBox(_pinBox);
        _pinBox.Bounds = new Rectangle(30, 115, 400, 38);

        Controls.Add(heading);
        Controls.Add(instructions);
        Controls.Add(_pinBox);

        var buttonY = requireConfirmation ? 235 : 175;
        if (requireConfirmation)
        {
            _confirmBox = new TextBox();
            ConfigurePinBox(_confirmBox);
            _confirmBox.Bounds = new Rectangle(30, 169, 400, 38);
            _confirmBox.PlaceholderText = "Confirm staff password";
            Controls.Add(_confirmBox);
        }

        var cancelButton = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(230, buttonY, 95, 40),
            DialogResult = DialogResult.Cancel
        };
        var continueButton = new Button
        {
            Text = "Continue",
            Bounds = new Rectangle(335, buttonY, 95, 40),
            BackColor = Color.FromArgb(183, 68, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        continueButton.FlatAppearance.BorderColor = Color.FromArgb(183, 68, 255);
        continueButton.Click += (_, _) => ValidateAndClose();

        Controls.Add(cancelButton);
        Controls.Add(continueButton);
        AcceptButton = continueButton;
        CancelButton = cancelButton;
        Shown += (_, _) => _pinBox.Focus();
    }

    private static void ConfigurePinBox(TextBox box)
    {
        box.UseSystemPasswordChar = true;
        box.MaxLength = 8;
        box.PlaceholderText = "Staff password";
        box.Font = new Font("Segoe UI", 13);
        box.TextAlign = HorizontalAlignment.Center;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
    }

    private void ValidateAndClose()
    {
        if (_pinBox.Text.Length is < 4 or > 8 || !_pinBox.Text.All(char.IsDigit))
        {
            MessageBox.Show(
                "Use a 4–8 digit numerical password.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _pinBox.Focus();
            return;
        }

        if (_confirmBox is not null && !string.Equals(_pinBox.Text, _confirmBox.Text, StringComparison.Ordinal))
        {
            MessageBox.Show(
                "The passwords do not match.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _confirmBox.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

internal static class KioskLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(KioskSettings.DataFolder);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(Path.Combine(KioskSettings.DataFolder, "kiosk.log"), line);
            }
        }
        catch
        {
            // Logging must never interrupt the kiosk.
        }
    }
}
