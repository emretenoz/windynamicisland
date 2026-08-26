using System.Windows;
using System.Windows.Input;

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
            TimerEnabled = settings.TimerEnabled
        };
        StartWithWindows = startWithWindows;

        InitializeComponent();
        StartWithWindowsCheckBox.IsChecked = StartWithWindows;
        NotificationsCheckBox.IsChecked = Settings.NotificationsEnabled;
        WeatherCheckBox.IsChecked = Settings.WeatherEnabled;
        ScreenshotPreviewCheckBox.IsChecked = Settings.ScreenshotPreviewEnabled;
        TimerCheckBox.IsChecked = Settings.TimerEnabled;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
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
        DialogResult = true;
        Close();
    }
}
