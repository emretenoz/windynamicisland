using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.Win32;
using Windows.Foundation.Metadata;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinDynamicIsland;

public partial class MainWindow : Window
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryName = "WinDynamicIsland";
    private const string CapabilityAccessPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNchittest = 0x0084;
    private const int WmClipboardUpdate = 0x031D;
    private const int Httransparent = -1;
    private const int SwShowMinimized = 2;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const string WeatherCity = "Istanbul";
    private static readonly HttpClient HttpClient = new();

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _observedMediaSessions = new();
    private CoreAudioController? _audioController;
    private CoreAudioDevice? _defaultPlaybackDevice;
    private UserNotificationListener? _notificationListener;
    private CancellationTokenSource? _notificationCollapseCts;
    private CancellationTokenSource? _utilityCollapseCts;
    private DispatcherTimer? _fullscreenWatcher;
    private DispatcherTimer? _privacyWatcher;
    private DispatcherTimer? _notificationWatcher;
    private DispatcherTimer? _timerTimer;
    private DispatcherTimer? _weatherWatcher;
    private readonly HashSet<uint> _seenNotificationIds = new();
    private readonly List<string> _clipboardHistory = new();
    private IslandState _state = IslandState.MediaCompact;
    private DateTimeOffset? _timerStartedAt;
    private DateTimeOffset? _timerEndsAt;
    private bool _hasMediaSession;
    private bool _isMediaActive;
    private bool _isCameraActive;
    private bool _isMicrophoneActive;
    private bool _isHovering;
    private bool _isHiddenForFullscreen;
    private bool _showedNotificationReadError;
    private double _screenshotPreviewWidth = 510;
    private double _screenshotPreviewHeight = 190;

    private bool IsTimerActive => _timerEndsAt is not null;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        HideFromAltTab();
        AddTransparentHitTestSupport();
        AddClipboardListener();
        RefreshStartupMenuState();
        InitializeAudio();
        StartFullscreenWatcher();
        StartPrivacyWatcher();
        StartWeatherWatcher();
        await InitializeMediaAsync();
        TransitionTo(IslandState.MediaCompact);
        await InitializeNotificationsAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_mediaManager is not null)
        {
            _mediaManager.CurrentSessionChanged -= MediaManager_CurrentSessionChanged;
        }

        DetachAllMediaEvents();

        if (_notificationListener is not null)
        {
            _notificationListener.NotificationChanged -= NotificationListener_NotificationChanged;
        }

        _notificationCollapseCts?.Cancel();
        _notificationCollapseCts?.Dispose();
        _utilityCollapseCts?.Cancel();
        _utilityCollapseCts?.Dispose();
        _fullscreenWatcher?.Stop();
        _privacyWatcher?.Stop();
        _notificationWatcher?.Stop();
        _timerTimer?.Stop();
        _weatherWatcher?.Stop();
    }

    private void PositionAtTopCenter()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void HideFromAltTab()
    {
        var helper = new WindowInteropHelper(this);
        var style = GetWindowLong(helper.Handle, GwlExStyle);
        SetWindowLong(helper.Handle, GwlExStyle, style | WsExToolWindow);
    }

    private void AddTransparentHitTestSupport()
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private void AddClipboardListener()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            AddClipboardFormatListener(helper.Handle);
        }
    }

    private void InitializeAudio()
    {
        try
        {
            _audioController = new CoreAudioController();
            _defaultPlaybackDevice = _audioController.DefaultPlaybackDevice;
        }
        catch
        {
            _audioController = null;
            _defaultPlaybackDevice = null;
        }
    }

    private void StartFullscreenWatcher()
    {
        _fullscreenWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _fullscreenWatcher.Tick += (_, _) => UpdateFullscreenVisibility();
        _fullscreenWatcher.Start();
    }

    private void StartPrivacyWatcher()
    {
        _privacyWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _privacyWatcher.Tick += (_, _) => UpdatePrivacyIndicators();
        _privacyWatcher.Start();
        UpdatePrivacyIndicators();
    }

    private void StartWeatherWatcher()
    {
        _weatherWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(20)
        };
        _weatherWatcher.Tick += async (_, _) => await ShowIdleWeatherAsync();
        _weatherWatcher.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
            Dispatcher.Invoke(async () => await ShowIdleWeatherAsync()));
    }

    private void UpdatePrivacyIndicators()
    {
        var cameraActive = IsCapabilityInUse("webcam");
        var microphoneActive = IsCapabilityInUse("microphone");
        if (_isCameraActive == cameraActive && _isMicrophoneActive == microphoneActive)
        {
            return;
        }

        _isCameraActive = cameraActive;
        _isMicrophoneActive = microphoneActive;

        CameraDot.Visibility = cameraActive ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneDot.Visibility = microphoneActive ? Visibility.Visible : Visibility.Collapsed;
        PrivacyIndicator.Visibility = cameraActive || microphoneActive ? Visibility.Visible : Visibility.Collapsed;
        CompactSplitDivider.Visibility = _isMediaActive && (cameraActive || microphoneActive) ? Visibility.Visible : Visibility.Collapsed;

        if (_state is IslandState.MediaCompact)
        {
            AnimateIsland(GetCompactWidth(), GetCompactHeight(), GetCompactHeight() / 2);
        }
    }

    private static bool IsCapabilityInUse(string capabilityName)
    {
        return IsCapabilityInUse(RegistryHive.CurrentUser, capabilityName) ||
               IsCapabilityInUse(RegistryHive.LocalMachine, capabilityName);
    }

    private static bool IsCapabilityInUse(RegistryHive hive, string capabilityName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var capabilityKey = baseKey.OpenSubKey($@"{CapabilityAccessPath}\{capabilityName}");
            return capabilityKey is not null && IsCapabilityKeyInUse(capabilityKey);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCapabilityKeyInUse(RegistryKey key)
    {
        if (IsLastUsedOpen(key))
        {
            return true;
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null && IsCapabilityKeyInUse(subKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLastUsedOpen(RegistryKey key)
    {
        var start = ToLong(key.GetValue("LastUsedTimeStart"));
        var stop = ToLong(key.GetValue("LastUsedTimeStop"));
        return start > 0 && stop == 0;
    }

    private static long ToLong(object? value)
    {
        return value switch
        {
            long number => number,
            int number => number,
            string text when long.TryParse(text, out var number) => number,
            _ => -1
        };
    }

    private void UpdateFullscreenVisibility()
    {
        var shouldHide = IsAnotherWindowFullscreen();
        if (shouldHide == _isHiddenForFullscreen)
        {
            return;
        }

        _isHiddenForFullscreen = shouldHide;
        AnimateFullscreenVisibility(shouldHide);
    }

    private void AnimateFullscreenVisibility(bool hide)
    {
        var ease = (QuadraticEase)Resources["IslandEase"];
        var duration = TimeSpan.FromMilliseconds(hide ? 180 : 240);

        if (!hide)
        {
            Visibility = Visibility.Visible;
            Island.Opacity = 0;
            IslandScale.ScaleX = 0.06;
            IslandScale.ScaleY = 0.82;
        }

        var scaleXAnimation = new DoubleAnimation(hide ? 0.06 : 1, duration)
        {
            EasingFunction = ease
        };
        var scaleYAnimation = new DoubleAnimation(hide ? 0.82 : 1, duration)
        {
            EasingFunction = ease
        };
        var opacityAnimation = new DoubleAnimation(hide ? 0 : 1, duration)
        {
            EasingFunction = ease
        };

        if (hide)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (_isHiddenForFullscreen)
                {
                    Visibility = Visibility.Hidden;
                }
            };
        }

        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
        Island.BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private bool IsAnotherWindowFullscreen()
    {
        var foreground = GetForegroundWindow();
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (foreground == IntPtr.Zero ||
            foreground == ownHandle ||
            !IsWindowVisible(foreground) ||
            IsIconic(foreground) ||
            !GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var monitorInfo = MonitorInfo.Create();
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var monitorRect = monitorInfo.Monitor;
        const int tolerance = 2;
        var fillsMonitor = windowRect.Left <= monitorRect.Left + tolerance &&
               windowRect.Top <= monitorRect.Top + tolerance &&
               windowRect.Right >= monitorRect.Right - tolerance &&
               windowRect.Bottom >= monitorRect.Bottom - tolerance;
        if (!fillsMonitor)
        {
            return false;
        }

        var placement = WindowPlacement.Create();
        return !GetWindowPlacement(foreground, ref placement) || placement.ShowCmd != SwShowMinimized;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            HandleClipboardUpdate();
            return IntPtr.Zero;
        }

        if (msg != WmNchittest)
        {
            return IntPtr.Zero;
        }

        var point = GetPointFromLParam(lParam);
        var relative = Island.PointFromScreen(point);
        var bounds = new Rect(0, 0, Island.ActualWidth, Island.ActualHeight);
        if (bounds.Contains(relative))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(Httransparent);
    }

    private static Point GetPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private void HandleClipboardUpdate()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _clipboardHistory.Remove(text);
                _clipboardHistory.Insert(0, text);
                if (_clipboardHistory.Count > 5)
                {
                    _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);
                }

                ShowUtility("Pano", TrimForIsland(text.ReplaceLineEndings(" "), 72), "C", 1, TimeSpan.FromSeconds(2));
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                _clipboardHistory.Insert(0, "[Gorsel]");
                if (_clipboardHistory.Count > 5)
                {
                    _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);
                }

                ShowScreenshotPreview(image);
            }
        }
        catch
        {
        }
    }

    private async Task InitializeMediaAsync()
    {
        _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mediaManager.CurrentSessionChanged += MediaManager_CurrentSessionChanged;
        SyncMediaSessions();
        await SelectBestMediaSessionAsync();
        await RefreshMediaAsync();
    }

    private async Task InitializeNotificationsAsync()
    {
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                return;
            }

            _notificationListener = UserNotificationListener.Current;
            var access = await _notificationListener.RequestAccessAsync();
            if (access is not UserNotificationListenerAccessStatus.Allowed)
            {
                ShowNotificationAccessIssue(access);
                return;
            }

            await SeedExistingNotificationsAsync();
            TrySubscribeNotificationEvents();
            StartNotificationWatcher();
        }
        catch
        {
            _notificationListener = null;
            ShowNotification("WinDynamicIsland", "Bildirim sistemi okunamadi");
        }
    }

    private void TrySubscribeNotificationEvents()
    {
        if (_notificationListener is null)
        {
            return;
        }

        try
        {
            _notificationListener.NotificationChanged += NotificationListener_NotificationChanged;
        }
        catch
        {
            // Some unpackaged WPF apps can poll notifications but cannot subscribe to this WinRT event.
        }
    }

    private void ShowNotificationAccessIssue(UserNotificationListenerAccessStatus access)
    {
        var message = access switch
        {
            UserNotificationListenerAccessStatus.Denied => "Bildirim izni reddedildi",
            UserNotificationListenerAccessStatus.Unspecified => "Bildirim izni verilmedi",
            _ => "Bildirim izni kapali"
        };

        ShowNotification("WinDynamicIsland", message);
    }

    private async Task SeedExistingNotificationsAsync()
    {
        if (_notificationListener is null)
        {
            return;
        }

        try
        {
            var notifications = await _notificationListener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notification in notifications)
            {
                _seenNotificationIds.Add(notification.Id);
            }
        }
        catch
        {
            if (!_showedNotificationReadError)
            {
                _showedNotificationReadError = true;
                ShowNotification("WinDynamicIsland", "Bildirim listesi okunamadi");
            }
        }
    }

    private void StartNotificationWatcher()
    {
        _notificationWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _notificationWatcher.Tick += async (_, _) => await PollNotificationsAsync();
        _notificationWatcher.Start();
    }

    private async Task PollNotificationsAsync()
    {
        if (_notificationListener is null)
        {
            return;
        }

        try
        {
            var notifications = await _notificationListener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notification in notifications.OrderBy(item => item.Id))
            {
                if (!_seenNotificationIds.Add(notification.Id))
                {
                    continue;
                }

                await TryShowNotificationAsync(notification);
            }
        }
        catch
        {
            if (!_showedNotificationReadError)
            {
                _showedNotificationReadError = true;
                ShowNotification("WinDynamicIsland", "Bildirim listesi okunamadi");
            }
        }
    }

    private void NotificationListener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind is not UserNotificationChangedKind.Added)
        {
            return;
        }

        try
        {
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null)
            {
                return;
            }

            _seenNotificationIds.Add(notification.Id);
            _ = Dispatcher.InvokeAsync(async () => await TryShowNotificationAsync(notification));
        }
        catch
        {
        }
    }

    private async Task TryShowNotificationAsync(UserNotification notification)
    {
        var preview = await ParseNotificationAsync(notification);
        if (string.IsNullOrWhiteSpace(preview.Message))
        {
            return;
        }

        if (ShouldSuppressNotification(preview.AppName, preview.Message))
        {
            return;
        }

        ShowNotification(preview.AppName, preview.Message, preview.Logo);
    }

    private static bool ShouldSuppressNotification(string appName, string message)
    {
        var value = $"{appName} {message}".ToLowerInvariant();
        return value.Contains("snipping tool") ||
               value.Contains("screen snip") ||
               value.Contains("screenshot saved") ||
               value.Contains("screenshot copied") ||
               value.Contains("ekran alintisi") ||
               value.Contains("ekran alıntısı");
    }

    private static async Task<NotificationPreview> ParseNotificationAsync(UserNotification notification)
    {
        var appName = notification.AppInfo?.DisplayInfo.DisplayName;
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "Notification";
        }

        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        var parts = binding?.GetTextElements()
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray() ?? Array.Empty<string>();

        BitmapImage? logo = null;
        try
        {
            var logoReference = notification.AppInfo?.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
            if (logoReference is not null)
            {
                logo = await LoadBitmapAsync(logoReference);
            }
        }
        catch
        {
        }

        return new NotificationPreview(appName, string.Join(" - ", parts), logo);
    }

    private void ShowNotification(string appName, string message, ImageSource? logo = null)
    {
        NotificationAppText.Text = TrimForIsland(appName, 34);
        NotificationMessageText.Text = TrimForIsland(message, 78);
        NotificationLogoImage.Source = logo;
        NotificationLogoImage.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        NotificationFallbackIcon.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        TransitionTo(IslandState.Notification);

        _notificationCollapseCts?.Cancel();
        _notificationCollapseCts?.Dispose();
        _notificationCollapseCts = new CancellationTokenSource();
        _ = CollapseNotificationLaterAsync(_notificationCollapseCts.Token);
    }

    private void ShowUtility(string title, string detail, string icon, double progress = 0, TimeSpan? duration = null)
    {
        UtilityTitleText.Text = TrimForIsland(title, 34);
        UtilityDetailText.Text = TrimForIsland(detail, 78);
        UtilityIconText.Text = icon;
        UtilityProgressFill.Width = Math.Clamp(progress, 0, 1) * 210;
        TransitionTo(IslandState.Utility);

        _utilityCollapseCts?.Cancel();
        _utilityCollapseCts?.Dispose();
        _utilityCollapseCts = new CancellationTokenSource();
        _ = CollapseUtilityLaterAsync(duration ?? TimeSpan.FromSeconds(3), _utilityCollapseCts.Token);
    }

    private void ShowScreenshotPreview(BitmapSource? image)
    {
        if (image is null)
        {
            ShowUtility("Screenshot", "SS panoya kopyalandi", "S", 1, TimeSpan.FromSeconds(2.4));
            return;
        }

        ScreenshotPreviewImage.Source = image;
        SetScreenshotPreviewSize(image);
        TransitionTo(IslandState.ScreenshotPreview);

        _utilityCollapseCts?.Cancel();
        _utilityCollapseCts?.Dispose();
        _utilityCollapseCts = new CancellationTokenSource();
        _ = CollapseUtilityLaterAsync(TimeSpan.FromSeconds(3.2), _utilityCollapseCts.Token);
    }

    private void SetScreenshotPreviewSize(BitmapSource image)
    {
        var aspect = image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 1.6;
        if (aspect < 0.8)
        {
            var imageHeight = 340d;
            _screenshotPreviewWidth = Math.Clamp(imageHeight * aspect + 24, 220, 330);
            _screenshotPreviewHeight = 390;
            return;
        }

        if (aspect > 1.35)
        {
            var imageWidth = 486d;
            _screenshotPreviewWidth = 510;
            _screenshotPreviewHeight = Math.Clamp(imageWidth / aspect + 50, 170, 270);
            return;
        }

        _screenshotPreviewWidth = 380;
        _screenshotPreviewHeight = 380;
    }

    private async Task CollapseUtilityLaterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && _state is IslandState.Utility or IslandState.ScreenshotPreview)
            {
                TransitionTo(_isHovering && _hasMediaSession ? IslandState.MediaExpanded : IslandState.MediaCompact);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task CollapseNotificationLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(4800, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && _state is IslandState.Notification)
            {
                TransitionTo(_isHovering ? IslandState.MediaExpanded : IslandState.MediaCompact);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task ShowIdleWeatherAsync()
    {
        if (_state is not IslandState.MediaCompact || _isMediaActive || _isCameraActive || _isMicrophoneActive || _isHovering)
        {
            return;
        }

        try
        {
            using var response = await HttpClient.GetAsync($"https://wttr.in/{WeatherCity}?format=j1");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var current = json.RootElement.GetProperty("current_condition")[0];
            var temp = current.GetProperty("temp_C").GetString();
            var desc = current.GetProperty("weatherDesc")[0].GetProperty("value").GetString();
            if (!string.IsNullOrWhiteSpace(temp) && !string.IsNullOrWhiteSpace(desc))
            {
                ShowUtility("Hava Durumu", $"{WeatherCity} {temp}C - {desc}", "W", 1, TimeSpan.FromSeconds(4));
            }
        }
        catch
        {
        }
    }

    private void MediaManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Dispatcher.Invoke(async () =>
        {
            SyncMediaSessions();
            await SelectBestMediaSessionAsync();
            await RefreshMediaAsync();
        });
    }

    private void SyncMediaSessions()
    {
        if (_mediaManager is null)
        {
            return;
        }

        foreach (var session in _mediaManager.GetSessions())
        {
            if (_observedMediaSessions.Add(session))
            {
                session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            }
        }
    }

    private void DetachAllMediaEvents()
    {
        foreach (var session in _observedMediaSessions)
        {
            session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        }

        _observedMediaSessions.Clear();
    }

    private async Task SelectBestMediaSessionAsync()
    {
        if (_mediaManager is null)
        {
            _mediaSession = null;
            return;
        }

        var sessions = _mediaManager.GetSessions().ToArray();
        var scoredSessions = new List<(GlobalSystemMediaTransportControlsSession Session, int Score)>();
        foreach (var session in sessions)
        {
            scoredSessions.Add((session, await GetMediaSessionScoreAsync(session)));
        }

        _mediaSession = scoredSessions
            .OrderByDescending(item => item.Score)
            .Select(item => item.Session)
            .FirstOrDefault() ?? _mediaManager.GetCurrentSession();
    }

    private async Task<int> GetMediaSessionScoreAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var score = 0;
        var playbackStatus = session.GetPlaybackInfo().PlaybackStatus;
        if (playbackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            score += 100;
        }
        else if (playbackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            score += 20;
        }

        if (session == _mediaManager?.GetCurrentSession())
        {
            score += 10;
        }

        if (string.Equals(session.SourceAppUserModelId, _mediaSession?.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var foregroundTitle = GetForegroundWindowTitle();
            if (TitleLooksLikeForegroundMedia(properties.Title, foregroundTitle))
            {
                score += 80;
            }
        }
        catch
        {
        }

        return score;
    }

    private static bool TitleLooksLikeForegroundMedia(string? mediaTitle, string foregroundTitle)
    {
        if (string.IsNullOrWhiteSpace(mediaTitle) || string.IsNullOrWhiteSpace(foregroundTitle))
        {
            return false;
        }

        var normalizedMediaTitle = NormalizeComparableText(mediaTitle);
        var normalizedForegroundTitle = NormalizeComparableText(foregroundTitle);
        return normalizedMediaTitle.Length >= 8 &&
               normalizedForegroundTitle.Contains(normalizedMediaTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableText(string value)
    {
        var suffixes = new[] { " - youtube", " - brave", " - google chrome", " - microsoft edge" };
        var normalized = value.Trim();
        foreach (var suffix in suffixes)
        {
            var index = normalized.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                normalized = normalized[..index];
            }
        }

        return normalized.Trim();
    }

    private static string GetForegroundWindowTitle()
    {
        const int maxTitleLength = 512;
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        var title = new System.Text.StringBuilder(maxTitleLength);
        return GetWindowText(handle, title, title.Capacity) > 0 ? title.ToString() : string.Empty;
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Dispatcher.Invoke(async () =>
        {
            SyncMediaSessions();
            await SelectBestMediaSessionAsync();
            await RefreshMediaAsync();
        });
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Dispatcher.Invoke(async () =>
        {
            SyncMediaSessions();
            await SelectBestMediaSessionAsync();
            await RefreshMediaAsync();
        });
    }

    private async Task RefreshMediaAsync()
    {
        try
        {
            if (_mediaSession is null)
            {
                _hasMediaSession = false;
                SetMediaActive(false);
                SetPlayPauseIcon(false);
                SetTrackText("No media", "");
                SourceAppText.Text = "Media";
                AlbumArtImage.Source = null;
                return;
            }

            _hasMediaSession = true;
            var playbackInfo = _mediaSession.GetPlaybackInfo();
            var isPlaying = playbackInfo.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            SetMediaActive(isPlaying);
            SetPlayPauseIcon(isPlaying);

            var properties = await _mediaSession.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(properties.Title) ? "Media playing" : properties.Title;
            var artist = string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist;
            SetTrackText(title, string.IsNullOrWhiteSpace(artist) ? "Unknown artist" : artist);
            SourceAppText.Text = GetFriendlySourceAppName(_mediaSession.SourceAppUserModelId);

            if (properties.Thumbnail is not null)
            {
                var albumArt = await LoadBitmapAsync(properties.Thumbnail);
                AlbumArtImage.Source = albumArt;
                ApplyAmbientColor(albumArt);
            }
        }
        catch
        {
            _hasMediaSession = false;
            SetMediaActive(false);
            SetPlayPauseIcon(false);
            SetTrackText("No media", "");
            SourceAppText.Text = "Media";
        }
    }

    private void SetMediaActive(bool isActive)
    {
        var changed = _isMediaActive != isActive;
        _isMediaActive = isActive;
        CompactMediaIndicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        PrivacyIndicator.Visibility = _isCameraActive || _isMicrophoneActive ? Visibility.Visible : Visibility.Collapsed;
        CompactSplitDivider.Visibility = isActive && (_isCameraActive || _isMicrophoneActive) ? Visibility.Visible : Visibility.Collapsed;

        var mediaPulse = (Storyboard)Resources["MediaPlayingStoryboard"];
        if (isActive)
        {
            mediaPulse.Begin(this, true);
        }
        else
        {
            mediaPulse.Stop(this);
            CompactBarOne.Height = 6;
            CompactBarTwo.Height = 10;
            CompactBarThree.Height = 18;
            CompactBarFour.Height = 8;
            CompactBarFive.Height = 14;
        }

        if (changed && _state is IslandState.MediaCompact)
        {
            AnimateIsland(GetCompactWidth(), GetCompactHeight(), GetCompactHeight() / 2);
        }
    }

    private void SetPlayPauseIcon(bool isPlaying)
    {
        PlayPauseIconPath.Data = Geometry.Parse(isPlaying
            ? "M 2 1 L 2 12 L 5 12 L 5 1 Z M 8 1 L 8 12 L 11 12 L 11 1 Z"
            : "M 3 1.5 L 11 6.5 L 3 11.5 Z");
    }

    private void SetTrackText(string title, string artist)
    {
        ExpandedTitleText.Text = title;
        ExpandedArtistText.Text = artist;
    }

    private static string GetFriendlySourceAppName(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return "Media";
        }

        var source = sourceAppUserModelId.ToLowerInvariant();
        if (source.Contains("spotify"))
        {
            return "Spotify";
        }

        if (source.Contains("chrome"))
        {
            return "Chrome";
        }

        if (source.Contains("edge"))
        {
            return "Edge";
        }

        if (source.Contains("firefox"))
        {
            return "Firefox";
        }

        if (source.Contains("vlc"))
        {
            return "VLC";
        }

        if (source.Contains("zune"))
        {
            return "Media Player";
        }

        var trimmed = sourceAppUserModelId.Split('!', StringSplitOptions.RemoveEmptyEntries)[0];
        var lastSegment = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? trimmed;
        return TrimForIsland(lastSegment.Replace("_", " "), 22);
    }

    private static async Task<BitmapImage?> LoadBitmapAsync(IRandomAccessStreamReference thumbnail)
    {
        await using var readable = (await thumbnail.OpenReadAsync()).AsStreamForRead();
        await using var memory = new MemoryStream();
        await readable.CopyToAsync(memory);
        memory.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memory;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ApplyAmbientColor(BitmapSource? bitmap)
    {
        if (bitmap is null)
        {
            IslandStroke.BorderBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
            CompactBarThree.Background = new SolidColorBrush(Color.FromRgb(29, 185, 84));
            return;
        }

        try
        {
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(0.08, 0.08));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            long red = 0;
            long green = 0;
            long blue = 0;
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                if (r + g + b < 70)
                {
                    continue;
                }

                red += r;
                green += g;
                blue += b;
                count++;
            }

            if (count == 0)
            {
                return;
            }

            var color = Color.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
            IslandStroke.BorderBrush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
            CompactBarThree.Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));
        }
        catch
        {
        }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovering = true;
        if (_state is IslandState.MediaCompact && (_hasMediaSession || IsTimerActive))
        {
            if (!_hasMediaSession && IsTimerActive)
            {
                SetTrackText("Timer", "Countdown");
                SourceAppText.Text = "WinDynamicIsland";
                AlbumArtImage.Source = null;
            }

            UpdateTimer();
            TransitionTo(IslandState.MediaExpanded);
        }
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovering = false;
        if (_state is IslandState.MediaExpanded)
        {
            TransitionTo(IslandState.MediaCompact);
        }
    }

    private void Window_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_defaultPlaybackDevice is null)
        {
            InitializeAudio();
        }

        if (_defaultPlaybackDevice is null)
        {
            return;
        }

        var delta = e.Delta > 0 ? 4 : -4;
        _defaultPlaybackDevice.Volume = Math.Clamp(_defaultPlaybackDevice.Volume + delta, 0, 100);
        ShowVolumeUtility(_defaultPlaybackDevice);
        e.Handled = true;
    }

    private void ShowVolumeUtility(CoreAudioDevice device)
    {
        ShowUtility("Ses", $"{Math.Round(device.Volume)}% - {TrimForIsland(device.Name, 32)}", "V", device.Volume / 100, TimeSpan.FromSeconds(2));
    }

    private static string TrimForIsland(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_mediaSession is not null)
        {
            await _mediaSession.TrySkipPreviousAsync();
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_mediaSession is not null)
        {
            await _mediaSession.TryTogglePlayPauseAsync();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_mediaSession is not null)
        {
            await _mediaSession.TrySkipNextAsync();
        }
    }

    private void StartTimer(TimeSpan duration)
    {
        _timerStartedAt = DateTimeOffset.Now;
        _timerEndsAt = DateTimeOffset.Now.Add(duration);
        _timerTimer?.Stop();
        _timerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timerTimer.Tick += (_, _) => UpdateTimer();
        _timerTimer.Start();
        UpdateTimer();
        TransitionTo(IslandState.MediaCompact);
    }

    private void UpdateTimer()
    {
        if (_timerEndsAt is null)
        {
            return;
        }

        var remaining = _timerEndsAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            _timerTimer?.Stop();
            _timerStartedAt = null;
            _timerEndsAt = null;
            TimerPanel.Visibility = Visibility.Collapsed;
            CompactTimerText.Visibility = Visibility.Collapsed;
            ShowNotification("Timer", "Sure bitti");
            if (_state is IslandState.MediaExpanded && !_hasMediaSession)
            {
                TransitionTo(IslandState.MediaCompact);
            }
            return;
        }

        var totalSeconds = Math.Max(1, (_timerEndsAt.Value - (_timerStartedAt ?? DateTimeOffset.Now)).TotalSeconds);
        var progress = Math.Clamp(1 - remaining.TotalSeconds / totalSeconds, 0, 1);
        CompactTimerText.Text = FormatRemaining(remaining);
        CompactTimerText.Visibility = Visibility.Visible;
        TimerPanel.Visibility = Visibility.Visible;
        TimerTimeText.Text = FormatRemaining(remaining);
        TimerProgressFill.Width = progress * 374;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}:{remaining.Minutes:00}";
        }

        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void StopTimer()
    {
        _timerTimer?.Stop();
        _timerStartedAt = null;
        _timerEndsAt = null;
        TimerPanel.Visibility = Visibility.Collapsed;
        CompactTimerText.Visibility = Visibility.Collapsed;
        ShowUtility("Timer", "Durduruldu", "T", 0, TimeSpan.FromSeconds(1.3));
        TransitionTo(IslandState.MediaCompact);
    }

    private void Island_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (_clipboardHistory.Count == 0)
        {
            return;
        }

        ShowUtility("Pano Gecmisi", TrimForIsland(string.Join(" | ", _clipboardHistory.Take(5)), 78), "C", 1, TimeSpan.FromSeconds(4));
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void TransitionTo(IslandState nextState)
    {
        _state = nextState;
        var (width, height) = nextState switch
        {
            IslandState.MediaCompact => (GetCompactWidth(), GetCompactHeight()),
            IslandState.MediaExpanded => (510d, IsTimerActive ? 140d : 108d),
            IslandState.Notification => (330d, 56d),
            IslandState.Utility => (330d, 56d),
            IslandState.ScreenshotPreview => (_screenshotPreviewWidth, _screenshotPreviewHeight),
            _ => (190d, 36d)
        };

        AnimateIsland(width, height, GetCornerRadius(nextState, height));
        TimerPanel.Visibility = nextState is IslandState.MediaExpanded && IsTimerActive ? Visibility.Visible : Visibility.Collapsed;
        SetView(MediaCompactView, nextState is IslandState.MediaCompact);
        SetView(MediaExpandedView, nextState is IslandState.MediaExpanded);
        SetView(NotificationView, nextState is IslandState.Notification);
        SetView(UtilityView, nextState is IslandState.Utility);
        SetView(ScreenshotPreviewView, nextState is IslandState.ScreenshotPreview);

    }

    private double GetCompactWidth()
    {
        if (_isMediaActive)
        {
            if (IsTimerActive)
            {
                return _isCameraActive || _isMicrophoneActive ? 180d : 154d;
            }

            return _isCameraActive || _isMicrophoneActive ? 146d : 124d;
        }

        if (IsTimerActive)
        {
            return _isCameraActive || _isMicrophoneActive ? 108d : 82d;
        }

        return _isCameraActive || _isMicrophoneActive ? 52d : 44d;
    }

    private double GetCompactHeight() => _isMediaActive || IsTimerActive ? 30d : 22d;

    private double GetCornerRadius(IslandState state, double height)
    {
        return state switch
        {
            IslandState.MediaCompact => height / 2,
            IslandState.MediaExpanded => 26d,
            IslandState.Notification => 16d,
            IslandState.Utility => 16d,
            IslandState.ScreenshotPreview => 24d,
            _ => height / 2
        };
    }

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetStartWithWindows(StartupMenuItem.IsChecked);
        RefreshStartupMenuState();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NotificationSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        TryOpenSettings("ms-settings:privacy-notifications");
    }

    private async void SwitchAudioOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SwitchAudioOutputAsync();
    }

    private void TimerFiveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(5);
    }

    private void TimerTenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(10);
    }

    private void TimerFifteenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(15);
    }

    private void TimerThirtyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(30);
    }

    private void TimerFortyFiveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(45);
    }

    private void TimerSixtyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartTimerPreset(60);
    }

    private void StartTimerPreset(double minutes)
    {
        StartTimer(TimeSpan.FromMinutes(minutes));
    }

    private void TimerCustomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TimerInputDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Minutes is { } minutes)
        {
            StartTimer(TimeSpan.FromMinutes(minutes));
        }
    }

    private void TimerStopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
    }

    private async Task SwitchAudioOutputAsync()
    {
        try
        {
            _audioController ??= new CoreAudioController();
            var devices = (await _audioController.GetPlaybackDevicesAsync(DeviceState.Active))
                .Where(device => !device.IsDefaultDevice)
                .ToArray();
            if (devices.Length == 0)
            {
                ShowUtility("Ses Cihazi", "Baska aktif cikis yok", "V", 1);
                return;
            }

            var next = devices[0];
            await _audioController.SetDefaultDeviceAsync(next);
            _defaultPlaybackDevice = next;
            ShowUtility("Ses Cihazi", TrimForIsland(next.Name, 48), "V", next.Volume / 100, TimeSpan.FromSeconds(3));
        }
        catch
        {
            ShowUtility("Ses Cihazi", "Degistirilemedi", "V", 0);
        }
    }

    private static void TryOpenSettings(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void RefreshStartupMenuState()
    {
        StartupMenuItem.IsChecked = IsStartWithWindowsEnabled();
    }

    private static bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
        var configuredPath = key?.GetValue(StartupRegistryName) as string;
        return string.Equals(configuredPath?.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true) ??
                        Registry.CurrentUser.CreateSubKey(StartupRegistryPath);
        if (enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            key?.SetValue(StartupRegistryName, $"\"{Environment.ProcessPath}\"");
            return;
        }

        key?.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
    }

    private void AnimateIsland(double width, double height, double radius)
    {
        var ease = (QuadraticEase)Resources["IslandEase"];
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });

        var cornerAnimation = new CornerRadiusAnimation
        {
            To = new CornerRadius(radius),
            Duration = TimeSpan.FromMilliseconds(230),
            EasingFunction = ease
        };
        Island.BeginAnimation(Border.CornerRadiusProperty, cornerAnimation);
    }

    private static void SetView(UIElement view, bool isVisible)
    {
        view.Visibility = Visibility.Visible;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(isVisible ? 1 : 0, TimeSpan.FromMilliseconds(140)));
        if (!isVisible)
        {
            Task.Delay(150).ContinueWith(_ =>
            {
                view.Dispatcher.Invoke(() =>
                {
                    if (view.Opacity <= 0.01)
                    {
                        view.Visibility = Visibility.Collapsed;
                    }
                });
            });
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}

public enum IslandState
{
    MediaCompact,
    MediaExpanded,
    Notification,
    Utility,
    ScreenshotPreview
}

public sealed record NotificationPreview(string AppName, string Message, ImageSource? Logo);

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor;
    public NativeRect WorkArea;
    public uint Flags;

    public static MonitorInfo Create()
    {
        return new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int ShowCmd;
    public NativePoint MinPosition;
    public NativePoint MaxPosition;
    public NativeRect NormalPosition;

    public static WindowPlacement Create()
    {
        return new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint
{
    public int X;
    public int Y;
}
