using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Velopack;
using Velopack.Sources;

namespace SurgeMobileEventKiosk;

internal static class Program
{
    private const string MutexName = "SurgeMobileEventKiosk.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Velopack must be the first application code that runs so install,
        // update, and uninstall hooks can complete without opening kiosk UI.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("The event kiosk is already running.", "Surge Mobile Event Kiosk",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        KioskUpdater.ApplyAvailableUpdateOnStartup();
        LegacyInstallationMigration.PreserveStartupPreference();

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
                "The event kiosk could not start.\n\n" + ex.Message +
                "\n\nSee README.txt for repair instructions.",
                "Surge Mobile Event Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            // An unavailable update service must never prevent guests from using
            // the event. Staff can retry from Staff Settings.
            KioskLog.Write("Automatic update check error: " +
                ex.GetType().Name + " - " + ex.Message);
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
                "Automatic updates begin after the kiosk is installed with the Velopack Setup file.");
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

internal static class LegacyInstallationMigration
{
    public static void PreserveStartupPreference()
    {
        try
        {
            var startupShortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Surge Mobile Event Kiosk.lnk");
            var executablePath = Environment.ProcessPath;
            if (!File.Exists(startupShortcutPath) || string.IsNullOrWhiteSpace(executablePath))
                return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return;

            dynamic shortcut = shell.CreateShortcut(startupShortcutPath);
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            shortcut.Description = "Automatically start the Surge Mobile event kiosk";
            shortcut.Save();

            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            KioskLog.Write("The existing Windows startup preference was migrated to the updateable kiosk.");
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
    private const int StaffExitHotKeyId = 0x4D48;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF12 = 0x7B;
    private const string AdvertisementVirtualHost = "surgemobile-ads.local";
    private const string LogoUrl =
        "https://surgefun.com/wp-content/uploads/2025/03/cropped-Logo_Surge-copy-scaled-1-270x270.webp";

    private readonly KioskSettings _settings;
    private readonly WebView2 _webView = new();
    private readonly Label _banner = new();
    private readonly Label _previewBanner = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _completionTimer = new();
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 15000 };

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _allowExit;
    private bool _promptOpen;
    private bool _isResetting;
    private bool _browserReady;
    private bool _hotKeyRegistered;
    private bool _showingThankYouPage;
    private bool _showingClosedPage;
    private string? _pendingSwitchEmail;
    private string? _pendingSwitchChoice;
    private string? _lastEventEmail;
    private string? _lastEventChoice;
    private DateTime? _previewDateTime;
    private DateTime? _previewStartedUtc;
    private string? _dateTimePreviewScriptId;

    public KioskForm(KioskSettings settings)
    {
        _settings = settings;

        Text = "Surge Mobile Event Kiosk";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.White;

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.White;

        _banner.Dock = DockStyle.Top;
        _banner.Height = 48;
        _banner.Padding = new Padding(14, 0, 14, 0);
        _banner.TextAlign = ContentAlignment.MiddleCenter;
        _banner.Font = new Font("Segoe UI", 13, FontStyle.Bold);
        _banner.BackColor = Color.FromArgb(255, 222, 89);
        _banner.ForeColor = Color.FromArgb(32, 32, 32);
        _banner.Visible = false;

        _previewBanner.Dock = DockStyle.Bottom;
        _previewBanner.Height = 44;
        _previewBanner.Padding = new Padding(14, 0, 14, 0);
        _previewBanner.TextAlign = ContentAlignment.MiddleCenter;
        _previewBanner.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _previewBanner.BackColor = Color.FromArgb(183, 68, 255);
        _previewBanner.ForeColor = Color.White;
        _previewBanner.Visible = false;

        Controls.Add(_webView);
        Controls.Add(_banner);
        Controls.Add(_previewBanner);
        _banner.BringToFront();
        _previewBanner.BringToFront();

        _idleTimer.Tick += IdleTimer_Tick;
        _completionTimer.Interval = Math.Max(12, _settings.CompletionResetSeconds) * 1000;
        _completionTimer.Tick += async (_, _) =>
        {
            _completionTimer.Stop();
            await ResetForNextGuestAsync("completion");
        };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            if (_browserReady && !_isResetting && !_settings.StationClosed)
            {
                _showingClosedPage = false;
                _webView.CoreWebView2.Navigate(_settings.StartUrl);
            }
        };

        Shown += async (_, _) =>
        {
            if (!_hotKeyRegistered)
            {
                _allowExit = true;
                MessageBox.Show(
                    "The staff settings shortcut could not be registered. Close any program using Ctrl + Alt + Shift + F12, then start the kiosk again.",
                    "Surge Mobile Event Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            Activate();
            await InitializeBrowserAsync();
        };

        Deactivate += (_, _) =>
        {
            if (!_promptOpen && !_allowExit)
                BeginInvoke(() => { TopMost = true; Activate(); _webView.Focus(); });
        };

        FormClosing += (_, e) =>
        {
            if (!_allowExit)
                e.Cancel = true;
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotKeyRegistered = RegisterHotKey(Handle, StaffExitHotKeyId,
            ModControl | ModAlt | ModShift, VkF12);

        if (!_hotKeyRegistered)
            KioskLog.Write("Unable to register the staff settings hotkey.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotKeyRegistered)
            UnregisterHotKey(Handle, StaffExitHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam.ToInt32() == StaffExitHotKeyId)
        {
            ShowStaffExitPrompt();
            return;
        }
        base.WndProc(ref m);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(KioskSettings.DataDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions("--disable-features=msEdgeSidebarV2"));

            await _webView.EnsureCoreWebView2Async(environment);
            ConfigureBrowser();
            var eventPageScript = ActivityAndCompletionScript.Replace(
                "__SURGE_MOBILE_LOGO_DATA_URL__",
                GetOfficialWordmarkDataUrl(),
                StringComparison.Ordinal);
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(eventPageScript);

            _browserReady = true;
            _lastActivityUtc = DateTime.UtcNow;
            _idleTimer.Start();
            if (_settings.StationClosed)
                ShowStationClosedPage(connectionError: false);
            else
                _webView.CoreWebView2.Navigate(_settings.StartUrl);
            _webView.Focus();
            KioskLog.Write("Kiosk started.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException(
                "Microsoft Edge WebView2 Runtime is missing. Install the Evergreen WebView2 Runtime, then start the kiosk again.");
        }
    }

    private void ConfigureBrowser()
    {
        var core = _webView.CoreWebView2;
        var browserSettings = core.Settings;

        browserSettings.AreDefaultContextMenusEnabled = false;
        browserSettings.AreDevToolsEnabled = false;
        browserSettings.AreBrowserAcceleratorKeysEnabled = false;
        browserSettings.IsStatusBarEnabled = false;
        browserSettings.IsZoomControlEnabled = false;
        browserSettings.IsPinchZoomEnabled = false;
        browserSettings.IsSwipeNavigationEnabled = false;
        browserSettings.AreHostObjectsAllowed = false;
        browserSettings.IsBuiltInErrorPageEnabled = false;

        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;

        Directory.CreateDirectory(KioskSettings.AdvertisementsDirectory);
        core.SetVirtualHostNameToFolderMapping(
            AdvertisementVirtualHost,
            KioskSettings.AdvertisementsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.NavigationStarting += Core_NavigationStarting;
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!IsAllowedUri(e.Uri))
                e.Cancel = true;
        };
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += async (_, _) =>
        {
            KioskLog.Write("Web content process failed; resetting.");
            await ResetForNextGuestAsync("browser process recovery");
        };

    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        MarkActivity();
        _retryTimer.Stop();

        if (IsInternalKioskPageUri(e.Uri))
            return;

        if (IsAllowedUri(e.Uri))
        {
            if (IsCompletionUri(e.Uri))
            {
                e.Cancel = true;
                BeginInvoke(new Action(ShowThankYouPage));
            }
            return;
        }

        e.Cancel = true;
        ShowBanner("For guest safety, this kiosk only opens the Surge event request site.", false);
        KioskLog.Write("Blocked navigation outside the Surge event request site.");
    }

    private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        MarkActivity();

        if (_showingThankYouPage || _showingClosedPage)
            return;

        if (!e.IsSuccess || e.HttpStatusCode >= 400)
        {
            KioskLog.Write($"Event navigation failed: {e.WebErrorStatus}; HTTP {e.HttpStatusCode}.");
            ShowStationClosedPage(connectionError: true);
            return;
        }

        HideBanner();
        var current = _webView.Source?.ToString() ?? string.Empty;
        if (IsCompletionUri(current))
        {
            ShowThankYouPage();
            return;
        }

        await RestoreRememberedEventContextAsync();
        await ApplyPendingEventSwitchAsync();
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsAllowedUri(e.Uri))
            _webView.CoreWebView2.Navigate(e.Uri);
        else
        {
            ShowBanner("Outside websites are blocked on this event kiosk.", false);
            KioskLog.Write("Blocked pop-up outside the Surge event request site.");
        }
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        if (message == "activity")
            MarkActivity();
        else if (message == "completion-text")
            ShowThankYouPage();
        else if (message == "reset-event")
            _ = ResetForNextGuestAsync("guest reset button");
        else if (message == "switch-event-reset")
            _ = ResetForNextGuestAsync("event type change", showStatus: false);
        else if (message.StartsWith('{'))
        {
            try
            {
                using var payload = JsonDocument.Parse(message);
                var root = payload.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var type = typeElement.GetString();
                if (type == "remember-event-choice")
                {
                    var email = root.TryGetProperty("email", out var emailElement)
                        ? (emailElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    var choice = root.TryGetProperty("choice", out var choiceElement)
                        ? (choiceElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                        : string.Empty;
                    if (email.Length is >= 3 and <= 254 && email.Contains('@') &&
                        (choice == "just-me" || choice == "family"))
                    {
                        _lastEventEmail = email;
                        _lastEventChoice = choice;
                    }
                }
                else if (type == "switch-event-option")
                {
                    var email = root.TryGetProperty("email", out var emailElement)
                        ? emailElement.GetString() ?? string.Empty
                        : string.Empty;
                    var choice = root.TryGetProperty("choice", out var choiceElement)
                        ? choiceElement.GetString() ?? string.Empty
                        : string.Empty;
                    _ = RestartWithAlternateChoiceAsync(email, choice);
                }
                else if (type == "switch-applied")
                {
                    _pendingSwitchEmail = null;
                    _pendingSwitchChoice = null;
                    KioskLog.Write("Alternate event type selected automatically.");
                }
                else if (type == "switch-failed")
                {
                    _pendingSwitchEmail = null;
                    _pendingSwitchChoice = null;
                    KioskLog.Write("Automatic event-type selection was not available.");
                }
            }
            catch (JsonException)
            {
                KioskLog.Write("Ignored an invalid message from the event page.");
            }
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_browserReady || _promptOpen || _isResetting || _completionTimer.Enabled || _showingClosedPage)
            return;

        var idleFor = DateTime.UtcNow - _lastActivityUtc;
        if (idleFor >= TimeSpan.FromMinutes(Math.Max(1, _settings.IdleTimeoutMinutes)))
            _ = ResetForNextGuestAsync("inactivity");
    }

    private void MarkActivity() => _lastActivityUtc = DateTime.UtcNow;

    private bool IsAllowedUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var hostAllowed = _settings.AllowedHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
        if (!hostAllowed)
            return false;

        return _settings.AllowedPathPrefixes.Any(prefix =>
            uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsInternalKioskPageUri(string? value)
    {
        if ((!_showingThankYouPage && !_showingClosedPage) || string.IsNullOrWhiteSpace(value))
            return false;

        return string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCompletionUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        var partToCheck = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        return _settings.CompletionUrlKeywords.Any(keyword =>
            partToCheck.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private void ShowThankYouPage() => ShowThankYouPage(staffPreview: false, scheduleTimeOverride: null);

    private void ShowThankYouPage(bool staffPreview, DateTime? scheduleTimeOverride)
    {
        if (!_browserReady || _isResetting || _settings.StationClosed ||
            (_showingThankYouPage && !staffPreview))
            return;

        _showingThankYouPage = true;
        _showingClosedPage = false;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        _webView.CoreWebView2.NavigateToString(BuildThankYouHtml(scheduleTimeOverride));
        _completionTimer.Start();
        KioskLog.Write(staffPreview
            ? "Staff preview of the branded thank-you page displayed for " +
                (scheduleTimeOverride ?? GetEffectiveNow()).ToString("O") + "."
            : "Event completion detected; branded thank-you page displayed.");
    }

    private async Task ResetForNextGuestAsync(string reason, bool showStatus = true)
    {
        if (!_browserReady || _isResetting)
            return;

        _isResetting = true;
        _pendingSwitchEmail = null;
        _pendingSwitchChoice = null;
        _lastEventEmail = null;
        _lastEventChoice = null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        if (showStatus)
            ShowBanner("Preparing a fresh event request…", true);
        else
            HideBanner();

        try
        {
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            _showingThankYouPage = false;
            _showingClosedPage = false;
            if (_settings.StationClosed)
                ShowStationClosedPage(connectionError: false);
            else
                _webView.CoreWebView2.Navigate(_settings.StartUrl);
            _lastActivityUtc = DateTime.UtcNow;
            KioskLog.Write("Event reset: " + reason + ".");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Reset error: " + ex.GetType().Name + " - " + ex.Message);
            ShowStationClosedPage(connectionError: !_settings.StationClosed);
        }
        finally
        {
            _isResetting = false;
        }
    }

    private async Task RestartWithAlternateChoiceAsync(string email, string choice)
    {
        email = email.Trim();
        choice = choice.Trim().ToLowerInvariant();
        if (!_browserReady || _isResetting || email.Length is < 3 or > 254 || !email.Contains('@') ||
            (choice != "just-me" && choice != "family"))
        {
            KioskLog.Write("Ignored an incomplete event-type switch request.");
            return;
        }

        _isResetting = true;
        _pendingSwitchEmail = email;
        _pendingSwitchChoice = choice;
        _lastEventEmail = email;
        _lastEventChoice = choice;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();

        try
        {
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            _showingThankYouPage = false;
            _showingClosedPage = false;
            _webView.CoreWebView2.Navigate(_settings.StartUrl);
            _lastActivityUtc = DateTime.UtcNow;
            KioskLog.Write("Restarting the event with the alternate guest option.");
        }
        catch (Exception ex)
        {
            _pendingSwitchEmail = null;
            _pendingSwitchChoice = null;
            KioskLog.Write("Event switch error: " + ex.GetType().Name + " - " + ex.Message);
            _retryTimer.Start();
        }
        finally
        {
            _isResetting = false;
        }
    }

    private async Task ApplyPendingEventSwitchAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingSwitchEmail) || string.IsNullOrWhiteSpace(_pendingSwitchChoice))
            return;

        var emailJson = JsonSerializer.Serialize(_pendingSwitchEmail);
        var choiceJson = JsonSerializer.Serialize(_pendingSwitchChoice);
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__surgeMobileApplyEventSwitch?.({emailJson}, {choiceJson});");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Event switch script error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private async Task RestoreRememberedEventContextAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastEventEmail) || string.IsNullOrWhiteSpace(_lastEventChoice))
            return;

        var emailJson = JsonSerializer.Serialize(_lastEventEmail);
        var choiceJson = JsonSerializer.Serialize(_lastEventChoice);
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__surgeMobileSetEventContext?.({emailJson}, {choiceJson});");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Event context restore error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private void ShowStationClosedPage(bool connectionError)
    {
        if (!_browserReady)
            return;

        _showingThankYouPage = false;
        _showingClosedPage = true;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        _webView.CoreWebView2.NavigateToString(BuildStationClosedHtml(connectionError));

        if (connectionError && !_settings.StationClosed)
            _retryTimer.Start();

        KioskLog.Write(connectionError
            ? "The branded connection-closed page was displayed; the event site will be retried automatically."
            : "The staff-controlled event request station closed page was displayed.");
    }

    private static string BuildStationClosedHtml(bool connectionError)
    {
        var logoDataUrl = GetApplicationLogoDataUrl();
        var logoMarkup = string.IsNullOrWhiteSpace(logoDataUrl)
            ? "<div class=\"logo-fallback\">SURGE</div>"
            : $"<img class=\"brand-logo\" src=\"{logoDataUrl}\" alt=\"Surge Entertainment logo\">";
        var statusMarkup = connectionError
            ? """
                <section class="message connection-message">
                  <span class="message-label">CONNECTION ISSUE</span>
                  <p>The application cannot reach the Surge event request site or does not have an internet connection.</p>
                  <small>The kiosk will keep trying to reconnect automatically.</small>
                </section>
                """
            : """
                <section class="message closed-message">
                  <span class="message-label">STATION CLOSED</span>
                  <p>This event request station is currently closed.</p>
                </section>
                """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Event Request Station Closed | Surge Mobile</title>
              <style>
                :root {
                  --lime: #acd037;
                  --aqua: #cd7eff;
                  --blue: #7800c4;
                  --purple: #b744ff;
                  --orange: #ff8a3c;
                  --ink: #0b0023;
                  --paper: #ffffff;
                }
                * { box-sizing: border-box; }
                html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body {
                  font-family: 'Open Sans', 'Segoe UI', Arial, sans-serif;
                  color: var(--ink);
                  background:
                    radial-gradient(circle at 8% 12%, rgba(172,208,55,.34) 0 8%, transparent 8.4%),
                    radial-gradient(circle at 92% 16%, rgba(183,68,255,.32) 0 9%, transparent 9.4%),
                    radial-gradient(circle at 88% 88%, rgba(183,68,255,.30) 0 11%, transparent 11.4%),
                    radial-gradient(circle at 12% 88%, rgba(255,138,60,.27) 0 7%, transparent 7.4%),
                    linear-gradient(135deg, #fbf5ff 0%, #ffffff 50%, #f4e8ff 100%);
                  display: grid;
                  place-items: center;
                  padding: 28px;
                }
                .card {
                  position: relative;
                  width: min(980px, 94vw);
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(11,0,35,.24);
                  overflow: hidden;
                  text-align: center;
                }
                .stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--lime) 0 25%, var(--aqua) 25% 50%, var(--purple) 50% 75%, var(--orange) 75% 100%);
                }
                .content { padding: clamp(24px, 4.5vh, 48px) clamp(28px, 6vw, 74px) 42px; }
                .brand { min-height: 118px; display: grid; place-items: center; margin-bottom: 6px; }
                .brand-logo { width: 190px; height: 150px; object-fit: contain; }
                .logo-fallback {
                  font-size: clamp(34px, 5vw, 60px);
                  line-height: 1;
                  font-weight: 800;
                  color: var(--blue);
                  -webkit-text-stroke: 2px var(--ink);
                }
                .closed-badge {
                  display: inline-grid;
                  place-items: center;
                  min-width: 112px;
                  height: 62px;
                  margin: 4px auto 18px;
                  padding: 0 24px;
                  border: 4px solid var(--ink);
                  border-radius: 999px;
                  background: var(--orange);
                  box-shadow: 0 8px 0 rgba(11,0,35,.12);
                  color: var(--ink);
                  font-size: 22px;
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: 1px;
                }
                h1 {
                  max-width: 820px;
                  margin: 0 auto 24px;
                  color: var(--purple);
                  font-size: clamp(40px, 5.8vw, 72px);
                  line-height: 1.02;
                  font-weight: 800;
                  letter-spacing: -2px;
                }
                .message {
                  margin: 0 auto 20px;
                  padding: 22px 28px;
                  border-radius: 18px;
                }
                .connection-message { background: #fff6e9; border: 3px solid var(--orange); }
                .closed-message { background: #fbf5ff; border: 3px solid var(--aqua); }
                .message-label {
                  display: inline-block;
                  margin-bottom: 7px;
                  color: var(--purple);
                  font-size: 14px;
                  font-weight: 800;
                  letter-spacing: 1.5px;
                }
                .message p {
                  margin: 0;
                  font-size: clamp(20px, 2.2vw, 29px);
                  line-height: 1.35;
                  font-weight: 700;
                }
                .message small {
                  display: block;
                  margin-top: 9px;
                  color: #53616d;
                  font-size: 16px;
                  font-weight: 600;
                }
                .assistance {
                  margin: 0 auto;
                  padding: 22px 28px;
                  border: 3px solid var(--lime);
                  border-radius: 18px;
                  background: #fbf5ff;
                  font-size: clamp(21px, 2.35vw, 31px);
                  line-height: 1.3;
                  font-weight: 700;
                }
                .assistance strong { color: #397819; }
                @media (max-height: 720px) {
                  .content { padding-top: 20px; padding-bottom: 24px; }
                  .brand { min-height: 80px; }
                  .brand-logo { width: 150px; height: 95px; }
                  .closed-badge { height: 50px; margin-bottom: 12px; font-size: 18px; }
                  h1 { margin-bottom: 15px; }
                  .message, .assistance { padding-top: 15px; padding-bottom: 15px; }
                  .message { margin-bottom: 14px; }
                }
              </style>
            </head>
            <body>
              <main class="card" aria-labelledby="closed-heading">
                <div class="stripe"></div>
                <div class="content">
                  <div class="brand">{{logoMarkup}}</div>
                  <div class="closed-badge" aria-hidden="true">CLOSED</div>
                  <h1 id="closed-heading">EVENT REQUEST STATION CLOSED</h1>
                  {{statusMarkup}}
                  <section class="assistance">
                    Please see a staff member at the <strong>front desk</strong> for assistance.
                  </section>
                </div>
              </main>
            </body>
            </html>
            """;
    }

    private static string GetApplicationLogoDataUrl()
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is null)
                return string.Empty;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetOfficialWordmarkDataUrl()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("SurgeWordmarkWhite.svg");
            if (stream is null)
                return string.Empty;

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return "data:image/svg+xml;base64," + Convert.ToBase64String(copy.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private string BuildThankYouHtml(DateTime? scheduleTimeOverride = null)
    {
        var resetSeconds = Math.Max(12, _settings.CompletionResetSeconds);
        var effectiveNow = scheduleTimeOverride ?? GetEffectiveNow();
        var activeAdvertisements = new List<string>();
        foreach (var advertisement in _settings.Advertisements
                     .Where(ad => ad.IsActive(effectiveNow))
                     .OrderBy(ad => ad.Name)
                     .Take(12))
        {
            try
            {
                var path = AdvertisementFiles.GetSafePath(advertisement.ImageFileName);
                if (path is null || !File.Exists(path))
                    continue;

                var fileName = Uri.EscapeDataString(Path.GetFileName(path));
                activeAdvertisements.Add($"https://{AdvertisementVirtualHost}/{fileName}");
            }
            catch (Exception ex)
            {
                KioskLog.Write("Advertisement display error: " + ex.GetType().Name + " - " + ex.Message);
            }
        }
        KioskLog.Write($"Thank-you advertisement evaluation at {effectiveNow:O}: " +
            $"{activeAdvertisements.Count} active image(s) displayed.");

        var hasAdvertisements = activeAdvertisements.Count > 0;
        var advertisementSlides = new StringBuilder();
        var advertisementDots = new StringBuilder();
        for (var index = 0; index < activeAdvertisements.Count; index++)
        {
            var activeClass = index == 0 ? " active" : string.Empty;
            advertisementSlides.Append($"<figure class=\"ad-slide{activeClass}\" data-slide=\"{index}\">" +
                $"<img src=\"{activeAdvertisements[index]}\" alt=\"Surge Entertainment special\"></figure>");
            advertisementDots.Append($"<span class=\"ad-dot{activeClass}\" aria-hidden=\"true\"></span>");
        }

        var advertisementPanel = hasAdvertisements
            ? $$"""
                <aside class="ad-panel" aria-label="Surge Entertainment specials">
                  <div class="ad-stripe"></div>
                  <div class="ad-heading">
                    <span class="ad-kicker">DON'T MISS</span>
                    <h2>Today's Specials</h2>
                  </div>
                  <div class="ad-stage">{{advertisementSlides}}</div>
                  <div class="ad-dots">{{advertisementDots}}</div>
                </aside>
                """
            : string.Empty;
        var bodyClass = hasAdvertisements ? "with-ads" : "no-ads";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Event Complete | Surge Entertainment</title>
              <link rel="preconnect" href="https://fonts.googleapis.com">
              <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
              <link href="https://fonts.googleapis.com/css2?family=Open+Sans:wght@400;600;700;800&amp;display=swap" rel="stylesheet">
              <style>
                :root {
                  --lime: #acd037;
                  --aqua: #cd7eff;
                  --blue: #7800c4;
                  --purple: #b744ff;
                  --orange: #ff8a3c;
                  --ink: #0b0023;
                  --paper: #ffffff;
                }
                * { box-sizing: border-box; }
                html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body {
                  font-family: 'Open Sans', Arial, sans-serif;
                  color: var(--ink);
                  background:
                    radial-gradient(circle at 8% 12%, rgba(172,208,55,.34) 0 8%, transparent 8.4%),
                    radial-gradient(circle at 92% 16%, rgba(183,68,255,.32) 0 9%, transparent 9.4%),
                    radial-gradient(circle at 88% 88%, rgba(183,68,255,.30) 0 11%, transparent 11.4%),
                    radial-gradient(circle at 12% 88%, rgba(255,138,60,.27) 0 7%, transparent 7.4%),
                    linear-gradient(135deg, #fbf5ff 0%, #ffffff 50%, #f4e8ff 100%);
                  display: grid;
                  place-items: center;
                  padding: 26px;
                }
                .thank-layout {
                  width: min(960px, 94vw);
                }
                .with-ads .thank-layout {
                  width: min(1660px, 96vw);
                  display: grid;
                  grid-template-columns: minmax(600px, 1.15fr) minmax(360px, .85fr);
                  align-items: center;
                  gap: clamp(20px, 2.2vw, 38px);
                }
                .card {
                  position: relative;
                  width: 100%;
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(11,0,35,.24);
                  overflow: hidden;
                  text-align: center;
                }
                .stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--lime) 0 25%, var(--aqua) 25% 50%, var(--purple) 50% 75%, var(--orange) 75% 100%);
                }
                .content { padding: clamp(24px, 4vh, 48px) clamp(30px, 6vw, 76px) 28px; }
                .logo-wrap { min-height: 105px; display: grid; place-items: center; margin-bottom: 8px; }
                .logo { display: block; width: min(400px, 70vw); max-height: 150px; object-fit: contain; }
                .logo-fallback {
                  display: none;
                  font-size: clamp(34px, 5vw, 62px);
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: -2px;
                  color: var(--blue);
                  -webkit-text-stroke: 2px var(--ink);
                }
                .check {
                  width: 86px;
                  height: 86px;
                  margin: 6px auto 12px;
                  border-radius: 50%;
                  display: grid;
                  place-items: center;
                  background: var(--lime);
                  border: 4px solid var(--ink);
                  color: var(--ink);
                  font-size: 55px;
                  line-height: 1;
                  font-weight: 800;
                  box-shadow: 0 8px 0 rgba(11,0,35,.12);
                }
                h1 {
                  margin: 0;
                  font-size: clamp(42px, 6.2vw, 76px);
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: -2px;
                  text-transform: uppercase;
                  color: var(--purple);
                }
                .complete {
                  margin: 12px 0 22px;
                  font-size: clamp(20px, 2.2vw, 29px);
                  font-weight: 700;
                  color: var(--blue);
                }
                .next-step {
                  margin: 0 auto;
                  max-width: 760px;
                  padding: 22px 26px;
                  border-radius: 18px;
                  background: #fff6e9;
                  border: 3px solid var(--orange);
                  font-size: clamp(20px, 2.2vw, 29px);
                  line-height: 1.35;
                  font-weight: 700;
                }
                .next-step strong { color: #b64c00; }
                .countdown {
                  margin: 23px 0 0;
                  font-size: 16px;
                  color: #53616d;
                  font-weight: 600;
                }
                .countdown-number { color: var(--purple); font-weight: 800; }
                .ad-panel {
                  width: 100%;
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(11,0,35,.24);
                  overflow: hidden;
                }
                .ad-stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--orange) 0 38%, var(--purple) 38% 68%, var(--aqua) 68% 100%);
                }
                .ad-heading {
                  padding: 18px 24px 12px;
                  text-align: center;
                }
                .ad-kicker {
                  display: inline-block;
                  margin-bottom: 3px;
                  padding: 4px 12px;
                  border: 2px solid var(--orange);
                  border-radius: 999px;
                  color: #b64c00;
                  background: #fff6e9;
                  font-size: 13px;
                  font-weight: 800;
                  letter-spacing: 1.5px;
                }
                .ad-heading h2 {
                  margin: 4px 0 0;
                  color: var(--purple);
                  font-size: clamp(26px, 2.5vw, 40px);
                  line-height: 1.05;
                  text-transform: uppercase;
                }
                .ad-stage {
                  position: relative;
                  height: min(65vh, 700px);
                  margin: 0 20px;
                  overflow: hidden;
                  border: 3px solid var(--aqua);
                  border-radius: 20px;
                  background: #f4fbfe;
                }
                .ad-slide {
                  position: absolute;
                  inset: 0;
                  margin: 0;
                  display: grid;
                  grid-template-rows: minmax(0, 1fr);
                  opacity: 0;
                  transform: translateX(18px);
                  transition: opacity .45s ease, transform .45s ease;
                  pointer-events: none;
                }
                .ad-slide.active {
                  opacity: 1;
                  transform: translateX(0);
                }
                .ad-slide img {
                  width: 100%;
                  height: 100%;
                  min-height: 0;
                  padding: 10px;
                  object-fit: contain;
                }
                .ad-dots {
                  min-height: 29px;
                  padding: 10px 18px 12px;
                  display: flex;
                  justify-content: center;
                  gap: 8px;
                }
                .ad-dot {
                  width: 10px;
                  height: 10px;
                  border-radius: 50%;
                  background: #c9d5db;
                  transition: background .3s ease, transform .3s ease;
                }
                .ad-dot.active { background: var(--purple); transform: scale(1.25); }
                .with-ads .content { padding-left: clamp(26px, 3.5vw, 56px); padding-right: clamp(26px, 3.5vw, 56px); }
                .with-ads h1 { font-size: clamp(42px, 4.4vw, 68px); }
                .with-ads .logo { width: min(350px, 64vw); }
                @media (max-height: 700px) {
                  .content { padding-top: 18px; padding-bottom: 18px; }
                  .logo-wrap { min-height: 80px; }
                  .logo { max-height: 105px; }
                  .check { width: 68px; height: 68px; font-size: 44px; margin-bottom: 8px; }
                  .complete { margin-bottom: 14px; }
                  .next-step { padding: 15px 22px; }
                  .countdown { margin-top: 14px; }
                  .ad-heading { padding-top: 11px; padding-bottom: 8px; }
                  .ad-stage { height: 59vh; }
                  .ad-dots { padding-top: 7px; padding-bottom: 8px; }
                }
                @media (max-width: 1050px) {
                  html, body { min-height: 100%; height: auto; overflow: auto; }
                  .with-ads .thank-layout {
                    width: min(780px, 94vw);
                    grid-template-columns: 1fr;
                    padding: 22px 0;
                  }
                  .ad-stage { height: min(68vh, 650px); }
                }
              </style>
            </head>
            <body class="{{bodyClass}}">
              <div class="thank-layout">
                <main class="card" aria-labelledby="thank-you-heading">
                  <div class="stripe"></div>
                  <div class="content">
                    <div class="logo-wrap">
                      <img class="logo" src="{{LogoUrl}}" alt="Surge Entertainment logo"
                           onerror="this.style.display='none';document.getElementById('logo-fallback').style.display='block'">
                      <div class="logo-fallback" id="logo-fallback">SURGE</div>
                    </div>
                    <div class="check" aria-hidden="true">✓</div>
                    <h1 id="thank-you-heading">Thank You!</h1>
                    <p class="complete">Your event request is ready for staff review.</p>
                    <p class="next-step">
                      Please see a team member at the <strong>front desk</strong>
                      to continue planning your <strong>Surge event.</strong>
                    </p>
                    <p class="countdown">
                      This kiosk will be ready for the next guest in
                      <span class="countdown-number" id="seconds">{{resetSeconds}}</span> seconds.
                    </p>
                  </div>
                </main>
                {{advertisementPanel}}
              </div>
              <script>
                let remaining = {{resetSeconds}};
                const seconds = document.getElementById('seconds');
                setInterval(() => {
                  remaining = Math.max(0, remaining - 1);
                  seconds.textContent = String(remaining);
                }, 1000);

                const slides = Array.from(document.querySelectorAll('.ad-slide'));
                const dots = Array.from(document.querySelectorAll('.ad-dot'));
                let currentSlide = 0;
                if (slides.length > 1) {
                  setInterval(() => {
                    slides[currentSlide].classList.remove('active');
                    dots[currentSlide]?.classList.remove('active');
                    currentSlide = (currentSlide + 1) % slides.length;
                    slides[currentSlide].classList.add('active');
                    dots[currentSlide]?.classList.add('active');
                  }, 5000);
                }
              </script>
            </body>
            </html>
            """;
    }

    private async void ShowStaffExitPrompt()
    {
        if (_promptOpen)
            return;

        _promptOpen = true;
        _idleTimer.Stop();
        TopMost = false;

        try
        {
            using var dialog = new PinEntryDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (_settings.VerifyPin(dialog.Pin))
                {
                    while (!_allowExit)
                    {
                        using var settingsDialog = new StaffSettingsDialog(
                            _settings, _previewDateTime.HasValue ? GetEffectiveNow() : null);
                        if (settingsDialog.ShowDialog(this) != DialogResult.OK)
                            return;

                        switch (settingsDialog.SelectedAction)
                        {
                            case StaffSettingsAction.ExitToWindows:
                                _allowExit = true;
                                KioskLog.Write("Staff exit accepted.");
                                Close();
                                return;
                            case StaffSettingsAction.PreviewDateTime:
                                await EnableDateTimePreviewAsync(settingsDialog.SelectedDateTime);
                                continue;
                            case StaffSettingsAction.UseLiveDateTime:
                                await DisableDateTimePreviewAsync();
                                continue;
                            case StaffSettingsAction.PreviewThankYouPage:
                                ShowThankYouPage(
                                    staffPreview: true,
                                    scheduleTimeOverride: settingsDialog.SelectedDateTime);
                                return;
                            case StaffSettingsAction.ToggleStationClosed:
                                var previousClosedSetting = _settings.StationClosed;
                                try
                                {
                                    _settings.StationClosed = !previousClosedSetting;
                                    _settings.Save();
                                    if (_settings.StationClosed)
                                        ShowStationClosedPage(connectionError: false);
                                    else
                                        await ResetForNextGuestAsync("staff reopened event request station", showStatus: false);

                                    KioskLog.Write(_settings.StationClosed
                                        ? "Staff turned on the event request station closed page."
                                        : "Staff turned off the event request station closed page.");
                                }
                                catch (Exception ex)
                                {
                                    _settings.StationClosed = previousClosedSetting;
                                    KioskLog.Write("Closed-page setting error: " +
                                        ex.GetType().Name + " - " + ex.Message);
                                    MessageBox.Show(settingsDialog,
                                        "The event request station setting could not be saved.\n\n" + ex.Message,
                                        "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }
                                continue;
                        }
                    }
                    return;
                }

                MessageBox.Show(dialog, "The staff password was not correct.", "Surge Mobile Event Kiosk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                KioskLog.Write("Incorrect staff settings password entered.");
            }
        }
        finally
        {
            _promptOpen = false;
            if (!_allowExit)
            {
                TopMost = true;
                Activate();
                _webView.Focus();
                MarkActivity();
                _idleTimer.Start();
            }
        }
    }

    private async Task EnableDateTimePreviewAsync(DateTime selectedDateTime)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_dateTimePreviewScriptId))
                _webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_dateTimePreviewScriptId);

            var script = BuildDateTimePreviewScript(selectedDateTime);
            _dateTimePreviewScriptId = await _webView.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(script);
            _previewDateTime = selectedDateTime;
            _previewStartedUtc = DateTime.UtcNow;
            _previewBanner.Text = "STAFF DATE/TIME PREVIEW — " +
                selectedDateTime.ToString("dddd, MMMM d, yyyy 'at' h:mm tt") +
                " — Press Ctrl + Alt + Shift + F12 to return to live time";
            _previewBanner.Visible = true;
            _previewBanner.BringToFront();
            await ResetForNextGuestAsync("staff date/time preview", showStatus: false);
            KioskLog.Write("Staff date/time preview enabled for " + selectedDateTime.ToString("O") + ".");
        }
        catch (Exception ex)
        {
            _dateTimePreviewScriptId = null;
            _previewDateTime = null;
            _previewStartedUtc = null;
            _previewBanner.Visible = false;
            KioskLog.Write("Date/time preview error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The date/time preview could not be started.\n\n" + ex.Message,
                "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task DisableDateTimePreviewAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_dateTimePreviewScriptId))
                _webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_dateTimePreviewScriptId);
            _dateTimePreviewScriptId = null;
            _previewDateTime = null;
            _previewStartedUtc = null;
            _previewBanner.Visible = false;
            await ResetForNextGuestAsync("return to live date and time", showStatus: false);
            KioskLog.Write("Staff date/time preview disabled.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Date/time preview reset error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The live date and time could not be restored. Restart the kiosk before allowing another guest to use it.\n\n" + ex.Message,
                "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string BuildDateTimePreviewScript(DateTime selectedDateTime)
    {
        var localValue = DateTime.SpecifyKind(selectedDateTime, DateTimeKind.Unspecified);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localValue);
        var previewTimestamp = new DateTimeOffset(localValue, localOffset).ToUnixTimeMilliseconds();
        return $$"""
            (() => {
              const RealDate = window.Date;
              const previewStart = {{previewTimestamp}};
              const realStart = RealDate.now();
              const previewNow = () => previewStart + (RealDate.now() - realStart);
              function PreviewDate(...args) {
                if (!(this instanceof PreviewDate)) return new RealDate(previewNow()).toString();
                return args.length === 0 ? new RealDate(previewNow()) : new RealDate(...args);
              }
              Object.setPrototypeOf(PreviewDate, RealDate);
              PreviewDate.prototype = RealDate.prototype;
              PreviewDate.now = previewNow;
              PreviewDate.parse = RealDate.parse;
              PreviewDate.UTC = RealDate.UTC;
              window.Date = PreviewDate;
              window.__surgeMobilePreviewDateTime = new RealDate(previewStart).toISOString();
            })();
            """;
    }

    private DateTime GetEffectiveNow()
    {
        if (!_previewDateTime.HasValue || !_previewStartedUtc.HasValue)
            return DateTime.Now;

        return _previewDateTime.Value + (DateTime.UtcNow - _previewStartedUtc.Value);
    }

    private void ShowBanner(string text, bool success)
    {
        _banner.Text = text;
        _banner.BackColor = success ? Color.FromArgb(126, 217, 87) : Color.FromArgb(255, 222, 89);
        _banner.Visible = true;
        _banner.BringToFront();
    }

    private void HideBanner() => _banner.Visible = false;

    private const string ActivityAndCompletionScript = """
        (() => {
          if (window.__surgeMobileKioskInstalled) return;
          window.__surgeMobileKioskInstalled = true;
          const officialWordmark = '__SURGE_MOBILE_LOGO_DATA_URL__';

          let lastActivityMessage = 0;
          const postActivity = () => {
            const now = Date.now();
            if (now - lastActivityMessage > 750) {
              lastActivityMessage = now;
              window.chrome.webview.postMessage("activity");
            }
          };

          ["pointerdown", "keydown", "input", "change", "touchstart", "wheel"]
            .forEach(name => window.addEventListener(name, postActivity, { capture: true, passive: true }));

          window.surgeKiosk = Object.freeze({
            reset: () => window.chrome.webview.postMessage("reset-event")
          });

          const applyOfficialSurgeBranding = () => {
            if (!document.head || !document.body ||
                location.hostname.toLowerCase() !== 'surge-guest-kiosk.m404ntfd.chatgpt.site') return;

            if (!document.getElementById('surge-mobile-official-branding')) {
              const style = document.createElement('style');
              style.id = 'surge-mobile-official-branding';
              style.textContent = `
                :root {
                  --ink: #0b0023 !important;
                  --canvas: #fbf5ff !important;
                  --purple: #b744ff !important;
                  --purple-bright: #cd7eff !important;
                  --purple-dark: #0b0023 !important;
                  --electric: #acd037 !important;
                  --yellow: #ff8a3c !important;
                }
                .topbar { background: #0b0023 !important; border-bottom-color: #b744ff !important; }
                .brand-logo { width: 210px !important; height: 54px !important; object-fit: contain !important; border: 0 !important; border-radius: 0 !important; }
                .hero { background: radial-gradient(circle at 86% 18%, rgba(183,68,255,.40), transparent 30%), linear-gradient(120deg,#0b0023 0%,#2f075d 54%,#7800c4 100%) !important; }
                .kicker { color: #acd037 !important; }
                .section-number, .modal-select { background: linear-gradient(135deg,#b744ff,#7800c4) !important; }
                .primary-button { background: #b744ff !important; color: #fff !important; }
                .submit-panel, footer { background: #0b0023 !important; }
              `;
              document.head.appendChild(style);
            }

            const logo = document.querySelector('.brand-logo');
            if (logo && officialWordmark && logo.src !== officialWordmark) {
              logo.src = officialWordmark;
              logo.alt = 'Surge Entertainment logo';
              logo.removeAttribute('referrerpolicy');
            }
          };

          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', applyOfficialSurgeBranding, { once: true });
          else
            applyOfficialSurgeBranding();

          const brandingObserver = new MutationObserver(applyOfficialSurgeBranding);
          brandingObserver.observe(document.documentElement, { childList: true, subtree: true });
          setTimeout(() => brandingObserver.disconnect(), 15000);
        })();
        """;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal enum AdvertisementScheduleType
{
    SpecificDates,
    Weekly
}

internal sealed class KioskAdvertisement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Advertisement";
    public string ImageFileName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public AdvertisementScheduleType ScheduleType { get; set; } = AdvertisementScheduleType.SpecificDates;
    public DateTime StartDateTime { get; set; } = DateTime.Today;
    public DateTime EndDateTime { get; set; } = DateTime.Today.AddDays(1).AddTicks(-1);
    public DayOfWeek[] DaysOfWeek { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeSpan DailyStartTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan DailyEndTime { get; set; } = TimeSpan.FromHours(22);

    public bool IsActive(DateTime now)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ImageFileName))
            return false;

        if (ScheduleType == AdvertisementScheduleType.SpecificDates)
            return now >= StartDateTime && now <= EndDateTime;

        var time = now.TimeOfDay;
        if (DailyStartTime == DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek);

        if (DailyStartTime <= DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek) && time >= DailyStartTime && time <= DailyEndTime;

        if (DaysOfWeek.Contains(now.DayOfWeek) && time >= DailyStartTime)
            return true;
        var previousDay = (DayOfWeek)(((int)now.DayOfWeek + 6) % 7);
        return DaysOfWeek.Contains(previousDay) && time <= DailyEndTime;
    }

    public string ScheduleSummary()
    {
        if (ScheduleType == AdvertisementScheduleType.SpecificDates)
            return $"{StartDateTime:MMM d, yyyy h:mm tt} – {EndDateTime:MMM d, yyyy h:mm tt}";

        var days = DaysOfWeek.Length == 7
            ? "Every day"
            : string.Join(", ", DaysOfWeek.Select(day => day.ToString()[..3]));
        if (DailyStartTime == DailyEndTime)
            return days + " · All day";
        var overnight = DailyStartTime > DailyEndTime ? " (overnight)" : string.Empty;
        return $"{days} · {DateTime.Today.Add(DailyStartTime):h:mm tt}–{DateTime.Today.Add(DailyEndTime):h:mm tt}{overnight}";
    }

    public KioskAdvertisement Clone() => new()
    {
        Id = Id,
        Name = Name,
        ImageFileName = ImageFileName,
        Enabled = Enabled,
        ScheduleType = ScheduleType,
        StartDateTime = StartDateTime,
        EndDateTime = EndDateTime,
        DaysOfWeek = [.. DaysOfWeek],
        DailyStartTime = DailyStartTime,
        DailyEndTime = DailyEndTime
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Advertisement" : Name.Trim();
        ImageFileName = Path.GetFileName(ImageFileName ?? string.Empty);
        if (ScheduleType != AdvertisementScheduleType.SpecificDates &&
            ScheduleType != AdvertisementScheduleType.Weekly)
            ScheduleType = AdvertisementScheduleType.SpecificDates;
        if (EndDateTime <= StartDateTime) EndDateTime = StartDateTime.AddHours(1);
        DaysOfWeek ??= [];
        DaysOfWeek = DaysOfWeek.Distinct().Where(day => (int)day is >= 0 and <= 6).ToArray();
        if (DaysOfWeek.Length == 0) DaysOfWeek = Enum.GetValues<DayOfWeek>();
        DailyStartTime = NormalizeTime(DailyStartTime);
        DailyEndTime = NormalizeTime(DailyEndTime);
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var ticks = value.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}

internal sealed class KioskSettings
{
    public string StartUrl { get; set; } =
        "https://surge-guest-kiosk.m404ntfd.chatgpt.site/";

    public string[] AllowedHosts { get; set; } = ["surge-guest-kiosk.m404ntfd.chatgpt.site"];
    public string[] AllowedPathPrefixes { get; set; } = ["/"];
    public int IdleTimeoutMinutes { get; set; } = 3;
    public int CompletionResetSeconds { get; set; } = 15;
    public bool StationClosed { get; set; }

    public string[] CompletionUrlKeywords { get; set; } =
        ["success", "complete", "completed", "confirmation", "finished", "done", "submitted", "thankyou", "thank-you"];
    public List<KioskAdvertisement> Advertisements { get; set; } = [];

    public string StaffPinSalt { get; set; } = string.Empty;
    public string StaffPinHash { get; set; } = string.Empty;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SurgeMobileEventKiosk", "Data");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string AdvertisementsDirectory => Path.Combine(DataDirectory, "Advertisements");

    public static KioskSettings? LoadOrCreate()
    {
        Directory.CreateDirectory(DataDirectory);
        var settings = new KioskSettings();

        if (File.Exists(SettingsPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<KioskSettings>(File.ReadAllText(SettingsPath));
                if (loaded is null)
                    throw new InvalidDataException("The settings file was empty.");

                loaded.Normalize();
                if (!string.IsNullOrWhiteSpace(loaded.StaffPinHash))
                {
                    return loaded;
                }

                settings = loaded;
                KioskLog.Write("A new staff password is required; existing kiosk and advertisement settings were retained.");
            }
            catch (Exception ex)
            {
                KioskLog.Write("Settings read error: " + ex.GetType().Name + " - " + ex.Message);
                settings = new KioskSettings();
                MessageBox.Show(
                    "The kiosk settings could not be read. A new staff password must be created.",
                    "Surge Mobile Event Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        using var pinDialog = new PinSetupDialog();
        if (pinDialog.ShowDialog() != DialogResult.OK)
            return null;

        settings.SetPin(pinDialog.Pin);
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        StaffPinSalt = Convert.ToBase64String(salt);
        StaffPinHash = Convert.ToBase64String(DerivePinHash(pin, salt));
    }

    public bool VerifyPin(string pin)
    {
        try
        {
            var salt = Convert.FromBase64String(StaffPinSalt);
            var expected = Convert.FromBase64String(StaffPinHash);
            var actual = DerivePinHash(pin, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private void Normalize()
    {
        if (!Uri.TryCreate(StartUrl, UriKind.Absolute, out _))
            StartUrl = "https://surge-guest-kiosk.m404ntfd.chatgpt.site/";

        AllowedHosts ??= ["surge-guest-kiosk.m404ntfd.chatgpt.site"];
        AllowedPathPrefixes ??= ["/"];
        CompletionUrlKeywords ??= ["success", "complete", "done", "submitted"];
        Advertisements ??= [];
        foreach (var advertisement in Advertisements)
            advertisement.Normalize();
        IdleTimeoutMinutes = Math.Clamp(IdleTimeoutMinutes, 1, 60);
        CompletionResetSeconds = Math.Clamp(CompletionResetSeconds, 12, 60);
    }

    private static byte[] DerivePinHash(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 150_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}

internal sealed class PinSetupDialog : Form
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    public string Pin => _pin.Text;

    public PinSetupDialog()
    {
        Text = "Create Staff Settings Password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(470, 275);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Create the numerical staff password for kiosk settings.",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Bounds = new Rectangle(25, 20, 420, 35)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = "Use 4–8 numbers only. Staff will press Ctrl + Alt + Shift + F12 and enter this password.",
            Bounds = new Rectangle(25, 58, 420, 48)
        };
        var pinLabel = new Label { Text = "New Password:", AutoSize = true, Location = new Point(25, 122) };
        var confirmLabel = new Label { Text = "Confirm Password:", AutoSize = true, Location = new Point(12, 161) };
        _pin.Location = new Point(150, 118);
        _confirm.Location = new Point(150, 157);
        ConfigureNumericOnly(_pin);
        ConfigureNumericOnly(_confirm);

        var save = new Button { Text = "Save and Start", Bounds = new Rectangle(213, 215, 130, 36) };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(350, 215, 90, 36), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => ValidateAndClose();

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([heading, note, pinLabel, confirmLabel, _pin, _confirm, save, cancel]);
    }

    private void ValidateAndClose()
    {
        if (_pin.Text.Length is < 4 or > 8 ||
            !_pin.Text.All(character => character >= '0' && character <= '9'))
        {
            MessageBox.Show(this, "Enter a password containing 4–8 numbers only.", "Staff Password",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pin.Focus();
            return;
        }

        if (_pin.Text != _confirm.Text)
        {
            MessageBox.Show(this, "The two password entries do not match.", "Staff Password",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _confirm.Clear();
            _confirm.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static void ConfigureNumericOnly(TextBox box)
    {
        var cleaning = false;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(box.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == box.Text) return;
            cleaning = true;
            box.Text = numbersOnly;
            box.SelectionStart = box.Text.Length;
            cleaning = false;
        };
    }
}

internal enum StaffSettingsAction
{
    None,
    ExitToWindows,
    PreviewDateTime,
    UseLiveDateTime,
    PreviewThankYouPage,
    ToggleStationClosed
}

internal sealed class StaffSettingsDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly string _connectionTestUrl;
    private readonly Button _connectionButton = new();
    private readonly Label _connectionResult = new();
    private readonly Button _updateButton = new();
    private readonly Label _updateResult = new();
    private readonly DateTimePicker _datePicker = new();
    private readonly DateTimePicker _timePicker = new();

    public StaffSettingsAction SelectedAction { get; private set; }
    public DateTime SelectedDateTime => _datePicker.Value.Date + _timePicker.Value.TimeOfDay;

    public StaffSettingsDialog(KioskSettings settings, DateTime? activePreview)
    {
        _settings = settings;
        _connectionTestUrl = settings.StartUrl;
        Text = "Surge Mobile Staff Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(680, 710);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "STAFF SETTINGS",
            Font = new Font("Segoe UI", 21, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 17, 630, 45)
        };
        var currentStatus = new Label
        {
            AutoSize = false,
            Text = activePreview.HasValue
                ? "Date/time preview is active: " + activePreview.Value.ToString("MMM d, yyyy h:mm tt")
                : "The kiosk is currently using the live date and time.",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = activePreview.HasValue ? Color.FromArgb(182, 76, 0) : Color.FromArgb(120, 0, 196),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(30, 62, 620, 28)
        };

        var internetGroup = new GroupBox
        {
            Text = "Internet Connection and Kiosk Updates",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 0, 196),
            Bounds = new Rectangle(30, 98, 620, 162)
        };
        var internetNote = new Label
        {
            AutoSize = false,
            Text = "Test the live Surge event request site or check GitHub for a newer kiosk version.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(11, 0, 35),
            Bounds = new Rectangle(18, 27, 575, 25)
        };
        _connectionButton.Text = "Check Connection";
        _connectionButton.Bounds = new Rectangle(18, 61, 165, 36);
        _connectionButton.BackColor = Color.FromArgb(205, 126, 255);
        _connectionButton.FlatStyle = FlatStyle.Flat;
        _connectionButton.Click += async (_, _) => await CheckConnectionAsync();
        _connectionResult.AutoSize = false;
        _connectionResult.Text = "Not checked yet.";
        _connectionResult.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _connectionResult.ForeColor = Color.FromArgb(83, 97, 109);
        _connectionResult.TextAlign = ContentAlignment.MiddleLeft;
        _connectionResult.Bounds = new Rectangle(198, 61, 395, 36);
        _updateButton.Text = "Check for Kiosk Update";
        _updateButton.Bounds = new Rectangle(18, 108, 165, 36);
        _updateButton.BackColor = Color.FromArgb(172, 208, 55);
        _updateButton.FlatStyle = FlatStyle.Flat;
        _updateButton.Click += async (_, _) => await CheckForUpdateAsync();
        _updateResult.AutoSize = false;
        _updateResult.Text = "Installed version: " + KioskUpdater.CurrentVersion;
        _updateResult.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _updateResult.ForeColor = Color.FromArgb(83, 97, 109);
        _updateResult.TextAlign = ContentAlignment.MiddleLeft;
        _updateResult.Bounds = new Rectangle(198, 108, 395, 36);
        internetGroup.Controls.AddRange([
            internetNote, _connectionButton, _connectionResult, _updateButton, _updateResult]);

        var previewGroup = new GroupBox
        {
            Text = "Preview a Different Date and Time",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            Bounds = new Rectangle(30, 272, 620, 205)
        };
        var previewNote = new Label
        {
            AutoSize = false,
            Text = "Choose a date and time, then reload a fresh event request in preview mode. This changes browser time only; online content may still use live server time.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(11, 0, 35),
            Bounds = new Rectangle(18, 27, 580, 52)
        };
        var dateLabel = new Label
        {
            Text = "Date:", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 0, 35), Location = new Point(32, 94)
        };
        var timeLabel = new Label
        {
            Text = "Time:", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 0, 35), Location = new Point(323, 94)
        };
        var initialValue = activePreview ?? DateTime.Now;
        _datePicker.Format = DateTimePickerFormat.Long;
        _datePicker.Value = initialValue;
        _datePicker.Bounds = new Rectangle(82, 89, 215, 32);
        _timePicker.Format = DateTimePickerFormat.Custom;
        _timePicker.CustomFormat = "h:mm tt";
        _timePicker.ShowUpDown = true;
        _timePicker.Value = initialValue;
        _timePicker.Bounds = new Rectangle(375, 89, 125, 32);

        var previewButton = new Button
        {
            Text = "Preview Selected Date & Time",
            Bounds = new Rectangle(18, 145, 245, 42),
            BackColor = Color.FromArgb(172, 208, 55),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        previewButton.Click += (_, _) => Complete(StaffSettingsAction.PreviewDateTime);
        var liveButton = new Button
        {
            Text = "Return to Live Date & Time",
            Bounds = new Rectangle(276, 145, 235, 42),
            BackColor = Color.FromArgb(255, 138, 60),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Enabled = activePreview.HasValue
        };
        liveButton.Click += (_, _) => Complete(StaffSettingsAction.UseLiveDateTime);
        previewGroup.Controls.AddRange([
            previewNote, dateLabel, timeLabel, _datePicker, _timePicker, previewButton, liveButton]);

        var exitButton = new Button
        {
            Text = "Exit Kiosk",
            Bounds = new Rectangle(30, 652, 190, 45),
            BackColor = Color.FromArgb(255, 138, 60),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        exitButton.Click += (_, _) => Complete(StaffSettingsAction.ExitToWindows);
        var advertisementsButton = new Button
        {
            Text = "Manage Advertisements",
            Bounds = new Rectangle(18, 30, 180, 42),
            BackColor = Color.FromArgb(183, 68, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        advertisementsButton.Click += (_, _) =>
        {
            using var advertisementsDialog = new AdvertisementManagerDialog(
                _settings,
                activePreview.HasValue ? SelectedDateTime : null);
            advertisementsDialog.ShowDialog(this);
        };
        var thankYouPreviewButton = new Button
        {
            Text = "Preview Thank-You Page",
            Bounds = new Rectangle(210, 30, 190, 42),
            BackColor = Color.FromArgb(205, 126, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        thankYouPreviewButton.Click += (_, _) => Complete(StaffSettingsAction.PreviewThankYouPage);
        var changePasswordButton = new Button
        {
            Text = "Change Staff Password",
            Bounds = new Rectangle(412, 30, 190, 42),
            BackColor = Color.FromArgb(172, 208, 55),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        changePasswordButton.Click += (_, _) =>
        {
            using var passwordDialog = new StaffPasswordChangeDialog(_settings);
            passwordDialog.ShowDialog(this);
        };
        var closedPageStatus = new Label
        {
            AutoSize = false,
            Text = _settings.StationClosed
                ? "Closed page is ON — guests cannot start an event request."
                : "Closed page is OFF — the event request station is available.",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = _settings.StationClosed
                ? Color.FromArgb(180, 35, 24)
                : Color.FromArgb(54, 128, 27),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(18, 85, 365, 42)
        };
        var closedPageButton = new Button
        {
            Text = _settings.StationClosed ? "Turn Off Closed Page" : "Turn On Closed Page",
            Bounds = new Rectangle(397, 85, 205, 42),
            BackColor = _settings.StationClosed
                ? Color.FromArgb(172, 208, 55)
                : Color.FromArgb(255, 138, 60),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        closedPageButton.Click += (_, _) => Complete(StaffSettingsAction.ToggleStationClosed);
        var staffToolsGroup = new GroupBox
        {
            Text = "Event Request Station, Advertisements, and Staff Tools",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            Bounds = new Rectangle(30, 489, 620, 145)
        };
        staffToolsGroup.Controls.AddRange([
            advertisementsButton, thankYouPreviewButton, changePasswordButton,
            closedPageStatus, closedPageButton]);
        var returnButton = new Button
        {
            Text = "Return to Kiosk",
            Bounds = new Rectangle(460, 652, 190, 45),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        CancelButton = returnButton;
        Controls.AddRange([
            heading, currentStatus, internetGroup, previewGroup, staffToolsGroup,
            exitButton, returnButton]);
    }

    private async Task CheckConnectionAsync()
    {
        _connectionButton.Enabled = false;
        _connectionResult.Text = "Checking the Surge event request site…";
        _connectionResult.ForeColor = Color.FromArgb(83, 97, 109);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SurgeMobileEventKiosk/" + KioskUpdater.CurrentVersion);
            using var response = await client.GetAsync(
                _connectionTestUrl, HttpCompletionOption.ResponseHeadersRead);
            if (IsDisposed) return;

            if (response.IsSuccessStatusCode)
            {
                _connectionResult.Text = "Connected — the Surge event request site responded successfully.";
                _connectionResult.ForeColor = Color.FromArgb(54, 128, 27);
            }
            else
            {
                _connectionResult.Text = $"Website reached — response: {(int)response.StatusCode} {response.ReasonPhrase}";
                _connectionResult.ForeColor = Color.FromArgb(182, 76, 0);
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _connectionResult.Text = "Connection failed — " + ex.Message;
            _connectionResult.ForeColor = Color.FromArgb(180, 35, 24);
        }
        finally
        {
            if (!IsDisposed)
                _connectionButton.Enabled = true;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        _updateButton.Enabled = false;
        _updateResult.Text = "Checking GitHub for an update…";
        _updateResult.ForeColor = Color.FromArgb(83, 97, 109);

        try
        {
            var result = await KioskUpdater.CheckDownloadAndApplyAsync();
            if (IsDisposed) return;

            _updateResult.Text = result.Message;
            _updateResult.ForeColor = result.Status switch
            {
                KioskUpdateStatus.UpToDate => Color.FromArgb(54, 128, 27),
                KioskUpdateStatus.Applying => Color.FromArgb(54, 128, 27),
                KioskUpdateStatus.NotConfigured => Color.FromArgb(182, 76, 0),
                KioskUpdateStatus.NotInstalled => Color.FromArgb(182, 76, 0),
                _ => Color.FromArgb(180, 35, 24)
            };
        }
        finally
        {
            if (!IsDisposed)
                _updateButton.Enabled = true;
        }
    }

    private void Complete(StaffSettingsAction action)
    {
        SelectedAction = action;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class StaffPasswordChangeDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly TextBox _currentPassword = CreatePasswordField();
    private readonly TextBox _newPassword = CreatePasswordField();
    private readonly TextBox _confirmNewPassword = CreatePasswordField();
    private readonly Label _verificationStatus = new();
    private readonly Button _changeButton = new();
    private string? _verifiedCurrentPassword;

    public StaffPasswordChangeDialog(KioskSettings settings)
    {
        _settings = settings;
        Text = "Change Staff Password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(620, 445);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "CHANGE STAFF PASSWORD",
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 14, 570, 44)
        };
        var requirement = new Label
        {
            AutoSize = false,
            Text = "The staff password must contain between 4–8 numbers only.",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(182, 76, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(35, 59, 550, 35)
        };

        var currentGroup = new GroupBox
        {
            Text = "Verify Existing Password",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 0, 196),
            Bounds = new Rectangle(30, 102, 560, 132)
        };
        var currentLabel = MakePasswordLabel("Confirm Current Password:", 18, 37, 178);
        _currentPassword.Bounds = new Rectangle(202, 31, 150, 32);
        var verifyButton = new Button
        {
            Text = "Verify Current Password",
            Bounds = new Rectangle(365, 29, 175, 37),
            BackColor = Color.FromArgb(205, 126, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        verifyButton.Click += (_, _) => VerifyCurrentPassword();
        _verificationStatus.AutoSize = false;
        _verificationStatus.Text = "Current password has not been verified.";
        _verificationStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _verificationStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _verificationStatus.Bounds = new Rectangle(202, 75, 335, 30);
        currentGroup.Controls.AddRange([
            currentLabel, _currentPassword, verifyButton, _verificationStatus]);

        var newGroup = new GroupBox
        {
            Text = "Choose New Password",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            Bounds = new Rectangle(30, 246, 560, 120)
        };
        var newLabel = MakePasswordLabel("New Password:", 60, 36, 135);
        var confirmLabel = MakePasswordLabel("Confirm New Password:", 18, 77, 178);
        _newPassword.Bounds = new Rectangle(202, 30, 180, 32);
        _confirmNewPassword.Bounds = new Rectangle(202, 71, 180, 32);
        newGroup.Controls.AddRange([
            newLabel, confirmLabel, _newPassword, _confirmNewPassword]);

        _changeButton.Text = "OK";
        _changeButton.Bounds = new Rectangle(315, 386, 135, 40);
        _changeButton.BackColor = Color.FromArgb(172, 208, 55);
        _changeButton.FlatStyle = FlatStyle.Flat;
        _changeButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _changeButton.Enabled = false;
        _changeButton.Click += (_, _) => ChangePassword();
        var cancelButton = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(460, 386, 130, 40),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        _currentPassword.TextChanged += (_, _) => ResetCurrentVerificationIfChanged();
        AcceptButton = _changeButton;
        CancelButton = cancelButton;
        Controls.AddRange([
            heading, requirement, currentGroup, newGroup, _changeButton, cancelButton]);
        Shown += (_, _) => _currentPassword.Focus();
    }

    private static TextBox CreatePasswordField()
    {
        var field = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = 8,
            Font = new Font("Segoe UI", 11, FontStyle.Regular)
        };
        var cleaning = false;
        field.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        field.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(field.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == field.Text) return;
            cleaning = true;
            field.Text = numbersOnly;
            field.SelectionStart = field.Text.Length;
            cleaning = false;
        };
        return field;
    }

    private static Label MakePasswordLabel(string text, int x, int y, int width) => new()
    {
        AutoSize = false,
        Text = text,
        ForeColor = Color.FromArgb(11, 0, 35),
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleRight,
        Bounds = new Rectangle(x, y, width, 25)
    };

    private static bool IsValidPassword(string value) =>
        value.Length is >= 4 and <= 8 && value.All(character => character >= '0' && character <= '9');

    private void VerifyCurrentPassword()
    {
        var current = _currentPassword.Text;
        if (!IsValidPassword(current))
        {
            ShowProblem("The current staff password must contain between 4–8 numbers.", _currentPassword);
            return;
        }
        if (!_settings.VerifyPin(current))
        {
            _verifiedCurrentPassword = null;
            _changeButton.Enabled = false;
            _verificationStatus.Text = "Current password is incorrect.";
            _verificationStatus.ForeColor = Color.FromArgb(180, 35, 24);
            ShowProblem("The current staff password is incorrect.", _currentPassword);
            return;
        }

        _verifiedCurrentPassword = current;
        _verificationStatus.Text = "Current password verified. The OK button is now available.";
        _verificationStatus.ForeColor = Color.FromArgb(54, 128, 27);
        _changeButton.Enabled = true;
        _newPassword.Focus();
    }

    private void ResetCurrentVerificationIfChanged()
    {
        if (_verifiedCurrentPassword is null || _currentPassword.Text == _verifiedCurrentPassword)
            return;

        _verifiedCurrentPassword = null;
        _changeButton.Enabled = false;
        _verificationStatus.Text = "Current password changed. Verify it again.";
        _verificationStatus.ForeColor = Color.FromArgb(182, 76, 0);
    }

    private void ChangePassword()
    {
        var current = _currentPassword.Text;
        if (_verifiedCurrentPassword is null || current != _verifiedCurrentPassword ||
            !_settings.VerifyPin(current))
        {
            _verifiedCurrentPassword = null;
            _changeButton.Enabled = false;
            _verificationStatus.Text = "Verify the current password before continuing.";
            _verificationStatus.ForeColor = Color.FromArgb(180, 35, 24);
            ShowProblem("Verify the current staff password before changing it.", _currentPassword);
            return;
        }
        if (!IsValidPassword(_newPassword.Text))
        {
            ShowProblem("The new staff password must contain between 4–8 numbers.", _newPassword);
            return;
        }
        if (!IsValidPassword(_confirmNewPassword.Text))
        {
            ShowProblem("Confirm the new staff password using between 4–8 numbers.", _confirmNewPassword);
            return;
        }
        if (_newPassword.Text != _confirmNewPassword.Text)
        {
            ShowProblem("The new password and Confirm New Password do not match.", _confirmNewPassword);
            return;
        }

        try
        {
            var previousSalt = _settings.StaffPinSalt;
            var previousHash = _settings.StaffPinHash;
            _settings.SetPin(_newPassword.Text);
            try
            {
                _settings.Save();
            }
            catch
            {
                _settings.StaffPinSalt = previousSalt;
                _settings.StaffPinHash = previousHash;
                throw;
            }
            MessageBox.Show(this,
                "The staff password has been successfully changed.",
                "Password Changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The staff password could not be saved. No password change was completed.\n\n" + ex.Message,
                "Password Change Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowProblem(string message, Control focusControl)
    {
        MessageBox.Show(this, message, "Password Change",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focusControl.Focus();
    }
}

internal static class AdvertisementFiles
{
    public static string ImportJpeg(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The selected JPG could not be found.", sourcePath);
        if (info.Length > 25_000_000)
            throw new InvalidOperationException("The JPG must be smaller than 25 MB.");

        using (var image = Image.FromFile(sourcePath))
        {
            if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                throw new InvalidOperationException("The selected file is not a valid JPG image.");
        }

        Directory.CreateDirectory(KioskSettings.AdvertisementsDirectory);
        var fileName = Guid.NewGuid().ToString("N") + ".jpg";
        File.Copy(sourcePath, Path.Combine(KioskSettings.AdvertisementsDirectory, fileName), false);
        return fileName;
    }

    public static string? GetSafePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var root = Path.GetFullPath(KioskSettings.AdvertisementsDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(KioskSettings.AdvertisementsDirectory, Path.GetFileName(fileName)));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    public static void DeleteIfPresent(string? fileName)
    {
        try
        {
            var path = GetSafePath(fileName);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Advertisement image cleanup error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }
}

internal sealed class AdvertisementManagerDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly DateTime? _previewNow;
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly Label _details = new();

    public AdvertisementManagerDialog(KioskSettings settings, DateTime? previewNow)
    {
        _settings = settings;
        _previewNow = previewNow;
        Text = "Manage Thank-You Page Advertisements";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(960, 650);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "SCHEDULED ADVERTISEMENTS",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(25, 12, 650, 42)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = previewNow.HasValue
                ? "Status is shown for the staff preview time. Active JPG ads appear beside the thank-you message."
                : "Active JPG advertisements appear beside the thank-you message. Multiple active ads rotate automatically.",
            ForeColor = Color.FromArgb(83, 97, 109),
            Bounds = new Rectangle(25, 51, 890, 25)
        };

        _list.Bounds = new Rectangle(25, 82, 620, 480);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("Advertisement", 180);
        _list.Columns.Add("Schedule", 335);
        _list.Columns.Add("Status", 95);
        _list.SelectedIndexChanged += (_, _) => ShowSelectedPreview();
        _list.DoubleClick += (_, _) => EditSelected();

        _preview.Bounds = new Rectangle(675, 82, 250, 250);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        _details.AutoSize = false;
        _details.Bounds = new Rectangle(675, 345, 250, 125);
        _details.ForeColor = Color.FromArgb(11, 0, 35);
        _details.Font = new Font("Segoe UI", 9.5f);

        var addButton = CreateButton("Add Advertisement", 25, Color.FromArgb(172, 208, 55));
        addButton.Click += (_, _) => AddAdvertisement();
        var editButton = CreateButton("Edit", 205, Color.FromArgb(205, 126, 255));
        editButton.Click += (_, _) => EditSelected();
        var toggleButton = CreateButton("Enable / Disable", 335, Color.FromArgb(255, 222, 89));
        toggleButton.Width = 160;
        toggleButton.Click += (_, _) => ToggleSelected();
        var deleteButton = CreateButton("Delete", 505, Color.FromArgb(255, 138, 60));
        deleteButton.Click += (_, _) => DeleteSelected();
        var closeButton = new Button
        {
            Text = "Close",
            Bounds = new Rectangle(805, 584, 120, 42),
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.AddRange([
            heading, note, _list, _preview, _details,
            addButton, editButton, toggleButton, deleteButton, closeButton]);
        FormClosed += (_, _) => _preview.Image?.Dispose();
        RefreshList();
    }

    private static Button CreateButton(string text, int x, Color color) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, 584, 120, 42),
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
    };

    private KioskAdvertisement? SelectedAdvertisement =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as KioskAdvertisement : null;

    private void RefreshList(string? selectId = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var advertisement in _settings.Advertisements.OrderBy(ad => ad.Name))
        {
            var scheduleNow = _previewNow ?? DateTime.Now;
            var status = !advertisement.Enabled
                ? "Disabled"
                : advertisement.IsActive(scheduleNow)
                    ? _previewNow.HasValue ? "Active in preview" : "Active now"
                    : "Scheduled";
            var item = new ListViewItem(advertisement.Name) { Tag = advertisement };
            item.SubItems.Add(advertisement.ScheduleSummary());
            item.SubItems.Add(status);
            if (!advertisement.Enabled) item.ForeColor = Color.Gray;
            _list.Items.Add(item);
            if (advertisement.Id == selectId) item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        ShowSelectedPreview();
    }

    private void ShowSelectedPreview()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        var advertisement = SelectedAdvertisement;
        if (advertisement is null)
        {
            _details.Text = "Select an advertisement to preview it.";
            return;
        }

        var path = AdvertisementFiles.GetSafePath(advertisement.ImageFileName);
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var image = Image.FromFile(path);
                _preview.Image = new Bitmap(image);
            }
            catch
            {
                _details.Text = "The saved JPG could not be opened.";
            }
        }
        _details.Text = advertisement.Name + Environment.NewLine +
            advertisement.ScheduleSummary() + Environment.NewLine +
            (advertisement.Enabled ? "Enabled" : "Disabled");
    }

    private void AddAdvertisement()
    {
        using var editor = new AdvertisementEditorDialog();
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        _settings.Advertisements.Add(editor.Advertisement);
        if (SaveSettings()) RefreshList(editor.Advertisement.Id);
    }

    private void EditSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        var oldImage = selected.ImageFileName;
        using var editor = new AdvertisementEditorDialog(selected);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        var index = _settings.Advertisements.FindIndex(ad => ad.Id == selected.Id);
        if (index < 0) return;
        _settings.Advertisements[index] = editor.Advertisement;
        if (SaveSettings())
        {
            if (!string.Equals(oldImage, editor.Advertisement.ImageFileName, StringComparison.OrdinalIgnoreCase))
                AdvertisementFiles.DeleteIfPresent(oldImage);
            RefreshList(editor.Advertisement.Id);
        }
    }

    private void ToggleSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        selected.Enabled = !selected.Enabled;
        if (SaveSettings()) RefreshList(selected.Id);
    }

    private void DeleteSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        if (MessageBox.Show(this,
                $"Delete the advertisement '{selected.Name}' and its saved JPG?",
                "Delete Advertisement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _settings.Advertisements.RemoveAll(ad => ad.Id == selected.Id);
        if (SaveSettings())
        {
            AdvertisementFiles.DeleteIfPresent(selected.ImageFileName);
            RefreshList();
        }
    }

    private bool SaveSettings()
    {
        try
        {
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The advertisement settings could not be saved.\n\n" + ex.Message,
                "Advertisements", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

internal sealed class AdvertisementEditorDialog : Form
{
    private readonly TextBox _name = new();
    private readonly Label _fileLabel = new();
    private readonly PictureBox _preview = new();
    private readonly CheckBox _enabled = new();
    private readonly RadioButton _specificDates = new();
    private readonly RadioButton _weekly = new();
    private readonly DateTimePicker _startDate = new();
    private readonly DateTimePicker _startTime = new();
    private readonly DateTimePicker _endDate = new();
    private readonly DateTimePicker _endTime = new();
    private readonly DateTimePicker _weeklyStart = new();
    private readonly DateTimePicker _weeklyEnd = new();
    private readonly Dictionary<DayOfWeek, CheckBox> _dayChecks = [];
    private readonly KioskAdvertisement _working;
    private string? _selectedSourcePath;

    public KioskAdvertisement? Advertisement { get; private set; }

    public AdvertisementEditorDialog(KioskAdvertisement? existing = null)
    {
        _working = existing?.Clone() ?? new KioskAdvertisement();
        Text = existing is null ? "Add Advertisement" : "Edit Advertisement";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(790, 690);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = existing is null ? "ADD ADVERTISEMENT" : "EDIT ADVERTISEMENT",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 12, 740, 42)
        };

        var imageGroup = new GroupBox
        {
            Text = "JPG Advertisement",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 0, 196),
            Bounds = new Rectangle(25, 60, 740, 220)
        };
        _preview.Bounds = new Rectangle(18, 30, 285, 170);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        var uploadButton = new Button
        {
            Text = "Upload JPG…",
            Bounds = new Rectangle(330, 35, 150, 40),
            BackColor = Color.FromArgb(205, 126, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        uploadButton.Click += (_, _) => SelectJpeg();
        _fileLabel.AutoSize = false;
        _fileLabel.Bounds = new Rectangle(495, 35, 220, 45);
        _fileLabel.ForeColor = Color.FromArgb(83, 97, 109);
        var nameLabel = new Label
        {
            Text = "Advertisement name:", AutoSize = true,
            ForeColor = Color.FromArgb(11, 0, 35), Location = new Point(330, 105)
        };
        _name.Bounds = new Rectangle(330, 130, 385, 32);
        _name.MaxLength = 80;
        _enabled.Text = "Advertisement is enabled";
        _enabled.AutoSize = true;
        _enabled.ForeColor = Color.FromArgb(11, 0, 35);
        _enabled.Location = new Point(330, 175);
        imageGroup.Controls.AddRange([_preview, uploadButton, _fileLabel, nameLabel, _name, _enabled]);

        var scheduleGroup = new GroupBox
        {
            Text = "Schedule",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(183, 68, 255),
            Bounds = new Rectangle(25, 292, 740, 310)
        };
        _specificDates.Text = "Run for specific dates";
        _specificDates.AutoSize = true;
        _specificDates.Location = new Point(22, 28);
        _weekly.Text = "Repeat every week";
        _weekly.AutoSize = true;
        _weekly.Location = new Point(235, 28);
        _specificDates.CheckedChanged += (_, _) => UpdateScheduleControls();
        _weekly.CheckedChanged += (_, _) => UpdateScheduleControls();

        var specificPanel = new GroupBox
        {
            Name = "specificPanel", Text = "Specific date and time range",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 0, 196),
            Bounds = new Rectangle(18, 57, 700, 125)
        };
        specificPanel.Controls.AddRange([
            MakeLabel("Date", 90, 24), MakeLabel("Time", 365, 24),
            MakeLabel("Starts:", 20, 50), MakeLabel("Ends:", 20, 88)]);
        ConfigureDatePicker(_startDate, 90, 43, 245);
        ConfigureTimePicker(_startTime, 365, 43, 140);
        ConfigureDatePicker(_endDate, 90, 81, 245);
        ConfigureTimePicker(_endTime, 365, 81, 140);
        specificPanel.Controls.AddRange([_startDate, _startTime, _endDate, _endTime]);

        var weeklyPanel = new GroupBox
        {
            Name = "weeklyPanel", Text = "Weekly repeating schedule",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 0, 196),
            Bounds = new Rectangle(18, 190, 700, 100)
        };
        var dayNames = new[]
        {
            (DayOfWeek.Sunday, "Sun"), (DayOfWeek.Monday, "Mon"),
            (DayOfWeek.Tuesday, "Tue"), (DayOfWeek.Wednesday, "Wed"),
            (DayOfWeek.Thursday, "Thu"), (DayOfWeek.Friday, "Fri"),
            (DayOfWeek.Saturday, "Sat")
        };
        for (var i = 0; i < dayNames.Length; i++)
        {
            var check = new CheckBox
            {
                Text = dayNames[i].Item2, AutoSize = true,
                ForeColor = Color.FromArgb(11, 0, 35), Location = new Point(18 + i * 85, 28)
            };
            _dayChecks[dayNames[i].Item1] = check;
            weeklyPanel.Controls.Add(check);
        }
        weeklyPanel.Controls.AddRange([
            MakeLabel("Daily start:", 18, 68), MakeLabel("Daily end:", 295, 68)]);
        ConfigureTimePicker(_weeklyStart, 105, 63, 130);
        ConfigureTimePicker(_weeklyEnd, 382, 63, 130);
        weeklyPanel.Controls.AddRange([_weeklyStart, _weeklyEnd]);
        scheduleGroup.Controls.AddRange([_specificDates, _weekly, specificPanel, weeklyPanel]);

        var saveButton = new Button
        {
            Text = "Save Advertisement", Bounds = new Rectangle(455, 625, 170, 42),
            BackColor = Color.FromArgb(172, 208, 55), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        saveButton.Click += (_, _) => SaveAndClose();
        var cancelButton = new Button
        {
            Text = "Cancel", Bounds = new Rectangle(635, 625, 130, 42),
            DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([heading, imageGroup, scheduleGroup, saveButton, cancelButton]);
        FormClosed += (_, _) => _preview.Image?.Dispose();
        LoadWorkingValues();
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(11, 0, 35), Location = new Point(x, y)
    };

    private static void ConfigureDatePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Short;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private static void ConfigureTimePicker(DateTimePicker picker, int x, int y, int width = 120)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "h:mm tt";
        picker.ShowUpDown = true;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private void LoadWorkingValues()
    {
        _name.Text = _working.Name;
        _enabled.Checked = _working.Enabled;
        _specificDates.Checked = _working.ScheduleType == AdvertisementScheduleType.SpecificDates;
        _weekly.Checked = _working.ScheduleType == AdvertisementScheduleType.Weekly;
        _startDate.Value = _working.StartDateTime.Date;
        _startTime.Value = DateTime.Today.Add(_working.StartDateTime.TimeOfDay);
        _endDate.Value = _working.EndDateTime.Date;
        _endTime.Value = DateTime.Today.Add(_working.EndDateTime.TimeOfDay);
        _weeklyStart.Value = DateTime.Today.Add(_working.DailyStartTime);
        _weeklyEnd.Value = DateTime.Today.Add(_working.DailyEndTime);
        foreach (var pair in _dayChecks) pair.Value.Checked = _working.DaysOfWeek.Contains(pair.Key);
        _fileLabel.Text = string.IsNullOrWhiteSpace(_working.ImageFileName)
            ? "No JPG selected." : "Saved JPG loaded.";
        LoadPreview(AdvertisementFiles.GetSafePath(_working.ImageFileName));
        UpdateScheduleControls();
    }

    private void SelectJpeg()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a JPG Advertisement",
            Filter = "JPEG images (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > 25_000_000) throw new InvalidOperationException("The JPG must be smaller than 25 MB.");
            using (var image = Image.FromFile(picker.FileName))
            {
                if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                    throw new InvalidOperationException("The selected file is not a valid JPG image.");
            }
            _selectedSourcePath = picker.FileName;
            _fileLabel.Text = Path.GetFileName(picker.FileName);
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "Advertisement")
                _name.Text = Path.GetFileNameWithoutExtension(picker.FileName);
            LoadPreview(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "JPG Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadPreview(string? path)
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var image = Image.FromFile(path);
            _preview.Image = new Bitmap(image);
        }
        catch
        {
            _fileLabel.Text = "The JPG could not be previewed.";
        }
    }

    private void UpdateScheduleControls()
    {
        var specificPanel = Controls.Find("specificPanel", true).FirstOrDefault();
        var weeklyPanel = Controls.Find("weeklyPanel", true).FirstOrDefault();
        if (specificPanel is not null) specificPanel.Enabled = _specificDates.Checked;
        if (weeklyPanel is not null) weeklyPanel.Enabled = _weekly.Checked;
    }

    private void SaveAndClose()
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for the advertisement.", "Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.ImageFileName) && string.IsNullOrWhiteSpace(_selectedSourcePath))
        {
            MessageBox.Show(this, "Upload a JPG advertisement before saving.", "Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var start = _startDate.Value.Date + _startTime.Value.TimeOfDay;
        var end = _endDate.Value.Date + _endTime.Value.TimeOfDay;
        var selectedDays = _dayChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray();
        if (_specificDates.Checked && end <= start)
        {
            MessageBox.Show(this, "The ending date and time must be after the starting date and time.",
                "Advertisement Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_weekly.Checked && selectedDays.Length == 0)
        {
            MessageBox.Show(this, "Select at least one day for the weekly schedule.",
                "Advertisement Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedSourcePath))
                _working.ImageFileName = AdvertisementFiles.ImportJpeg(_selectedSourcePath);
            _working.Name = name;
            _working.Enabled = _enabled.Checked;
            _working.ScheduleType = _weekly.Checked
                ? AdvertisementScheduleType.Weekly : AdvertisementScheduleType.SpecificDates;
            _working.StartDateTime = start;
            _working.EndDateTime = end;
            _working.DaysOfWeek = selectedDays;
            _working.DailyStartTime = _weeklyStart.Value.TimeOfDay;
            _working.DailyEndTime = _weeklyEnd.Value.TimeOfDay;
            _working.Normalize();
            Advertisement = _working;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The JPG could not be saved.\n\n" + ex.Message,
                "Advertisement", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class PinEntryDialog : Form
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    public string Pin => _pin.Text;

    public PinEntryDialog()
    {
        Text = "Staff Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 190);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Enter the staff password to open settings.",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Bounds = new Rectangle(25, 22, 370, 32)
        };
        var pinLabel = new Label { Text = "Staff Password:", AutoSize = true, Location = new Point(18, 80) };
        _pin.Location = new Point(155, 75);
        var cleaning = false;
        _pin.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        _pin.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(_pin.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == _pin.Text) return;
            cleaning = true;
            _pin.Text = numbersOnly;
            _pin.SelectionStart = _pin.Text.Length;
            cleaning = false;
        };

        var exit = new Button { Text = "Open Staff Settings", Bounds = new Rectangle(145, 130, 165, 36), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(316, 130, 80, 36), DialogResult = DialogResult.Cancel };

        AcceptButton = exit;
        CancelButton = cancel;
        Controls.AddRange([heading, pinLabel, _pin, exit, cancel]);
        Shown += (_, _) => _pin.Focus();
    }
}

internal static class KioskLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(KioskSettings.DataDirectory);
                var path = Path.Combine(KioskSettings.DataDirectory, "kiosk.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never prevent the kiosk from running.
        }
    }
}
