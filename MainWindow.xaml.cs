using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfColor = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

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
    private const int WmDisplayChange = 0x007E;
    private const int Httransparent = -1;
    private const int WmClose = 0x0010;
    private const int SwShowMinimized = 2;
    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    private const int WmGetIcon = 0x007F;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int IconSmall = 0;
    private const int GclpHicon = -14;
    private const int GclpHiconSmall = -34;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeTop = 1;
    private const int TopBarHeight = 38;
    private const string WeatherCity = "Istanbul";
    private static readonly HttpClient HttpClient = new();
    private static readonly HashSet<string> ShellLauncherNoiseTitles = new(StringComparer.CurrentCultureIgnoreCase)
    {
        "Adım Kaydedicisi", "App Recovery", "Başlarken", "Bileşen Hizmetleri", "Bilgisayar Yönetimi",
        "Büyüteç", "Canlı açıklamalı alt yazılar", "Çalıştır", "Disk Temizleme", "Dropbox Redeem Launcher",
        "EA app Güncelleyicisi", "EA Error Reporter", "Ekran Klavyesi", "Ekran Okuyucusu", "Feedback Hub",
        "Get Help", "Görev Zamanlayıcı", "Hizmetler", "iSCSI Başlatıcısı", "Karakter Eşlem",
        "Kayıt Defteri Düzenleyicisi", "Kaynak İzleyicisi", "Kurtarma Sürücüsü", "Mixed Reality Portal",
        "ODBC Veri Kaynakları (32-bit)", "ODBC Veri Kaynakları (64-bit)", "Olay Görüntüleyicisi",
        "Performans İzleyicisi", "Power Automate Troubleshooter", "Ses erişimi", "Sistem Bilgisi",
        "Sistem Yapılandırması", "Sürücüleri Birleştir ve İyileştir", "Tıklayarak Yap", "Windows Araçları",
        "Windows Bellek Tanılama", "Windows Faks ve Tarama", "Windows Media Player Legacy",
        "Windows PowerShell", "Windows PowerShell (x86)", "Windows PowerShell ISE", "Windows PowerShell ISE (x86)",
        "Windows Yedekleme", "Wraith W1 Service", "Yazdırma Yönetimi", "Yerel Güvenlik İlkesi"
    };

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _observedMediaSessions = new();
    private CoreAudioController? _audioController;
    private CoreAudioDevice? _defaultPlaybackDevice;
    private IslandSettings _settings = IslandSettings.Load();
    private UserNotificationListener? _notificationListener;
    private CancellationTokenSource? _notificationCollapseCts;
    private CancellationTokenSource? _utilityCollapseCts;
    private DispatcherTimer? _fullscreenWatcher;
    private DispatcherTimer? _privacyWatcher;
    private DispatcherTimer? _notificationWatcher;
    private DispatcherTimer? _timerTimer;
    private DispatcherTimer? _weatherWatcher;
    private DispatcherTimer? _systemStatusWatcher;
    private DispatcherTimer? _topBarWatcher;
    private DispatcherTimer? _dockWatcher;
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
    private bool _systemStatusPrimed;
    private bool _lastCapsLockOn;
    private bool _lastNumLockOn;
    private Forms.PowerLineStatus _lastPowerLineStatus;
    private int _lastBatteryPercent = -1;
    private double _screenshotPreviewWidth = 510;
    private double _screenshotPreviewHeight = 190;
    private bool _appBarRegistered;
    private IntPtr _lastStyledForegroundWindow;
    private DateTime _displayedCalendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedCalendarDate = DateTime.Today;
    private bool _isUpdatingVolumeSlider;
    private readonly Dictionary<string, ImageSource?> _dockIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dockFirstSeenOrder = new(StringComparer.OrdinalIgnoreCase);
    private string _lastDockSignature = string.Empty;
    private uint _lastActiveDockProcessId;
    private int _nextDockOrder;
    private System.Windows.Point? _dockDragStartPoint;
    private List<LauncherApp>? _installedLauncherApps;

    private bool IsTimerActive => _timerEndsAt is not null;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        HideFromAltTab();
        RegisterAppBar();
        AddTransparentHitTestSupport();
        AddClipboardListener();
        RefreshStartupMenuState();
        InitializeAudio();
        StartTopBarWatcher();
        StartDockWatcher();
        StartFullscreenWatcher();
        StartPrivacyWatcher();
        if (_settings.SystemAlertsEnabled)
        {
            StartSystemStatusWatcher();
        }
        if (_settings.WeatherEnabled)
        {
            StartWeatherWatcher();
        }
        await InitializeMediaAsync();
        TransitionTo(IslandState.MediaCompact);
        if (_settings.NotificationsEnabled)
        {
            await InitializeNotificationsAsync();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        UnregisterAppBar();
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
        _systemStatusWatcher?.Stop();
        _topBarWatcher?.Stop();
        _dockWatcher?.Stop();
    }

    private void PositionAtTopCenter()
    {
        var screens = Forms.Screen.AllScreens;
        var screen = screens.Length == 0
            ? Forms.Screen.PrimaryScreen
            : screens[Math.Clamp(_settings.DisplayIndex, 0, screens.Length - 1)];

        var bounds = screen?.Bounds ?? Forms.Screen.PrimaryScreen?.Bounds ?? Forms.SystemInformation.VirtualScreen;
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private void RegisterAppBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!_appBarRegistered)
        {
            var registration = AppBarData.Create(handle);
            SHAppBarMessage(AbmNew, ref registration);
            _appBarRegistered = true;
        }

        var screens = Forms.Screen.AllScreens;
        var screen = screens.Length == 0
            ? Forms.Screen.PrimaryScreen
            : screens[Math.Clamp(_settings.DisplayIndex, 0, screens.Length - 1)];
        var bounds = screen?.Bounds ?? Forms.SystemInformation.VirtualScreen;
        var appBar = AppBarData.Create(handle);
        appBar.Edge = AbeTop;
        appBar.Rect = new NativeRect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Top + TopBarHeight
        };

        SHAppBarMessage(AbmQueryPos, ref appBar);
        appBar.Rect.Top = bounds.Top;
        appBar.Rect.Bottom = bounds.Top + TopBarHeight;
        SHAppBarMessage(AbmSetPos, ref appBar);
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered)
        {
            return;
        }

        var appBar = AppBarData.Create(new WindowInteropHelper(this).Handle);
        SHAppBarMessage(AbmRemove, ref appBar);
        _appBarRegistered = false;
    }

    private void StartTopBarWatcher()
    {
        _topBarWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _topBarWatcher.Tick += (_, _) => UpdateTopBarStatus();
        _topBarWatcher.Start();
        UpdateTopBarStatus();
    }

    private void UpdateTopBarStatus()
    {
        var now = DateTime.Now;
        TopBarClockText.Text = now.ToString("ddd  dd MMM  HH:mm");
        CalendarClockText.Text = now.ToString("HH:mm:ss");
        UpdateTopBarNetworkIcon();

        try
        {
            if (_defaultPlaybackDevice is null)
            {
                TopBarVolumeIcon.Text = "\uE74F";
                TopBarVolumeText.Text = "--%";
            }
            else
            {
                var volume = (int)Math.Round(_defaultPlaybackDevice.Volume);
                TopBarVolumeText.Text = $"{volume}%";
                TopBarVolumeIcon.Text = volume switch
                {
                    <= 0 => "\uE74F",
                    <= 35 => "\uE993",
                    <= 70 => "\uE994",
                    _ => "\uE995"
                };
            }
        }
        catch
        {
            TopBarVolumeIcon.Text = "\uE74F";
            TopBarVolumeText.Text = "--%";
        }

        UpdateTopBarForForegroundApplication();
    }

    private void UpdateTopBarNetworkIcon()
    {
        var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus is OperationalStatus.Up &&
                              network.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
                              network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
            .ToArray();

        if (activeInterfaces.Any(network => network.NetworkInterfaceType is NetworkInterfaceType.Wireless80211))
        {
            TopBarNetworkText.Text = "\uE701";
            TopBarNetworkButton.ToolTip = "Wi-Fi bağlı — ağ ayarlarını aç";
            return;
        }

        if (activeInterfaces.Any(network => network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT))
        {
            TopBarNetworkText.Text = "\uE839";
            TopBarNetworkButton.ToolTip = "Ethernet bağlı — ağ ayarlarını aç";
            return;
        }

        TopBarNetworkText.Text = "\uEB55";
        TopBarNetworkButton.ToolTip = "Bağlantı yok — ağ ayarlarını aç";
    }

    private void UpdateTopBarForForegroundApplication()
    {
        var foreground = GetForegroundWindow();
        var ownHandle = new WindowInteropHelper(this).Handle;
        if (foreground == IntPtr.Zero || foreground == ownHandle || foreground == _lastStyledForegroundWindow)
        {
            return;
        }

        _lastStyledForegroundWindow = foreground;
        try
        {
            var accent = GetWindowContentColor(foreground);
            ApplyTopBarAccent(accent);

            var title = new System.Text.StringBuilder(160);
            if (GetWindowText(foreground, title, title.Capacity) > 0)
            {
                TopBarActivityText.Text = TrimForIsland(title.ToString(), 96);
            }
        }
        catch
        {
        }
    }

    private static WpfColor GetWindowContentColor(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var windowRect))
        {
            return WpfColor.FromRgb(220, 70, 35);
        }

        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        var fullWindowRect = System.Drawing.Rectangle.Intersect(
            new System.Drawing.Rectangle(
                windowRect.Left,
                windowRect.Top,
                Math.Max(1, windowRect.Right - windowRect.Left),
                Math.Max(1, windowRect.Bottom - windowRect.Top)),
            virtualScreen);
        var captureRect = new System.Drawing.Rectangle(
            fullWindowRect.Left,
            fullWindowRect.Top,
            fullWindowRect.Width,
            Math.Min(56, fullWindowRect.Height));
        if (captureRect.Width < 2 || captureRect.Height < 2)
        {
            return WpfColor.FromRgb(220, 70, 35);
        }

        using var screenshot = new System.Drawing.Bitmap(captureRect.Width, captureRect.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(screenshot))
        {
            graphics.CopyFromScreen(captureRect.Location, System.Drawing.Point.Empty, captureRect.Size);
        }

        long red = 0;
        long green = 0;
        long blue = 0;
        long weightTotal = 0;
        var stepX = Math.Max(1, screenshot.Width / 160);
        var stepY = 2;
        for (var y = stepY / 2; y < screenshot.Height; y += stepY)
        {
            for (var x = stepX / 2; x < screenshot.Width; x += stepX)
            {
                var pixel = screenshot.GetPixel(x, y);
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                weightTotal++;
            }
        }

        if (weightTotal == 0)
        {
            return WpfColor.FromRgb(110, 110, 120);
        }

        return WpfColor.FromRgb(
            (byte)(red / weightTotal),
            (byte)(green / weightTotal),
            (byte)(blue / weightTotal));
    }

    private void ApplyTopBarAccent(WpfColor accent)
    {
        TopBar.Background = new SolidColorBrush(WpfColor.FromRgb(accent.R, accent.G, accent.B));
        var highlight = WpfColor.FromRgb(
            (byte)Math.Min(255, accent.R + 42),
            (byte)Math.Min(255, accent.G + 42),
            (byte)Math.Min(255, accent.B + 42));
        TopBar.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(190, highlight.R, highlight.G, highlight.B));
        var accentBrush = new SolidColorBrush(highlight);
        TopBarBrandMark.Foreground = accentBrush;
        DockLauncherMark.Foreground = accentBrush;
        TopBarBrandBadge.Background = new SolidColorBrush(WpfColor.FromArgb(28, highlight.R, highlight.G, highlight.B));
        TopBarBrandBadge.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(42, highlight.R, highlight.G, highlight.B));
    }

    private void TopBarHomeButton_Click(object sender, RoutedEventArgs e) => SettingsMenuItem_Click(sender, e);

    private void TopBarNetworkButton_Click(object sender, RoutedEventArgs e) => TryOpenSettings("ms-settings:network-status");

    private void TopBarVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
    }

    private void VolumePopup_Opened(object sender, EventArgs e)
    {
        if (_defaultPlaybackDevice is null)
        {
            InitializeAudio();
        }

        _isUpdatingVolumeSlider = true;
        var volume = _defaultPlaybackDevice?.Volume ?? 0;
        VolumeSlider.Value = volume;
        UpdateVolumePopupIcon(volume);
        _isUpdatingVolumeSlider = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider || _defaultPlaybackDevice is null)
        {
            return;
        }

        try
        {
            _defaultPlaybackDevice.Volume = e.NewValue;
            UpdateVolumePopupIcon(e.NewValue);
            TopBarVolumeText.Text = $"{Math.Round(e.NewValue)}%";
        }
        catch
        {
        }
    }

    private void UpdateVolumePopupIcon(double volume)
    {
        VolumePopupIcon.Text = volume switch
        {
            <= 0 => "\uE74F",
            <= 35 => "\uE993",
            <= 70 => "\uE994",
            _ => "\uE995"
        };
    }

    private async void VolumeOutputButton_Click(object sender, RoutedEventArgs e)
    {
        VolumePopup.IsOpen = false;
        await PopulateOutputDevicesAsync();
        OutputDevicePopup.IsOpen = true;
    }

    private async Task PopulateOutputDevicesAsync()
    {
        OutputDevicesPanel.Children.Clear();
        try
        {
            _audioController ??= new CoreAudioController();
            var devices = (await _audioController.GetPlaybackDevicesAsync(DeviceState.Active)).ToArray();
            foreach (var device in devices)
            {
                var iconGlyph = GetOutputDeviceGlyph(device.Name);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

                var icon = new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var name = new TextBlock
                {
                    Text = device.Name,
                    FontSize = 12,
                    Foreground = System.Windows.Media.Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(icon);
                row.Children.Add(name);

                if (device.IsDefaultDevice)
                {
                    var check = new TextBlock
                    {
                        Text = "✓",
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(66, 189, 245)),
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = WpfHorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(check, 2);
                    row.Children.Add(check);
                }

                var button = new WpfButton
                {
                    Style = (Style)Resources["OutputDeviceButtonStyle"],
                    Content = row,
                    Tag = device,
                    Background = device.IsDefaultDevice
                        ? new SolidColorBrush(WpfColor.FromArgb(24, 66, 189, 245))
                        : System.Windows.Media.Brushes.Transparent
                };
                button.Click += OutputDeviceButton_Click;
                OutputDevicesPanel.Children.Add(button);
            }
        }
        catch
        {
            OutputDevicesPanel.Children.Add(new TextBlock
            {
                Text = "Ses cihazları okunamadı",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(180, 180, 185)),
                Margin = new Thickness(10, 12, 10, 12)
            });
        }
    }

    private async void OutputDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: CoreAudioDevice device } || _audioController is null)
        {
            return;
        }

        try
        {
            await _audioController.SetDefaultDeviceAsync(device);
            _defaultPlaybackDevice = device;
            TopBarVolumeText.Text = $"{Math.Round(device.Volume)}%";
        }
        catch
        {
        }
        finally
        {
            OutputDevicePopup.IsOpen = false;
        }
    }

    private static string GetOutputDeviceGlyph(string deviceName)
    {
        var lowerName = deviceName.ToLowerInvariant();
        if (lowerName.Contains("head") || lowerName.Contains("kulak") || lowerName.Contains("buds"))
        {
            return "\uE7F6";
        }

        if (lowerName.Contains("nvidia") || lowerName.Contains("monitor") || lowerName.Contains("display"))
        {
            return "\uE7F4";
        }

        return "\uE7F5";
    }

    private void TopBarClockButton_Click(object sender, RoutedEventArgs e)
    {
        CalendarPopup.IsOpen = !CalendarPopup.IsOpen;
    }

    private void CalendarPopup_Opened(object sender, EventArgs e)
    {
        _displayedCalendarMonth = new DateTime(_selectedCalendarDate.Year, _selectedCalendarDate.Month, 1);
        UpdateCalendarPopup();
    }

    private void CalendarPreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayedCalendarMonth = _displayedCalendarMonth.AddMonths(-1);
        UpdateCalendarPopup();
    }

    private void CalendarNextMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayedCalendarMonth = _displayedCalendarMonth.AddMonths(1);
        UpdateCalendarPopup();
    }

    private void CalendarDayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: DateTime date })
        {
            return;
        }

        _selectedCalendarDate = date;
        _displayedCalendarMonth = new DateTime(date.Year, date.Month, 1);
        UpdateCalendarPopup();
    }

    private void UpdateCalendarPopup()
    {
        var turkishMonths = new[] { "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık" };
        var turkishDays = new[] { "Pazar", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi" };
        var today = DateTime.Today;
        CalendarTodayText.Text = $"{today.Day} {turkishMonths[today.Month - 1]} {turkishDays[(int)today.DayOfWeek]}";
        CalendarMonthText.Text = $"{turkishMonths[_displayedCalendarMonth.Month - 1]} {_displayedCalendarMonth.Year}";
        CalendarDaysGrid.Children.Clear();

        var mondayOffset = ((int)_displayedCalendarMonth.DayOfWeek + 6) % 7;
        var firstVisibleDate = _displayedCalendarMonth.AddDays(-mondayOffset);
        for (var index = 0; index < 42; index++)
        {
            var date = firstVisibleDate.AddDays(index);
            var isCurrentMonth = date.Month == _displayedCalendarMonth.Month;
            var isToday = date.Date == today;
            var isSelected = date.Date == _selectedCalendarDate.Date;
            var dayButton = new WpfButton
            {
                Content = date.Day.ToString(),
                Tag = date,
                Style = (Style)Resources["CalendarDayButtonStyle"],
                Foreground = new SolidColorBrush(isCurrentMonth ? WpfColor.FromRgb(245, 245, 245) : WpfColor.FromRgb(105, 105, 110)),
                Background = new SolidColorBrush(isSelected ? WpfColor.FromRgb(55, 175, 235) : isToday ? WpfColor.FromArgb(45, 255, 255, 255) : Colors.Transparent),
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal
            };
            dayButton.Click += CalendarDayButton_Click;
            CalendarDaysGrid.Children.Add(dayButton);
        }
    }

    private void TopBarClipboardButton_Click(object sender, RoutedEventArgs e) => ShowClipboardHistory();

    private void TopBarTimerButton_Click(object sender, RoutedEventArgs e) => StartTimer(TimeSpan.FromMinutes(5));

    private void TopBarAudioButton_Click(object sender, RoutedEventArgs e) => SwitchAudioOutputMenuItem_Click(sender, e);

    private void DockLauncherButton_Click(object sender, RoutedEventArgs e) => ToggleStartMenu();

    private void StartDockWatcher()
    {
        _dockWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _dockWatcher.Tick += (_, _) => UpdateDockApps();
        _dockWatcher.Start();
        UpdateDockApps();
    }

    private void UpdateDockApps()
    {
        var apps = GetDockApps();
        var signature = string.Join("|", apps.Select(app => $"{app.Key}:{app.Handle}:{app.IsActive}:{app.IsPinned}:{app.IsRunning}"));
        if (signature == _lastDockSignature)
        {
            return;
        }

        _lastDockSignature = signature;
        DockAppsPanel.Children.Clear();
        DockBar.Visibility = Visibility.Visible;

        foreach (var app in apps)
        {
            DockAppsPanel.Children.Add(CreateDockButton(app));
        }
    }

    private WpfButton CreateDockButton(DockApp app)
    {
        var iconHost = new Grid
        {
            Width = 38,
            Height = 40
        };

        var iconBubble = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(WpfColor.FromArgb(28, 255, 255, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (app.Icon is not null)
        {
            iconBubble.Child = new System.Windows.Controls.Image
            {
                Source = app.Icon,
                Width = 27,
                Height = 27,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconBubble.Child = new TextBlock
            {
                Text = GetDockFallbackText(app.Title),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        iconHost.Children.Add(iconBubble);

        var activeDot = new Border
        {
            Width = app.IsActive ? 16 : 4,
            Height = 2.5,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(app.IsActive
                ? WpfColor.FromRgb(245, 245, 245)
                : app.IsRunning
                    ? WpfColor.FromArgb(115, 255, 255, 255)
                    : WpfColor.FromArgb(45, 255, 255, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -6)
        };
        iconHost.Children.Add(activeDot);

        var button = new WpfButton
        {
            Style = (Style)Resources["DockAppButtonStyle"],
            Content = iconHost,
            Tag = app,
            ToolTip = app.Title,
            AllowDrop = true
        };
        button.ContextMenu = CreateDockContextMenu(app);
        button.PreviewMouseLeftButtonDown += DockAppButton_PreviewMouseLeftButtonDown;
        button.PreviewMouseMove += DockAppButton_PreviewMouseMove;
        button.Drop += DockAppButton_Drop;
        button.Click += DockAppButton_Click;
        return button;
    }

    private void DockAppButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dockDragStartPoint = e.GetPosition(this);
    }

    private void DockAppButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not WpfButton { Tag: DockApp app } ||
            e.LeftButton != System.Windows.Input.MouseButtonState.Pressed ||
            _dockDragStartPoint is not { } startPoint)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, app.Key, System.Windows.DragDropEffects.Move);
        _dockDragStartPoint = null;
    }

    private void DockAppButton_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not WpfButton { Tag: DockApp target } ||
            e.Data.GetData(typeof(string)) is not string sourceKey ||
            string.Equals(sourceKey, target.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MoveDockApp(sourceKey, target.Key);
    }

    private void DockAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: DockApp app })
        {
            return;
        }

        if (app.Handle != IntPtr.Zero)
        {
            if (IsIconic(app.Handle))
            {
                ShowWindow(app.Handle, SwRestore);
            }

            SetForegroundWindow(app.Handle);
            return;
        }

        if (!string.IsNullOrWhiteSpace(app.Path) && IsLaunchablePath(app.Path))
        {
            LaunchPath(app.Path);
        }
    }

    private static void LaunchPath(string path)
    {
        if (!IsLaunchablePath(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = path,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }
    }

    private static void LaunchPathAsAdmin(string? path)
    {
        if (!IsAdminLaunchablePath(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path!,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
        }
    }

    private static void CloseWindow(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private ContextMenu CreateDockContextMenu(DockApp app)
    {
        var menu = CreateStyledContextMenu();
        var openItem = CreateStyledMenuItem(app.IsRunning ? "Öne getir" : "Aç");
        openItem.Click += (_, _) => DockAppButton_Click(new WpfButton { Tag = app }, new RoutedEventArgs());
        menu.Items.Add(openItem);

        if (IsAdminLaunchablePath(app.Path))
        {
            var adminItem = CreateStyledMenuItem(app.IsRunning
                ? "Yönetici olarak yeniden aç"
                : "Yönetici olarak çalıştır");
            adminItem.Click += (_, _) => LaunchPathAsAdmin(app.Path);
            menu.Items.Add(adminItem);
        }

        if (app.Handle != IntPtr.Zero)
        {
            var closeItem = CreateStyledMenuItem("Kapat");
            closeItem.Click += (_, _) => CloseWindow(app.Handle);
            menu.Items.Add(closeItem);
        }

        menu.Items.Add(CreateStyledSeparator());

        var pinItem = CreateStyledMenuItem(app.IsPinned ? "Dock'tan kaldır" : "Dock'a sabitle");
        pinItem.IsEnabled = !string.IsNullOrWhiteSpace(app.Path);
        pinItem.Click += (_, _) => ToggleDockPin(app);
        menu.Items.Add(pinItem);
        return menu;
    }

    private ContextMenu CreateStyledContextMenu()
    {
        var menu = new ContextMenu();
        if (TryFindResource(typeof(ContextMenu)) is Style style)
        {
            menu.Style = style;
        }

        return menu;
    }

    private MenuItem CreateStyledMenuItem(object header)
    {
        var item = new MenuItem { Header = header };
        if (TryFindResource(typeof(MenuItem)) is Style style)
        {
            item.Style = style;
        }

        return item;
    }

    private Separator CreateStyledSeparator()
    {
        var separator = new Separator();
        if (TryFindResource(typeof(Separator)) is Style style)
        {
            separator.Style = style;
        }

        return separator;
    }

    private void ToggleDockPin(DockApp app)
    {
        if (app.IsPinned)
        {
            _settings.PinnedDockApps.RemoveAll(pinned => string.Equals(pinned.Key, app.Key, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(app.Key) && !string.IsNullOrWhiteSpace(app.Path))
        {
            _settings.PinnedDockApps.RemoveAll(pinned => string.Equals(pinned.Key, app.Key, StringComparison.OrdinalIgnoreCase));
            _settings.PinnedDockApps.Add(new DockPinnedApp
            {
                Key = app.Key,
                Title = app.Title,
                Path = app.Path
            });
        }

        _settings.Save();
        _lastDockSignature = string.Empty;
        UpdateDockApps();
    }

    private void MoveDockApp(string sourceKey, string targetKey)
    {
        var currentOrder = GetDockApps()
            .Select(app => app.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        currentOrder.RemoveAll(key => string.Equals(key, sourceKey, StringComparison.OrdinalIgnoreCase));
        var targetIndex = currentOrder.FindIndex(key => string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            currentOrder.Add(sourceKey);
        }
        else
        {
            currentOrder.Insert(targetIndex, sourceKey);
        }

        _settings.DockAppOrder = currentOrder;
        _settings.Save();
        _lastDockSignature = string.Empty;
        UpdateDockApps();
    }

    private void ToggleStartMenu()
    {
        if (StartMenuPanel.Visibility == Visibility.Visible)
        {
            HideStartMenu();
            return;
        }

        ShowStartMenu();
    }

    private void ShowStartMenu()
    {
        UpdateLauncherApps();
        StartSearchBox.Text = string.Empty;
        StartMenuPanel.Visibility = Visibility.Visible;
        StartSearchBox.Focus();
    }

    private void HideStartMenu()
    {
        StartMenuPanel.Visibility = Visibility.Collapsed;
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private void UpdateLauncherApps()
    {
        var filter = StartSearchBox.Text.Trim();
        var apps = GetDockApps()
            .Where(app => string.IsNullOrWhiteSpace(filter) || app.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var installedApps = GetInstalledLauncherApps()
            .Where(app => string.IsNullOrWhiteSpace(filter) || app.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        LauncherPinnedPanel.Children.Clear();
        LauncherRunningPanel.Children.Clear();
        LauncherInstalledPanel.Children.Clear();

        foreach (var app in apps.Where(app => app.IsPinned))
        {
            LauncherPinnedPanel.Children.Add(CreateLauncherButton(app));
        }

        foreach (var app in apps.Where(app => app.IsRunning))
        {
            LauncherRunningPanel.Children.Add(CreateLauncherButton(app));
        }

        foreach (var app in installedApps)
        {
            LauncherInstalledPanel.Children.Add(CreateLauncherButton(app));
        }
    }

    private WpfButton CreateLauncherButton(DockApp app)
    {
        return CreateLauncherButton(app.Title, app.Icon, app, () =>
        {
            HideStartMenu();
            DockAppButton_Click(new WpfButton { Tag = app }, new RoutedEventArgs());
        });
    }

    private WpfButton CreateLauncherButton(LauncherApp app)
    {
        return CreateLauncherButton(app.Title, app.Icon, app, () =>
        {
            HideStartMenu();
            LaunchPath(app.Path);
        });
    }

    private WpfButton CreateLauncherButton(string title, ImageSource? icon, object tag, Action action)
    {
        var root = new StackPanel
        {
            Width = 78,
            Margin = new Thickness(4, 6, 4, 8)
        };

        var iconHost = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(WpfColor.FromArgb(28, 255, 255, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Center
        };

        if (icon is not null)
        {
            iconHost.Child = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            iconHost.Child = new TextBlock
            {
                Text = GetDockFallbackText(title),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        root.Children.Add(iconHost);
        root.Children.Add(new TextBlock
        {
            Text = TrimForIsland(title, 18),
            Foreground = new SolidColorBrush(WpfColor.FromArgb(220, 255, 255, 255)),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var button = new WpfButton
        {
            Style = (Style)Resources["TopBarButtonStyle"],
            Height = 92,
            Padding = new Thickness(0),
            Content = root,
            Tag = tag,
            ToolTip = title
        };
        button.ContextMenu = CreateLauncherContextMenu(tag, action);
        button.Click += (_, _) => action();
        return button;
    }

    private ContextMenu CreateLauncherContextMenu(object tag, Action openAction)
    {
        if (tag is DockApp dockApp)
        {
            return CreateDockContextMenu(dockApp);
        }

        var menu = CreateStyledContextMenu();
        var openItem = CreateStyledMenuItem("Aç");
        openItem.Click += (_, _) => openAction();
        menu.Items.Add(openItem);

        if (tag is LauncherApp launcherApp)
        {
            if (IsAdminLaunchablePath(launcherApp.Path))
            {
                var adminItem = CreateStyledMenuItem("Yönetici olarak çalıştır");
                adminItem.Click += (_, _) => LaunchPathAsAdmin(launcherApp.Path);
                menu.Items.Add(adminItem);
            }

            menu.Items.Add(CreateStyledSeparator());
            var hideItem = CreateStyledMenuItem("Launcher'dan gizle");
            hideItem.Click += (_, _) => HideLauncherApp(launcherApp);
            menu.Items.Add(hideItem);
        }

        return menu;
    }

    private void HideLauncherApp(LauncherApp app)
    {
        if (!_settings.HiddenLauncherApps.Contains(app.Path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.HiddenLauncherApps.Add(app.Path);
            _settings.Save();
        }

        _installedLauncherApps = null;
        UpdateLauncherApps();
    }

    private void StartSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StartMenuPanel.Visibility == Visibility.Visible)
        {
            UpdateLauncherApps();
        }
    }

    private void StartSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Escape)
        {
            HideStartMenu();
            e.Handled = true;
            return;
        }

        if (e.Key is not System.Windows.Input.Key.Enter)
        {
            return;
        }

        var query = StartSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var dockMatch = GetDockApps()
                .Where(app => app.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(app => app.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (dockMatch is not null)
            {
                DockAppButton_Click(new WpfButton { Tag = dockMatch }, new RoutedEventArgs());
            }
            else
            {
                var installedMatch = GetInstalledLauncherApps()
                    .Where(app => app.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .OrderByDescending(app => app.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                    .ThenBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
                    .FirstOrDefault();
                if (installedMatch is not null)
                {
                    LaunchPath(installedMatch.Path);
                }
            }
        }

        HideStartMenu();
        e.Handled = true;
    }

    private List<LauncherApp> GetInstalledLauncherApps()
    {
        if (_installedLauncherApps is not null)
        {
            return _installedLauncherApps;
        }

        _installedLauncherApps = GetStartMenuLauncherApps()
            .Concat(GetShellLauncherApps())
            .Concat(GetKnownRegistryLauncherApps())
            .Concat(GetKnownStoreLauncherApps())
            .Concat(GetSteamLauncherApps())
            .Where(app => !IsLauncherNoise(app.Title))
            .Where(app => !_settings.HiddenLauncherApps.Contains(app.Path, StringComparer.OrdinalIgnoreCase))
            .GroupBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group
                .OrderByDescending(app => IsShortcutPath(app.Path))
                .ThenBy(app => app.Path.Length)
                .First())
            .OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return _installedLauncherApps;
    }

    private static IEnumerable<LauncherApp> GetStartMenuLauncherApps()
    {
        var shortcutRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

        foreach (var root in shortcutRoots)
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var title = Path.GetFileNameWithoutExtension(shortcut);
                if (IsLauncherNoise(title))
                {
                    continue;
                }

                yield return new LauncherApp(title, shortcut, TryCreateIconSourceFromFile(shortcut));
            }
        }
    }

    private static IEnumerable<LauncherApp> GetShellLauncherApps()
    {
        var apps = new List<LauncherApp>();
        object? shellObject = null;
        object? folderObject = null;
        object? itemsObject = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return apps;
            }

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return apps;
            }

            dynamic shell = shellObject;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject is null)
            {
                return apps;
            }

            dynamic folder = folderObject;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            var count = (int)items.Count;
            for (var index = 0; index < count; index++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(index);
                    if (itemObject is null)
                    {
                        continue;
                    }

                    dynamic item = itemObject;
                    var title = Convert.ToString(item.Name)?.Trim();
                    var appId = Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID"))?.Trim();
                    if (string.IsNullOrWhiteSpace(title) ||
                        string.IsNullOrWhiteSpace(appId) ||
                        IsShellLauncherNoise(title, appId) ||
                        appId?.StartsWith("http:", StringComparison.OrdinalIgnoreCase) == true ||
                        appId?.StartsWith("https:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        continue;
                    }

                    var directPath = Convert.ToString(item.Path)?.Trim();
                    var launchPath = !string.IsNullOrWhiteSpace(directPath) && File.Exists(directPath)
                        ? directPath
                        : $@"shell:AppsFolder\{appId}";
                    var icon = !string.IsNullOrWhiteSpace(directPath) && File.Exists(directPath)
                        ? TryCreateIconSourceFromFile(directPath)
                        : null;
                    apps.Add(new LauncherApp(title, launchPath, icon));
                }
                catch
                {
                }
                finally
                {
                    ReleaseComObject(itemObject);
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseComObject(itemsObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellObject);
        }

        return apps;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }
    }

    private static IEnumerable<LauncherApp> GetKnownRegistryLauncherApps()
    {
        var allowedNames = new[]
        {
            "Spotify",
            "Steam",
            "Epic Games Launcher",
            "Discord",
            "Brave",
            "Google Chrome",
            "Riot Client",
            "VALORANT",
            "Roblox Player",
            "Roblox Studio",
            "WhatsApp",
            "Telegram",
            "Skype"
        };
        var uninstallRoots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };

        foreach (var (hive, view) in uninstallRoots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                var title = appKey?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(title) ||
                    IsLauncherNoise(title) ||
                    !allowedNames.Any(name => title.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var launchPath = FindRegistryLaunchPath(appKey);
                if (string.IsNullOrWhiteSpace(launchPath))
                {
                    continue;
                }

                var iconPath = NormalizeExecutablePath(appKey?.GetValue("DisplayIcon") as string) ?? launchPath;
                yield return new LauncherApp(title, launchPath, TryCreateIconSourceFromFile(iconPath));
            }
        }
    }

    private static string? FindRegistryLaunchPath(RegistryKey? appKey)
    {
        if (appKey is null)
        {
            return null;
        }

        var displayIcon = NormalizeExecutablePath(appKey.GetValue("DisplayIcon") as string);
        if (!string.IsNullOrWhiteSpace(displayIcon) && File.Exists(displayIcon))
        {
            return displayIcon;
        }

        var installLocation = appKey.GetValue("InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                return Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(path => !IsLauncherNoise(Path.GetFileNameWithoutExtension(path)))
                    .OrderBy(path => path.Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<LauncherApp> GetKnownStoreLauncherApps()
    {
        var localWindowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        var knownApps = new[]
        {
            (Title: "Spotify", AppId: @"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", Alias: "Spotify.exe"),
            (Title: "WhatsApp", AppId: @"shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", Alias: "WhatsApp.exe"),
            (Title: "ChatGPT", AppId: @"shell:AppsFolder\OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT", Alias: "ChatGPT.exe")
        };

        foreach (var app in knownApps)
        {
            var aliasPath = Path.Combine(localWindowsApps, app.Alias);
            if (File.Exists(aliasPath))
            {
                yield return new LauncherApp(app.Title, app.AppId, TryCreateIconSourceFromFile(aliasPath));
            }
        }
    }

    private static IEnumerable<LauncherApp> GetSteamLauncherApps()
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            yield break;
        }

        var steamIcon = TryCreateIconSourceFromFile(Path.Combine(steamPath, "steam.exe"));
        foreach (var library in GetSteamLibraryFolders(steamPath))
        {
            var steamAppsPath = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamAppsPath))
            {
                continue;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                var text = ReadAllTextSafely(manifest);
                var name = MatchAcfValue(text, "name");
                var appId = MatchAcfValue(text, "appid") ?? Regex.Match(Path.GetFileNameWithoutExtension(manifest), @"\d+").Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
                {
                    continue;
                }

                yield return new LauncherApp(name, $"steam://rungameid/{appId}", steamIcon);
            }
        }
    }

    private List<DockApp> GetDockApps()
    {
        var ownHandle = new WindowInteropHelper(this).Handle;
        var activeWindow = GetForegroundWindow();
        GetWindowThreadProcessId(activeWindow, out var activeProcessId);
        if (activeProcessId != 0 && activeProcessId != (uint)Environment.ProcessId)
        {
            _lastActiveDockProcessId = activeProcessId;
        }

        var appsByProcess = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((handle, _) =>
        {
            if (!IsDockWindow(handle, ownHandle))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            var title = GetWindowTitle(handle);
            var processPath = TryGetProcessPath(processId);
            var processKey = GetDockProcessKey(processId, handle, processPath);
            var icon = GetDockIcon(handle, processId);
            var app = new DockApp(processKey, handle, title, processPath, icon, processId == _lastActiveDockProcessId, true, IsDockAppPinned(processKey));

            if (!appsByProcess.TryGetValue(processKey, out var existing) || app.IsActive || string.Compare(app.Title, existing.Title, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                appsByProcess[processKey] = app;
            }

            return true;
        }, IntPtr.Zero);

        foreach (var pinned in _settings.PinnedDockApps)
        {
            if (string.IsNullOrWhiteSpace(pinned.Key) || appsByProcess.ContainsKey(pinned.Key))
            {
                continue;
            }

            var icon = TryCreateIconSourceFromFile(pinned.Path);
            appsByProcess[pinned.Key] = new DockApp(pinned.Key, IntPtr.Zero, pinned.Title, pinned.Path, icon, false, false, true);
        }

        var orderedKeys = _settings.DockAppOrder
            .Select((key, index) => new { key, index })
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

        foreach (var key in appsByProcess.Keys)
        {
            if (!_dockFirstSeenOrder.ContainsKey(key))
            {
                _dockFirstSeenOrder[key] = _nextDockOrder++;
            }
        }

        return appsByProcess.Values
            .OrderBy(app => orderedKeys.TryGetValue(app.Key, out var index) ? index : int.MaxValue)
            .ThenByDescending(app => app.IsPinned)
            .ThenBy(app => _dockFirstSeenOrder[app.Key])
            .Take(17)
            .ToList();
    }

    private bool IsDockWindow(IntPtr handle, IntPtr ownHandle)
    {
        if (handle == IntPtr.Zero || handle == ownHandle || !IsWindowVisible(handle))
        {
            return false;
        }

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var title = GetWindowTitle(handle);
        var className = GetWindowClassName(handle);
        var processName = TryGetProcessName(processId);
        var processPath = TryGetProcessPath(processId);
        if (IsDockNoiseWindow(title, className, processName, processPath))
        {
            return false;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0 || GetWindow(handle, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        if (!IsIconic(handle) &&
            (!GetWindowRect(handle, out var rect) || rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 60))
        {
            return false;
        }

        return !IsShellSurfaceWindow(handle);
    }

    private bool IsDockAppPinned(string key)
    {
        return _settings.PinnedDockApps.Any(app => string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDockNoiseWindow(string title, string className, string? processName, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        if (title.Contains("WinDynamicIsland", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Windows Giriş Deneyimi", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
        {
            return true;
        }

        if (processName is null)
        {
            return false;
        }

        if (processName.Equals("WinDynamicIsland", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return processPath?.Contains(@"\Windows\SystemApps\", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var title = new System.Text.StringBuilder(180);
        return GetWindowText(handle, title, title.Capacity) > 0 ? title.ToString() : string.Empty;
    }

    private string GetDockProcessKey(uint processId, IntPtr handle, string? processPath)
    {
        if (processId == 0)
        {
            return $"window:{handle}";
        }

        return processPath ?? $"process:{processId}";
    }

    private ImageSource? GetDockIcon(IntPtr windowHandle, uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var windowIcon = TryCreateIconSourceFromWindow(windowHandle);
        if (windowIcon is not null)
        {
            return windowIcon;
        }

        var path = TryGetProcessPath(processId);
        var key = path ?? $"process:{processId}";

        if (_dockIconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var source = TryCreateIconSourceFromFile(path);

        _dockIconCache[key] = source;
        return source;
    }

    private static ImageSource? TryCreateIconSourceFromWindow(IntPtr windowHandle)
    {
        var iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconBig), IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconSmall2), IntPtr.Zero);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconSmall), IntPtr.Zero);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = GetClassLongPtr(windowHandle, GclpHicon);
        }

        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = GetClassLongPtr(windowHandle, GclpHiconSmall);
        }

        return CreateImageSourceFromIconHandle(iconHandle, destroyHandle: false);
    }

    private static ImageSource? TryCreateIconSourceFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var fileInfo = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref fileInfo, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result != IntPtr.Zero && fileInfo.IconHandle != IntPtr.Zero)
        {
            return CreateImageSourceFromIconHandle(fileInfo.IconHandle, destroyHandle: true);
        }

        try
        {
            using var associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            using var icon = associatedIcon?.Clone() as System.Drawing.Icon;
            return icon is null
                ? null
                : CreateImageSourceFromIconHandle(icon.Handle, destroyHandle: false);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? CreateImageSourceFromIconHandle(IntPtr iconHandle, bool destroyHandle)
    {
        if (iconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (destroyHandle)
            {
                DestroyIcon(iconHandle);
            }
        }
    }

    private static string? TryGetProcessPath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch
        {
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pathBuilder = new System.Text.StringBuilder(1024);
            var capacity = pathBuilder.Capacity;
            return QueryFullProcessImageName(processHandle, 0, pathBuilder, ref capacity)
                ? pathBuilder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var endQuote = text.IndexOf('"', 1);
            if (endQuote > 1)
            {
                text = text[1..endQuote];
            }
        }
        else
        {
            var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex < 0)
            {
                return null;
            }

            if (exeIndex >= 0)
            {
                text = text[..(exeIndex + 4)];
            }
        }

        text = text.Trim().Trim(',');
        return Path.GetExtension(text).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(text) ? text : null;
    }

    private static bool IsLaunchablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return File.Exists(path) &&
               (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".url", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAdminLaunchablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShortcutPath(string path)
    {
        return Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLauncherNoise(string title)
    {
        return string.IsNullOrWhiteSpace(title) ||
               title.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Kaldır", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Updater", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Help", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Manual", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Support Center", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Documentation", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Readme", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellLauncherNoise(string title, string appId)
    {
        return IsLauncherNoise(title) ||
               ShellLauncherNoiseTitles.Contains(title) ||
               title.Contains("Belgeler", StringComparison.CurrentCultureIgnoreCase) ||
               title.Contains(" sitesi", StringComparison.CurrentCultureIgnoreCase) ||
               title.Contains("Config File", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Release Notes", StringComparison.OrdinalIgnoreCase) ||
               title.EndsWith(" Demo", StringComparison.OrdinalIgnoreCase) ||
               appId.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSteamPath()
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Valve\Steam",
            @"SOFTWARE\WOW6432Node\Valve\Steam"
        };

        foreach (var registryPath in registryPaths)
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath) ?? Registry.LocalMachine.OpenSubKey(registryPath);
            var path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path.Replace('/', '\\');
            }
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static IEnumerable<string> GetSteamLibraryFolders(string steamPath)
    {
        yield return steamPath;

        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var text = ReadAllTextSafely(libraryFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var path = Regex.Unescape(match.Groups[1].Value).Replace(@"\\", @"\");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static string? MatchAcfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ReadAllTextSafely(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetDockFallbackText(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length == 0 ? "?" : trimmed[..Math.Min(2, trimmed.Length)].ToUpperInvariant();
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

    private void StartSystemStatusWatcher()
    {
        _systemStatusWatcher?.Stop();
        _systemStatusWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _systemStatusWatcher.Tick += (_, _) => UpdateSystemStatusAlerts();
        _systemStatusWatcher.Start();
        UpdateSystemStatusAlerts();
    }

    private void UpdateSystemStatusAlerts()
    {
        if (!_settings.SystemAlertsEnabled)
        {
            return;
        }

        var capsOn = IsToggleKeyOn(0x14);
        var numOn = IsToggleKeyOn(0x90);
        var power = Forms.SystemInformation.PowerStatus;
        var percent = power.BatteryLifePercent >= 0
            ? (int)Math.Round(power.BatteryLifePercent * 100)
            : -1;

        if (!_systemStatusPrimed)
        {
            _lastCapsLockOn = capsOn;
            _lastNumLockOn = numOn;
            _lastPowerLineStatus = power.PowerLineStatus;
            _lastBatteryPercent = percent;
            _systemStatusPrimed = true;
            return;
        }

        if (capsOn != _lastCapsLockOn)
        {
            _lastCapsLockOn = capsOn;
            ShowUtility("Caps Lock", capsOn ? "Acik" : "Kapali", "A", capsOn ? 1 : 0, TimeSpan.FromSeconds(1.4));
        }
        else if (numOn != _lastNumLockOn)
        {
            _lastNumLockOn = numOn;
            ShowUtility("Num Lock", numOn ? "Acik" : "Kapali", "#", numOn ? 1 : 0, TimeSpan.FromSeconds(1.4));
        }
        else if (power.PowerLineStatus != _lastPowerLineStatus)
        {
            _lastPowerLineStatus = power.PowerLineStatus;
            ShowUtility("Pil", power.PowerLineStatus is Forms.PowerLineStatus.Online ? $"Sarj oluyor {percent}%" : $"Pilde {percent}%", "B", percent / 100d, TimeSpan.FromSeconds(2.2));
        }
        else if (percent >= 0 &&
                 percent <= 20 &&
                 percent != _lastBatteryPercent &&
                 power.PowerLineStatus is not Forms.PowerLineStatus.Online)
        {
            ShowUtility("Pil Dusuk", $"{percent}% kaldi", "B", percent / 100d, TimeSpan.FromSeconds(2.2));
        }

        _lastBatteryPercent = percent;
    }

    private static bool IsToggleKeyOn(int virtualKey)
    {
        return (GetKeyState(virtualKey) & 0x0001) != 0;
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
        UpdateCompactMediaPlacement();

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
        var ease = (IEasingFunction)Resources["IslandEase"];
        var duration = TimeSpan.FromMilliseconds(hide ? 190 : 280);

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
            IsShellSurfaceWindow(foreground) ||
            !IsWindowVisible(foreground) ||
            IsIconic(foreground) ||
            !GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var ownMonitor = MonitorFromWindow(ownHandle, MonitorDefaultToNearest);
        if (monitor != ownMonitor)
        {
            return false;
        }

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

    private static bool IsShellSurfaceWindow(IntPtr hwnd)
    {
        var className = GetWindowClassName(hwnd);
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var className = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            HandleClipboardUpdate();
            return IntPtr.Zero;
        }

        if (msg == WmDisplayChange)
        {
            PositionAtTopCenter();
            RegisterAppBar();
            return IntPtr.Zero;
        }

        if (msg != WmNchittest)
        {
            return IntPtr.Zero;
        }

        var point = GetPointFromLParam(lParam);
        var hoverZoneRelative = IslandHoverZone.PointFromScreen(point);
        var hoverZoneBounds = new Rect(0, 0, IslandHoverZone.ActualWidth, IslandHoverZone.ActualHeight);
        if (hoverZoneBounds.Contains(hoverZoneRelative))
        {
            return IntPtr.Zero;
        }

        var islandRelative = Island.PointFromScreen(point);
        var islandBounds = new Rect(0, 0, Island.ActualWidth, Island.ActualHeight);
        if (islandBounds.Contains(islandRelative))
        {
            return IntPtr.Zero;
        }

        var topBarRelative = TopBar.PointFromScreen(point);
        var topBarBounds = new Rect(0, 0, TopBar.ActualWidth, TopBar.ActualHeight);
        if (topBarBounds.Contains(topBarRelative))
        {
            return IntPtr.Zero;
        }

        var dockRelative = DockBar.PointFromScreen(point);
        var dockBounds = new Rect(0, 0, DockBar.ActualWidth, DockBar.ActualHeight);
        if (dockBounds.Contains(dockRelative))
        {
            return IntPtr.Zero;
        }

        if (StartMenuPanel.Visibility == Visibility.Visible)
        {
            var startMenuRelative = StartMenuPanel.PointFromScreen(point);
            var startMenuBounds = new Rect(0, 0, StartMenuPanel.ActualWidth, StartMenuPanel.ActualHeight);
            if (startMenuBounds.Contains(startMenuRelative))
            {
                return IntPtr.Zero;
            }
        }

        handled = true;
        return new IntPtr(Httransparent);
    }

    private static System.Windows.Point GetPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new System.Windows.Point(x, y);
    }

    private void HandleClipboardUpdate()
    {
        try
        {
            if (WpfClipboard.ContainsText())
            {
                var text = WpfClipboard.GetText().Trim();
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

            if (WpfClipboard.ContainsImage())
            {
                if (!_settings.ScreenshotPreviewEnabled)
                {
                    return;
                }

                var image = WpfClipboard.GetImage();
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
        if (!_settings.NotificationsEnabled)
        {
            return;
        }

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
        if (_state is not IslandState.MediaCompact || _isMediaActive || _isCameraActive || _isMicrophoneActive || _isHovering || !_settings.WeatherEnabled)
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
        UpdateCompactMediaPlacement();

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
            IslandStroke.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(18, 255, 255, 255));
            CompactBarThree.Background = new SolidColorBrush(WpfColor.FromRgb(29, 185, 84));
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

            var color = WpfColor.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
            IslandStroke.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, color.R, color.G, color.B));
            CompactBarThree.Background = new SolidColorBrush(WpfColor.FromArgb(255, color.R, color.G, color.B));
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
        if (!_settings.TimerEnabled)
        {
            ShowUtility("Timer", "Timer kapali", "T", 0, TimeSpan.FromSeconds(1.5));
            return;
        }

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
        UpdateCompactTimerPlacement();
        UpdateCompactMediaPlacement();
        CompactTimerText.Visibility = Visibility.Visible;
        TimerPanel.Visibility = Visibility.Visible;
        TimerTimeText.Text = FormatRemaining(remaining);
        var progressWidth = TimerProgressTrack.ActualWidth > 0 ? TimerProgressTrack.ActualWidth : 360;
        TimerProgressFill.Width = progress * progressWidth;
    }

    private void UpdateCompactTimerPlacement()
    {
        if (_isMediaActive)
        {
            Grid.SetColumn(CompactTimerText, 1);
            Grid.SetColumnSpan(CompactTimerText, 1);
            CompactTimerText.HorizontalAlignment = WpfHorizontalAlignment.Right;
            CompactTimerText.Margin = new Thickness(8, 0, 0, 0);
            return;
        }

        Grid.SetColumn(CompactTimerText, 0);
        Grid.SetColumnSpan(CompactTimerText, 4);
        CompactTimerText.HorizontalAlignment = WpfHorizontalAlignment.Center;
        CompactTimerText.Margin = new Thickness(0);
    }

    private void UpdateCompactMediaPlacement()
    {
        if (_isMediaActive && !IsTimerActive && !_isCameraActive && !_isMicrophoneActive)
        {
            Grid.SetColumn(CompactMediaIndicator, 0);
            Grid.SetColumnSpan(CompactMediaIndicator, 4);
            CompactMediaIndicator.HorizontalAlignment = WpfHorizontalAlignment.Center;
            CompactMediaIndicator.Margin = new Thickness(0);
            return;
        }

        Grid.SetColumn(CompactMediaIndicator, 0);
        Grid.SetColumnSpan(CompactMediaIndicator, 1);
        CompactMediaIndicator.HorizontalAlignment = WpfHorizontalAlignment.Left;
        CompactMediaIndicator.Margin = new Thickness(0);
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
        if (FindVisualParent<WpfButton>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        ShowClipboardHistory();
    }

    private void ShowClipboardHistory()
    {
        if (_clipboardHistory.Count == 0)
        {
            ShowUtility("Pano Gecmisi", "Henuz bir kayit yok", "C", 0, TimeSpan.FromSeconds(2));
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

        PositionIslandForState(nextState, height);
        AnimateIsland(width, height, GetCornerRadius(nextState, height));
        AnimateIslandFrame(nextState, width, height);
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

            return _isCameraActive || _isMicrophoneActive ? 146d : 58d;
        }

        if (IsTimerActive)
        {
            return _isCameraActive || _isMicrophoneActive ? 108d : 82d;
        }

        return _isCameraActive || _isMicrophoneActive ? 52d : 44d;
    }

    private double GetCompactHeight() => _isMediaActive || IsTimerActive ? 30d : 24d;

    private void PositionIslandForState(IslandState state, double height)
    {
        var topOffset = state is IslandState.MediaCompact
            ? Math.Max(4d, (TopBarHeight - height) / 2d)
            : 6d;

        Island.Margin = new Thickness(0, topOffset, 0, 0);
        IslandPocket.Margin = new Thickness(0, Math.Max(0d, topOffset - 4d), 0, 0);
    }

    private static double GetIslandFrameWidth(IslandState state, double islandWidth)
    {
        var horizontalBreathingRoom = state is IslandState.MediaCompact ? 38d : 64d;
        return islandWidth + horizontalBreathingRoom;
    }

    private static double GetIslandFrameHeight(IslandState state, double islandHeight)
    {
        return state is IslandState.MediaCompact
            ? TopBarHeight
            : islandHeight + 18d;
    }

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

    private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings, IsStartWithWindowsEnabled())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings = dialog.Settings;
        _settings.Save();
        SetStartWithWindows(dialog.StartWithWindows);
        RefreshStartupMenuState();
        ApplyRuntimeSettings();

        if (_settings.NotificationsEnabled && _notificationListener is null)
        {
            await InitializeNotificationsAsync();
        }
    }

    private void ApplyRuntimeSettings()
    {
        if (_settings.WeatherEnabled)
        {
            if (_weatherWatcher is null)
            {
                StartWeatherWatcher();
            }
        }
        else
        {
            _weatherWatcher?.Stop();
            _weatherWatcher = null;
        }

        if (!_settings.TimerEnabled && IsTimerActive)
        {
            StopTimer();
        }

        if (_settings.SystemAlertsEnabled)
        {
            if (_systemStatusWatcher is null)
            {
                _systemStatusPrimed = false;
                StartSystemStatusWatcher();
            }
        }
        else
        {
            _systemStatusWatcher?.Stop();
            _systemStatusWatcher = null;
            _systemStatusPrimed = false;
        }

        PositionAtTopCenter();
        RegisterAppBar();
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
        var ease = (IEasingFunction)Resources["IslandEase"];
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        var cornerAnimation = new CornerRadiusAnimation
        {
            To = new CornerRadius(radius),
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = ease
        };
        Island.BeginAnimation(Border.CornerRadiusProperty, cornerAnimation);
    }

    private void AnimateIslandFrame(IslandState state, double islandWidth, double islandHeight)
    {
        var ease = (IEasingFunction)Resources["IslandEase"];
        var frameWidth = GetIslandFrameWidth(state, islandWidth);
        var hoverHeight = GetIslandFrameHeight(state, islandHeight);
        var pocketWidth = islandWidth + (state is IslandState.MediaCompact ? 18d : 28d);
        var pocketHeight = islandHeight + (state is IslandState.MediaCompact ? 8d : 16d);
        var pocketRadius = state is IslandState.MediaCompact ? pocketHeight / 2d : GetCornerRadius(state, islandHeight) + 8d;

        IslandHoverZone.BeginAnimation(WidthProperty, new DoubleAnimation(frameWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        IslandHoverZone.BeginAnimation(HeightProperty, new DoubleAnimation(hoverHeight, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        IslandBarSpacer.BeginAnimation(WidthProperty, new DoubleAnimation(frameWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        IslandPocket.BeginAnimation(WidthProperty, new DoubleAnimation(pocketWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        IslandPocket.BeginAnimation(HeightProperty, new DoubleAnimation(pocketHeight, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        IslandPocket.BeginAnimation(Border.CornerRadiusProperty, new CornerRadiusAnimation
        {
            To = new CornerRadius(pocketRadius),
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = ease
        });
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
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLong")]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetClassLongPtr64(hWnd, nIndex)
            : new IntPtr(GetClassLong32(hWnd, nIndex));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
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

public sealed record DockApp(
    string Key,
    IntPtr Handle,
    string Title,
    string? Path,
    ImageSource? Icon,
    bool IsActive,
    bool IsRunning,
    bool IsPinned);

public sealed record LauncherApp(string Title, string Path, ImageSource? Icon);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct ShFileInfo
{
    public IntPtr IconHandle;
    public int IconIndex;
    public uint Attributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string DisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string TypeName;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct AppBarData
{
    public int Size;
    public IntPtr WindowHandle;
    public uint CallbackMessage;
    public uint Edge;
    public NativeRect Rect;
    public IntPtr Param;

    public static AppBarData Create(IntPtr handle)
    {
        return new AppBarData
        {
            Size = Marshal.SizeOf<AppBarData>(),
            WindowHandle = handle
        };
    }
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
