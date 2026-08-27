using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace WinDynamicIsland;

public partial class SettingsDialog : Window
{
    public bool StartWithWindows { get; private set; }
    public IslandSettings Settings { get; }

    public SettingsDialog(IslandSettings settings, bool startWithWindows)
    {
        Settings = new IslandSettings
        {
            NotificationsEnabled = settings.NotificationsEnabled,
            WeatherEnabled = settings.WeatherEnabled,
            ScreenshotPreviewEnabled = settings.ScreenshotPreviewEnabled,
            TimerEnabled = settings.TimerEnabled,
            SystemAlertsEnabled = settings.SystemAlertsEnabled,
            DisplayIndex = settings.DisplayIndex,
            PinnedDockApps = settings.PinnedDockApps
                .Select(app => new DockPinnedApp { Key = app.Key, Title = app.Title, Path = app.Path })
                .ToList(),
            DockAppOrder = settings.DockAppOrder.ToList(),
            HiddenLauncherApps = settings.HiddenLauncherApps.ToList()
        };
        StartWithWindows = startWithWindows;

        InitializeComponent();
        StartWithWindowsCheckBox.IsChecked = StartWithWindows;
        NotificationsCheckBox.IsChecked = Settings.NotificationsEnabled;
        WeatherCheckBox.IsChecked = Settings.WeatherEnabled;
        ScreenshotPreviewCheckBox.IsChecked = Settings.ScreenshotPreviewEnabled;
        TimerCheckBox.IsChecked = Settings.TimerEnabled;
        SystemAlertsCheckBox.IsChecked = Settings.SystemAlertsEnabled;
        LoadDisplays();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        else if (e.Key is Key.Enter)
        {
            SaveAndClose();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState is MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void SaveAndClose()
    {
        StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        Settings.NotificationsEnabled = NotificationsCheckBox.IsChecked == true;
        Settings.WeatherEnabled = WeatherCheckBox.IsChecked == true;
        Settings.ScreenshotPreviewEnabled = ScreenshotPreviewCheckBox.IsChecked == true;
        Settings.TimerEnabled = TimerCheckBox.IsChecked == true;
        Settings.SystemAlertsEnabled = SystemAlertsCheckBox.IsChecked == true;
        Settings.DisplayIndex = Math.Max(0, DisplayComboBox.SelectedIndex);
        DialogResult = true;
        Close();
    }

    private void LoadDisplays()
    {
        var screens = Forms.Screen.AllScreens;
        DisplayComboBox.Items.Clear();

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var primary = screen.Primary ? "ana" : "ikincil";
            DisplayComboBox.Items.Add($"Monitör {i + 1} ({primary}) · {screen.Bounds.Width}x{screen.Bounds.Height}");
        }

        DisplayComboBox.SelectedIndex = Math.Clamp(Settings.DisplayIndex, 0, Math.Max(0, screens.Length - 1));
    }
}
