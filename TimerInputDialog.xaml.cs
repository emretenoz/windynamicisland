using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace WinDynamicIsland;

public partial class TimerInputDialog : Window
{
    public double? Minutes { get; private set; }

    public TimerInputDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MinutesTextBox.Focus();
        MinutesTextBox.SelectAll();
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
            TryAccept();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        TryAccept();
    }

    private void TryAccept()
    {
        var input = MinutesTextBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 1 or > 240)
        {
            ErrorText.Text = "1-240 arasi bir dakika yaz";
            return;
        }

        Minutes = minutes;
        DialogResult = true;
        Close();
    }
}
