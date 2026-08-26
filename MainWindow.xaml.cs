using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private const int Httransparent = -1;
    private const int SwShowMinimized = 2;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private UserNotificationListener? _notificationListener;
    private CancellationTokenSource? _notificationCollapseCts;
    private DispatcherTimer? _fullscreenWatcher;
    private DispatcherTimer? _privacyWatcher;
    private IslandState _state = IslandState.MediaCompact;
    private bool _hasMediaSession;
    private bool _isMediaActive;
    private bool _isCameraActive;
    private bool _isMicrophoneActive;
    private bool _isHovering;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        HideFromAltTab();
        AddTransparentHitTestSupport();
        RefreshStartupMenuState();
        StartFullscreenWatcher();
        StartPrivacyWatcher();
        await InitializeMediaAsync();
        await InitializeNotificationsAsync();
        TransitionTo(IslandState.MediaCompact);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_mediaManager is not null)
        {
            _mediaManager.CurrentSessionChanged -= MediaManager_CurrentSessionChanged;
        }

        if (_notificationListener is not null)
        {
            _notificationListener.NotificationChanged -= NotificationListener_NotificationChanged;
        }

        _notificationCollapseCts?.Cancel();
        _notificationCollapseCts?.Dispose();
        _fullscreenWatcher?.Stop();
        _privacyWatcher?.Stop();
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
        var stop = key.GetValue("LastUsedTimeStop");
        var stopValue = ToLong(stop);

        if (start > 0 && stopValue == 0)
        {
            return true;
        }

        if (start > 0 && stopValue < 0 && WasStartedVeryRecently(start))
        {
            return true;
        }

        return false;
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

    private static bool WasStartedVeryRecently(long fileTime)
    {
        try
        {
            return DateTime.UtcNow - DateTime.FromFileTimeUtc(fileTime) < TimeSpan.FromSeconds(8);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateFullscreenVisibility()
    {
        var shouldHide = IsAnotherWindowFullscreen();
        if (shouldHide && Visibility != Visibility.Hidden)
        {
            Visibility = Visibility.Hidden;
            return;
        }

        if (!shouldHide && Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }
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

    private async Task InitializeMediaAsync()
    {
        _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mediaManager.CurrentSessionChanged += MediaManager_CurrentSessionChanged;
        _mediaSession = _mediaManager.GetCurrentSession();
        AttachMediaEvents(_mediaSession);
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
                return;
            }

            _notificationListener.NotificationChanged += NotificationListener_NotificationChanged;
        }
        catch
        {
            _notificationListener = null;
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

            var preview = ParseNotification(notification);
            if (string.IsNullOrWhiteSpace(preview.Message))
            {
                return;
            }

            Dispatcher.Invoke(() => ShowNotification(preview.AppName, preview.Message));
        }
        catch
        {
        }
    }

    private static NotificationPreview ParseNotification(UserNotification notification)
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

        return new NotificationPreview(appName, string.Join(" - ", parts));
    }

    private void ShowNotification(string appName, string message)
    {
        NotificationAppText.Text = TrimForIsland(appName, 34);
        NotificationMessageText.Text = TrimForIsland(message, 78);
        TransitionTo(IslandState.Notification);

        _notificationCollapseCts?.Cancel();
        _notificationCollapseCts?.Dispose();
        _notificationCollapseCts = new CancellationTokenSource();
        _ = CollapseNotificationLaterAsync(_notificationCollapseCts.Token);
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

    private void MediaManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Dispatcher.Invoke(async () =>
        {
            AttachMediaEvents(_mediaSession, detach: true);
            _mediaSession = sender.GetCurrentSession();
            AttachMediaEvents(_mediaSession);
            await RefreshMediaAsync();
        });
    }

    private void AttachMediaEvents(GlobalSystemMediaTransportControlsSession? session, bool detach = false)
    {
        if (session is null)
        {
            return;
        }

        if (detach)
        {
            session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            return;
        }

        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Dispatcher.Invoke(async () => await RefreshMediaAsync());
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Dispatcher.Invoke(async () => await RefreshMediaAsync());
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
                AlbumArtImage.Source = await LoadBitmapAsync(properties.Thumbnail);
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

        var mediaPulse = (Storyboard)Resources["MediaPlayingStoryboard"];
        if (isActive)
        {
            mediaPulse.Begin(this, true);
            return;
        }

        mediaPulse.Stop(this);
        CompactBarOne.Height = 6;
        CompactBarTwo.Height = 10;
        CompactBarThree.Height = 18;
        CompactBarFour.Height = 8;
        CompactBarFive.Height = 14;

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

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovering = true;
        if (_state is IslandState.MediaCompact && _hasMediaSession)
        {
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

    private void TransitionTo(IslandState nextState)
    {
        _state = nextState;
        var (width, height) = nextState switch
        {
            IslandState.MediaCompact => (GetCompactWidth(), GetCompactHeight()),
            IslandState.MediaExpanded => (500d, 108d),
            IslandState.Notification => (300d, 48d),
            _ => (190d, 36d)
        };

        AnimateIsland(width, height, GetCornerRadius(nextState, height));
        SetView(MediaCompactView, nextState is IslandState.MediaCompact);
        SetView(MediaExpandedView, nextState is IslandState.MediaExpanded);
        SetView(NotificationView, nextState is IslandState.Notification);

    }

    private double GetCompactWidth()
    {
        if (_isMediaActive)
        {
            return 124d;
        }

        return _isCameraActive || _isMicrophoneActive ? 62d : 44d;
    }

    private double GetCompactHeight() => _isMediaActive ? 30d : 22d;

    private double GetCornerRadius(IslandState state, double height)
    {
        return state switch
        {
            IslandState.MediaCompact => height / 2,
            IslandState.MediaExpanded => 28d,
            IslandState.Notification => 16d,
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
    Notification
}

public sealed record NotificationPreview(string AppName, string Message);

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
