using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinDynamicIsland.Models;
using WinDynamicIsland.Services;

namespace WinDynamicIsland;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNchittest = 0x0084;
    private const int Httransparent = -1;

    private readonly AudioRecorderService _audioService = new();
    private readonly AgentApiService _agentService = new();
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private IslandState _state = IslandState.MediaCompact;
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
        await InitializeMediaAsync();
        TransitionTo(IslandState.MediaCompact);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _audioService.Dispose();
        if (_mediaManager is not null)
        {
            _mediaManager.CurrentSessionChanged -= MediaManager_CurrentSessionChanged;
        }
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
            return;
        }

        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Dispatcher.Invoke(async () => await RefreshMediaAsync());
    }

    private async Task RefreshMediaAsync()
    {
        try
        {
            if (_mediaSession is null)
            {
                SetTrackText("No media", "Click to talk");
                AlbumArtImage.Source = null;
                return;
            }

            var properties = await _mediaSession.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(properties.Title) ? "Media playing" : properties.Title;
            var artist = string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist;
            SetTrackText(title, string.IsNullOrWhiteSpace(artist) ? "Unknown artist" : artist);

            if (properties.Thumbnail is not null)
            {
                AlbumArtImage.Source = await LoadBitmapAsync(properties.Thumbnail);
            }
        }
        catch
        {
            SetTrackText("Media unavailable", "Click to talk");
        }
    }

    private void SetTrackText(string title, string artist)
    {
        TrackTitleText.Text = title;
        TrackArtistText.Text = artist;
        ExpandedTitleText.Text = title;
        ExpandedArtistText.Text = artist;
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
        if (_state is IslandState.MediaCompact)
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

    private async void Island_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_state is IslandState.Thinking or IslandState.Response)
        {
            return;
        }

        if (_state is IslandState.Recording)
        {
            await StopAndSendAsync();
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        _audioService.StartRecording();
        TransitionTo(IslandState.Recording);
    }

    private async Task StopAndSendAsync()
    {
        try
        {
            var audio = await _audioService.StopRecordingAsync();
            TransitionTo(IslandState.Thinking);

            var response = await _agentService.SendVoiceAsync(audio);
            await ShowAndPlayResponseAsync(response);
        }
        catch (Exception ex)
        {
            ResponseText.Text = ex.Message;
            TransitionTo(IslandState.Response);
            await Task.Delay(2500);
            TransitionTo(_isHovering ? IslandState.MediaExpanded : IslandState.MediaCompact);
        }
    }

    private async Task ShowAndPlayResponseAsync(AgentResponse response)
    {
        ResponseText.Text = string.IsNullOrWhiteSpace(response.Text) ? "Response received" : response.Text;
        TransitionTo(IslandState.Response);

        var audioBytes = response.AudioBytes ?? await _agentService.DownloadAudioAsync(response.AudioUrl);
        if (audioBytes is not null)
        {
            await _audioService.PlayAudioAsync(audioBytes, response.AudioContentType);
        }
        else
        {
            await Task.Delay(3500);
        }

        TransitionTo(_isHovering ? IslandState.MediaExpanded : IslandState.MediaCompact);
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession is not null)
        {
            await _mediaSession.TrySkipPreviousAsync();
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession is not null)
        {
            await _mediaSession.TryTogglePlayPauseAsync();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
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
            IslandState.MediaCompact => (190d, 36d),
            IslandState.MediaExpanded => (340d, 66d),
            IslandState.Recording => (280d, 45d),
            IslandState.Thinking => (280d, 45d),
            IslandState.Response => (330d, 66d),
            _ => (190d, 36d)
        };

        AnimateIsland(width, height, height / 2);
        SetView(MediaCompactView, nextState is IslandState.MediaCompact);
        SetView(MediaExpandedView, nextState is IslandState.MediaExpanded);
        SetView(RecordingView, nextState is IslandState.Recording);
        SetView(ThinkingView, nextState is IslandState.Thinking);
        SetView(ResponseView, nextState is IslandState.Response);

        var pulse = (Storyboard)Resources["PulseStoryboard"];
        if (nextState is IslandState.Recording)
        {
            GlowBorder.Opacity = 0.45;
            pulse.Begin(this, true);
        }
        else
        {
            pulse.Stop(this);
            GlowBorder.Opacity = 0;
            GlowScale.ScaleX = 1;
            GlowScale.ScaleY = 1;
        }
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
}

public enum IslandState
{
    MediaCompact,
    MediaExpanded,
    Recording,
    Thinking,
    Response
}
