using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

/// <summary>
/// Ekran za zaposlene. Prikazuje samo ono sto audio proces zaista salje; nema dugmeta bez funkcije.
/// Jos nisu na ekranu (dolaze sa svojim fazama): omot albuma, sledeca pesma, "Mirnije/Zivlje".
/// </summary>
internal sealed class StaffScreen : Grid
{
    private readonly TextBlock _logo = Theme.Label("L U S T R E   R A D I O", 22, Theme.Gold, FontWeights.SemiBold);
    private readonly TextBlock _date = Theme.Label(string.Empty, 18, Theme.Muted);
    private readonly TextBlock _clock = Theme.Label(string.Empty, 30, Theme.Text, FontWeights.Light);
    private readonly TextBlock _program = Theme.Label(string.Empty, 18, Theme.Gold);
    private readonly TextBlock _nextLine = Theme.Label(string.Empty, 18, Theme.Muted);
    private readonly TextBlock _title = Theme.Label(string.Empty, 46, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _artist = Theme.Label(string.Empty, 26, Theme.Muted);
    private readonly TextBlock _position = Theme.Label(string.Empty, 16, Theme.Muted);
    private readonly TextBlock _duration = Theme.Label(string.Empty, 16, Theme.Muted);
    private readonly Border _progressFill = new() { Background = Theme.Gold, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _progressTrack = new() { Height = 6 };
    private readonly TextBlock _volumeLabel = Theme.Label(string.Empty, 16, Theme.Muted);
    private readonly TextBlock _status = Theme.Label(string.Empty, 18, Theme.Text);
    private readonly TextBlock _closes = Theme.Label(string.Empty, 18, Theme.Muted);
    private readonly Border _dot = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _nowPlaying = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _playPause;
    private readonly Button _next;
    private readonly Button _admin;
    private readonly LevelBar _volume = new();
    private double _progress;
    private bool _showPlay;

    public StaffScreen(Action playPause, Action next, Action<double> volume, Action admin)
    {
        Background = Theme.Black;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // zaglavlje
        var header = new DockPanel { Margin = new Thickness(36, 24, 36, 0), LastChildFill = false };
        DockPanel.SetDock(_logo, Dock.Left);
        header.Children.Add(_logo);
        var when = new StackPanel { Orientation = Orientation.Horizontal };
        _date.Margin = new Thickness(0, 0, 20, 0);
        when.Children.Add(_date);
        when.Children.Add(_clock);
        DockPanel.SetDock(when, Dock.Right);
        header.Children.Add(when);
        Add(header, 0);

        // pesma
        _program.Margin = new Thickness(0, 0, 0, 10);
        _artist.Margin = new Thickness(0, 6, 0, 26);
        _nowPlaying.Margin = new Thickness(36, 0, 36, 0);
        _nowPlaying.Children.Add(_program);
        _nowPlaying.Children.Add(_title);
        _nowPlaying.Children.Add(_artist);
        _progressTrack.Children.Add(new Border { Background = Theme.Line, CornerRadius = new CornerRadius(3) });
        _progressTrack.Children.Add(_progressFill);
        _progressTrack.SizeChanged += (_, _) => ApplyProgress();
        _nowPlaying.Children.Add(_progressTrack);
        var times = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_position, Dock.Left);
        DockPanel.SetDock(_duration, Dock.Right);
        times.Children.Add(_position);
        times.Children.Add(_duration);
        _nextLine.Margin = new Thickness(0, 14, 0, 0);
        _nowPlaying.Children.Add(times);
        _nowPlaying.Children.Add(_nextLine);
        Add(_nowPlaying, 1);

        // kontrole
        _playPause = Theme.IconButton(Icons.Pause, playPause, 96, primary: true);
        _next = Theme.IconButton(Icons.Next, next, 72);
        _next.Margin = new Thickness(Theme.Gap * 2, 0, Theme.Gap * 3, 0);
        _volume.Changing += volume;
        _volume.Committed += volume;
        var controls = new Grid { Margin = new Thickness(36, 0, 36, 24) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var volumeBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        volumeBox.Children.Add(_volumeLabel);
        volumeBox.Children.Add(_volume);
        SetColumn(_playPause, 0);
        SetColumn(_next, 1);
        SetColumn(volumeBox, 2);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);
        controls.Children.Add(volumeBox);
        Add(controls, 2);

        // dno: stanje, vreme gasenja, admin
        _admin = Theme.Button(string.Empty, admin);
        var footer = new Grid { Background = Theme.Anthracite };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var statusBox = new DockPanel { Margin = new Thickness(36, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_dot, Dock.Left);
        statusBox.Children.Add(_dot);
        statusBox.Children.Add(_status);
        _closes.Margin = new Thickness(0, 0, 24, 0);
        _admin.Margin = new Thickness(0, Theme.Gap, 36, Theme.Gap);
        SetColumn(statusBox, 0);
        SetColumn(_closes, 1);
        SetColumn(_admin, 2);
        footer.Children.Add(statusBox);
        footer.Children.Add(_closes);
        footer.Children.Add(_admin);
        Add(footer, 3);
    }

    public bool VolumeIsDragging => _volume.IsDragging;

    public void Render(StaffView view, DateTime nowLocal)
    {
        _date.Text = view.DateLine;
        _clock.Text = Texts.Time(nowLocal);
        _program.Text = view.ProgramLine;
        _nextLine.Text = view.NextLine;
        _nextLine.Visibility = string.IsNullOrEmpty(view.NextLine) ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = view.Title;
        _artist.Text = view.Artist;
        _position.Text = view.PositionText;
        _duration.Text = view.DurationText;
        _status.Text = view.StatusLine;
        _closes.Text = view.ClosesLine;
        _dot.Background = Theme.For(view.Health);
        _nowPlaying.Opacity = view.Live ? 1 : 0.35;

        _progress = view.Progress;
        ApplyProgress();

        if (_showPlay != view.ShowPlay)
        {
            _showPlay = view.ShowPlay;
            Theme.SetIcon(_playPause, _showPlay ? Icons.Play : Icons.Pause);
        }

        _playPause.IsEnabled = view.CanPlayPause;
        _playPause.ToolTip = null;
        _next.IsEnabled = view.CanNext;
        _volume.IsEnabled = view.Live;
        _admin.IsEnabled = view.Live;
        Theme.SetText(_admin, Texts.Get(view.Language, T.Admin));

        _volume.Set(view.VolumeMin, view.VolumeMax, view.Volume);
        var shown = _volume.IsDragging ? _volume.Value : view.Volume;
        _volumeLabel.Text = Texts.Get(view.Language, T.Volume) + "  " + Math.Round(shown * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private void Add(UIElement element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    private void ApplyProgress() => _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth * Math.Clamp(_progress, 0, 1));
}
