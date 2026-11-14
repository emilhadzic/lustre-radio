using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LustreRadio.Ipc;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

internal enum AdminSection
{
    Library,
    License,
    Volume,
    Language,
    Pin,
    System,
}

/// <summary>
/// Admin deo (posle PIN-a). U ovoj fazi sadrzi samo ono sto zaista radi: muzika, opseg glasnoce,
/// jezik, PIN i provera sistema. Raspored, miksevi, licence, glas i backup dolaze sa svojim fazama.
/// Sve izmene izvrsava audio proces; interfejs samo salje komande (spec. 2.6).
/// </summary>
internal sealed class AdminPanel : Grid
{
    private readonly IRadio _radio;
    private readonly Action<T?> _lock;
    private readonly Action _changePin;
    private readonly Func<Task<LocalProbes>> _probe;
    private readonly TextBlock _heading = Theme.Label(string.Empty, 26, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _message = Theme.Label(string.Empty, 18, Theme.Attention);
    private readonly Button _lockButton;
    private readonly Dictionary<AdminSection, Button> _nav = [];
    private readonly StackPanel _content = new() { Margin = new Thickness(28, 20, 28, 20) };
    private LocalProbes _probes = new();
    private bool _probing;
    private string _builtFor = string.Empty;
    private LevelBar? _minBar;
    private LevelBar? _maxBar;
    private TextBlock? _minLabel;
    private TextBlock? _maxLabel;
    private RadioState? _state;
    private LinkStatus _link;

    public AdminPanel(IRadio radio, Action<T?> lockRequested, Action changePin, Func<Task<LocalProbes>> probe)
    {
        _radio = radio;
        _lock = lockRequested;
        _changePin = changePin;
        _probe = probe;
        Background = Theme.Black;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var top = new DockPanel { Background = Theme.Anthracite, LastChildFill = false };
        _heading.Margin = new Thickness(28, 0, 0, 0);
        _lockButton = Theme.Button(string.Empty, () => _lock(null), primary: true);
        _lockButton.Margin = new Thickness(0, Theme.Gap, 28, Theme.Gap);
        DockPanel.SetDock(_heading, Dock.Left);
        DockPanel.SetDock(_lockButton, Dock.Right);
        top.Children.Add(_heading);
        top.Children.Add(_lockButton);
        SetColumnSpan(top, 2);
        Children.Add(top);

        var nav = new StackPanel { Background = Theme.Anthracite };
        foreach (var section in Enum.GetValues<AdminSection>())
        {
            var button = Theme.Button(string.Empty, () => Open(section));
            button.Margin = new Thickness(Theme.Gap, Theme.Gap, Theme.Gap, 0);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            _nav[section] = button;
            nav.Children.Add(button);
        }

        SetRow(nav, 1);
        SetRowSpan(nav, 2);
        Children.Add(nav);

        var scroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
        };
        SetRow(scroll, 1);
        SetColumn(scroll, 1);
        Children.Add(scroll);

        _message.Margin = new Thickness(28, 0, 28, 16);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextTrimming = TextTrimming.None;
        _message.MinHeight = 26;
        SetRow(_message, 2);
        SetColumn(_message, 1);
        Children.Add(_message);
    }

    public AdminSection Section { get; private set; } = AdminSection.Library;

    private string Language => Texts.Normalize(_state?.AdminLanguage);

    public void Open(AdminSection section)
    {
        Section = section;
        _message.Text = string.Empty;
        if (section == AdminSection.System)
        {
            RunProbe();
        }

        Render(_state, _link);
    }

    public void SetProbes(LocalProbes probes) => _probes = probes;

    public void Render(RadioState? state, LinkStatus link)
    {
        _state = state;
        _link = link;
        var language = Language;
        _heading.Text = Texts.Get(language, T.AdminTitle);
        Theme.SetText(_lockButton, Texts.Get(language, T.Lock));
        Theme.SetText(_nav[AdminSection.Library], Texts.Get(language, T.SectionLibrary));
        Theme.SetText(_nav[AdminSection.License], Texts.Get(language, T.SectionLicense));
        Theme.SetText(_nav[AdminSection.Volume], Texts.Get(language, T.SectionVolume));
        Theme.SetText(_nav[AdminSection.Language], Texts.Get(language, T.SectionLanguage));
        Theme.SetText(_nav[AdminSection.Pin], Texts.Get(language, T.SectionPin));
        Theme.SetText(_nav[AdminSection.System], Texts.Get(language, T.SectionSystem));
        foreach (var (section, button) in _nav)
        {
            Theme.SetSelected(button, section == Section);
        }

        if (_minBar?.IsDragging == true || _maxBar?.IsDragging == true)
        {
            return;
        }

        var key = Signature(state, link);
        if (key == _builtFor)
        {
            return;
        }

        _builtFor = key;
        _content.Children.Clear();
        _minBar = _maxBar = null;
        switch (Section)
        {
            case AdminSection.Library:
                BuildLibrary(state);
                break;
            case AdminSection.License:
                BuildLicense(state);
                break;
            case AdminSection.Volume:
                BuildVolume(state);
                break;
            case AdminSection.Language:
                BuildLanguage(state);
                break;
            case AdminSection.Pin:
                Title(T.SectionPin);
                Row(Theme.Button(Texts.Get(language, T.ChangePin), _changePin, primary: true));
                break;
            default:
                BuildSystem(state, link);
                break;
        }
    }

    private string Signature(RadioState? s, LinkStatus link) => string.Join(
        '|',
        Section,
        Language,
        link,
        s?.StaffLanguage,
        string.Join(';', s?.MusicFolders ?? []),
        s?.Scanning,
        s?.TotalTracks,
        s?.BroadcastableTracks,
        s?.LicenseOrganizations,
        s?.LicenseDeclaredLocal,
        s?.VolumeMin.ToString("R", CultureInfo.InvariantCulture),
        s?.VolumeMax.ToString("R", CultureInfo.InvariantCulture),
        Section == AdminSection.System ? $"{s?.Output}|{s?.Device}|{s?.PinIsSet}|{s?.SafeMode}|{s?.ClosesAtLocal}|{s?.OpensAtLocal}|{_probing}|{_probes.FreeDiskGb}|{(_probes.AutostartProblems is null ? "-" : string.Join(',', _probes.AutostartProblems))}" : string.Empty);

    private void BuildLibrary(RadioState? state)
    {
        var language = Language;
        Title(T.MusicFolders);
        var folders = state?.MusicFolders ?? [];
        if (folders.Count == 0)
        {
            Row(Theme.Label(Texts.Get(language, T.NoFolders), 18, Theme.Muted));
        }

        foreach (var folder in folders)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, Theme.Gap) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = Theme.Label(folder, 18);
            path.Margin = new Thickness(0, 0, Theme.Gap, 0);
            var remove = Theme.Button(Texts.Get(language, T.RemoveFolder), () => SetFolders(folders.Where(f => f != folder).ToList()));
            SetColumn(remove, 1);
            row.Children.Add(path);
            row.Children.Add(remove);
            _content.Children.Add(row);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Theme.Gap, 0, Theme.Gap * 2) };
        var add = Theme.Button(Texts.Get(language, T.AddFolder), () => AddFolder(folders), primary: true);
        add.Margin = new Thickness(0, 0, Theme.Gap, 0);
        var rescan = Theme.Button(Texts.Get(language, T.Rescan), () => Send("rescan"));
        rescan.IsEnabled = state?.Scanning != true;
        actions.Children.Add(add);
        actions.Children.Add(rescan);
        _content.Children.Add(actions);

        Row(Theme.Label(Texts.Format(language, T.TracksSummary, state?.TotalTracks ?? 0, state?.BroadcastableTracks ?? 0), 18));
        if (state?.Scanning == true)
        {
            Row(Theme.Label(Texts.Get(language, T.Scanning), 18, Theme.Gold));
        }
    }

    private void AddFolder(IReadOnlyList<string> current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        if (!current.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            SetFolders([.. current, dialog.FolderName]);
        }
    }

    private void SetFolders(IReadOnlyList<string> folders)
    {
        var args = new Dictionary<string, string>();
        for (var i = 0; i < folders.Count; i++)
        {
            args["folder" + i.ToString("000", CultureInfo.InvariantCulture)] = folders[i];
        }

        Send("set-music-folders", args);
    }

    private void BuildLicense(RadioState? state)
    {
        var language = Language;
        Title(T.SectionLicense);
        var explain = Theme.Label(Texts.Get(language, T.LicenseExplain), 18, Theme.Muted);
        explain.TextWrapping = TextWrapping.Wrap;
        explain.TextTrimming = TextTrimming.None;
        explain.MaxWidth = 640;
        Row(explain);

        if (state?.LicenseOrganizations is { Length: > 0 } organizations)
        {
            var when = state.LicenseDeclaredLocal is { } declared
                ? declared.ToString("dd.MM.yyyy. HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            Row(Theme.Label(Texts.Format(language, T.LicenseDeclared, organizations, when), 18, Theme.Good));
        }
        else
        {
            Row(Theme.Label(Texts.Get(language, T.LicenseMissing), 18, Theme.Attention));
            Row(Theme.Button(Texts.Get(language, T.LicenseDeclare), () => Send("declare-collective-license", new() { ["organizations"] = "SOKOJ, OFPS" }), primary: true));
        }

        Row(Theme.Label(Texts.Format(language, T.TracksSummary, state?.TotalTracks ?? 0, state?.BroadcastableTracks ?? 0), 18));
    }

    private void BuildVolume(RadioState? state)
    {
        var language = Language;
        Title(T.SectionVolume);
        var help = Theme.Label(Texts.Get(language, T.VolumeRangeHelp), 18, Theme.Muted);
        help.TextWrapping = TextWrapping.Wrap;
        help.TextTrimming = TextTrimming.None;
        Row(help);

        var min = state?.VolumeMin ?? 0.2;
        var max = state?.VolumeMax ?? 1.0;
        _minLabel = Theme.Label(string.Empty, 18);
        _maxLabel = Theme.Label(string.Empty, 18);
        _minBar = new LevelBar { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        _maxBar = new LevelBar { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        _minBar.Set(0, 1, min);
        _maxBar.Set(0, 1, max);
        _minBar.Changing += _ => RangeLabels();
        _maxBar.Changing += _ => RangeLabels();
        _minBar.Committed += value => SendRange(value, Math.Max(value, _maxBar.Value));
        _maxBar.Committed += value => SendRange(Math.Min(value, _minBar.Value), value);
        _minLabel.Margin = new Thickness(0, Theme.Gap, 0, 0);
        _maxLabel.Margin = new Thickness(0, Theme.Gap, 0, 0);
        _content.Children.Add(_minLabel);
        _content.Children.Add(_minBar);
        _content.Children.Add(_maxLabel);
        _content.Children.Add(_maxBar);
        RangeLabels();
    }

    private void RangeLabels()
    {
        if (_minBar is null || _maxBar is null || _minLabel is null || _maxLabel is null)
        {
            return;
        }

        var language = Language;
        _minLabel.Text = Texts.Get(language, T.VolumeMin) + "  " + Percent(_minBar.Value);
        _maxLabel.Text = Texts.Get(language, T.VolumeMax) + "  " + Percent(_maxBar.Value);
    }

    private void SendRange(double min, double max)
    {
        _builtFor = string.Empty; // posle odgovora prikazi ono sto je audio proces zaista prihvatio
        Send("set-volume-range", new()
        {
            ["min"] = Math.Round(min, 2).ToString("R", CultureInfo.InvariantCulture),
            ["max"] = Math.Round(max, 2).ToString("R", CultureInfo.InvariantCulture),
        });
    }

    private void BuildLanguage(RadioState? state)
    {
        Title(T.SectionLanguage);
        LanguageRow(T.LanguageStaff, "staff", Texts.Normalize(state?.StaffLanguage));
        LanguageRow(T.LanguageAdmin, "admin", Texts.Normalize(state?.AdminLanguage));
    }

    private void LanguageRow(T label, string screen, string current)
    {
        var language = Language;
        var name = Theme.Label(Texts.Get(language, label), 18);
        name.Margin = new Thickness(0, Theme.Gap, 0, Theme.Gap / 2);
        _content.Children.Add(name);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (code, text) in new[] { (Texts.Serbian, T.Serbian), (Texts.English, T.English) })
        {
            var button = Theme.Button(Texts.Get(language, text), () => Send("set-language", new() { ["screen"] = screen, ["lang"] = code }), minWidth: 160);
            button.Margin = new Thickness(0, 0, Theme.Gap, 0);
            Theme.SetSelected(button, current == code);
            row.Children.Add(button);
        }

        _content.Children.Add(row);
    }

    private void BuildSystem(RadioState? state, LinkStatus link)
    {
        var language = Language;
        Title(T.SectionSystem);
        foreach (var line in SystemCheck.Evaluate(state, link, _probes, language))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Theme.For(line.Health), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
            var name = Theme.Label(line.Name, 18);
            name.VerticalAlignment = VerticalAlignment.Top;
            var detail = Theme.Label(line.Detail, 18, line.Health == Health.Good ? Theme.Muted : Theme.Text);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.None;
            SetColumn(name, 1);
            SetColumn(detail, 2);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(detail);
            _content.Children.Add(row);
        }

        var refresh = Theme.Button(Texts.Get(language, T.Refresh), RunProbe);
        refresh.IsEnabled = !_probing;
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(0, Theme.Gap, 0, 0);
        _content.Children.Add(refresh);
    }

    private async void RunProbe()
    {
        if (_probing)
        {
            return;
        }

        _probing = true;
        try
        {
            _probes = await _probe();
        }
        catch (Exception)
        {
            _probes = new LocalProbes();
        }
        finally
        {
            _probing = false;
            Render(_state, _link);
        }
    }

    private async void Send(string name, Dictionary<string, string>? args = null)
    {
        var language = Language;
        IpcMessage? response;
        try
        {
            response = await _radio.SendAsync(name, args, admin: true);
        }
        catch (Exception)
        {
            response = null;
        }

        if (response is { Ok: true })
        {
            _message.Text = string.Empty;
        }
        else if (response?.Error == "admin-required")
        {
            _lock(T.SessionExpired);
        }
        else if (response?.Error == "folder-not-found")
        {
            _message.Text = Texts.Format(language, T.FolderNotFound, response.Data?.GetValueOrDefault("folder") ?? string.Empty);
        }
        else
        {
            _message.Text = Texts.Get(language, T.CommandFailed);
        }

        _builtFor = string.Empty;
        Render(_radio.State, _radio.Status);
    }

    private void Title(T key)
    {
        var title = Theme.Label(Texts.Get(Language, key), 22, Theme.Gold, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 16);
        _content.Children.Add(title);
    }

    private void Row(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.Margin = new Thickness(0, 0, 0, Theme.Gap);
        _content.Children.Add(element);
    }

    private static string Percent(double value) => Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + "%";
}
