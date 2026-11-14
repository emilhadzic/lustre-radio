using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LustreRadio.Ipc;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

/// <summary>
/// Ceo sadrzaj prozora: ekran za zaposlene, unos PIN-a i admin deo. Ne cuva stanje radija;
/// svaki prikaz se racuna iz poslednje poruke audio procesa.
/// </summary>
internal sealed class Shell : Grid
{
    private static readonly TimeSpan AdminIdle = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PinIdle = TimeSpan.FromMinutes(1);

    private readonly IRadio _radio;
    private readonly StaffScreen _staff;
    private readonly PinPad _pin;
    private readonly AdminPanel _admin;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sinceActivity = Stopwatch.StartNew();
    private readonly Stopwatch _sinceTouch = Stopwatch.StartNew();
    private Mode _mode = Mode.Staff;
    private NewPinFlow? _newPin;
    private bool _refreshQueued;
    private bool _volumeSending;
    private double? _volumePending;

    public Shell(IRadio radio, Func<Task<LocalProbes>> probe)
    {
        _radio = radio;
        _staff = new StaffScreen(PlayPause, Next, Volume, OpenAdmin);
        _pin = new PinPad(PinSubmitted, () => Lock(null));
        _admin = new AdminPanel(radio, Lock, StartPinChange, probe);
        Children.Add(_staff);
        Children.Add(_pin);
        Children.Add(_admin);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        PreviewMouseDown += (_, _) => Activity();
        PreviewTouchDown += (_, _) => Activity();
        _radio.Changed += OnRadioChanged;
        Refresh();
    }

    private enum Mode
    {
        Staff,
        Login,
        NewPin,
        Admin,
    }

    private string AdminLanguage => Texts.Normalize(_radio.State?.AdminLanguage);

    public void Refresh()
    {
        var state = _radio.State;
        var link = _radio.Status;
        _staff.Visibility = _mode == Mode.Staff ? Visibility.Visible : Visibility.Collapsed;
        _pin.Visibility = _mode is Mode.Login or Mode.NewPin ? Visibility.Visible : Visibility.Collapsed;
        _admin.Visibility = _mode == Mode.Admin ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == Mode.Staff)
        {
            _staff.Render(StaffView.From(state, link, DateTime.Now, DateTime.UtcNow), DateTime.Now);
        }
        else if (_mode == Mode.Admin)
        {
            if (_radio.AdminSession is null)
            {
                // veza je pukla ili je sesija istekla: nazad na ekran za zaposlene
                Lock(null);
                return;
            }

            _admin.Render(state, link);
        }
    }

    /// <summary>Otvara admin deo: prvi put trazi postavljanje PIN-a, inace unos PIN-a.</summary>
    internal void OpenAdmin()
    {
        var state = _radio.State;
        if (state is null || _radio.Status != LinkStatus.Connected)
        {
            return;
        }

        _pin.Clear();
        if (state.PinIsSet)
        {
            _mode = Mode.Login;
            _pin.Show(AdminLanguage, T.PinEnter);
        }
        else
        {
            _mode = Mode.NewPin;
            _newPin = new NewPinFlow();
            _pin.Show(AdminLanguage, T.PinSetNew);
        }

        Activity();
        Refresh();
    }

    internal void ShowAdmin(AdminSection section)
    {
        _mode = Mode.Admin;
        _admin.Open(section);
        Refresh();
    }

    internal void SetProbes(LocalProbes probes) => _admin.SetProbes(probes);

    private void OnRadioChanged()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Tick()
    {
        if (_mode == Mode.Admin && _sinceActivity.Elapsed >= AdminIdle)
        {
            Lock(null);
            return;
        }

        if (_mode is Mode.Login or Mode.NewPin && _sinceActivity.Elapsed >= PinIdle)
        {
            Lock(null);
            return;
        }

        Refresh();
    }

    private void Activity()
    {
        _sinceActivity.Restart();
        if (_mode == Mode.Admin && _sinceTouch.Elapsed >= TimeSpan.FromSeconds(30))
        {
            // dok admin radi na ekranu, sesija u audio procesu ne sme da istekne
            _sinceTouch.Restart();
            _ = _radio.SendAsync("touch", admin: true);
        }
    }

    private async void PlayPause()
    {
        var state = _radio.State;
        if (state is not null)
        {
            await SendQuietly(state.Paused ? "play" : "pause");
        }
    }

    private async void Next()
    {
        // oznaka pesme: dupli dodir ne preskace dve pesme (spec. 2.6)
        var track = _radio.State?.TrackId;
        if (track is not null)
        {
            await SendQuietly("next", new() { ["track"] = track });
        }
    }

    private async void Volume(double value)
    {
        _volumePending = value;
        if (_volumeSending)
        {
            return;
        }

        _volumeSending = true;
        try
        {
            while (_volumePending is { } next)
            {
                _volumePending = null;
                await SendQuietly("set-volume", new() { ["value"] = next.ToString("R", CultureInfo.InvariantCulture) });
            }
        }
        finally
        {
            _volumeSending = false;
        }
    }

    private async Task<IpcMessage?> SendQuietly(string name, Dictionary<string, string>? args = null, bool admin = false)
    {
        try
        {
            return await _radio.SendAsync(name, args, admin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void PinSubmitted(string digits)
    {
        Activity();
        var language = AdminLanguage;
        if (_mode == Mode.Login)
        {
            await Login(digits);
            return;
        }

        if (_mode != Mode.NewPin || _newPin is null)
        {
            return;
        }

        switch (_newPin.Submit(digits))
        {
            case NewPinStep.Repeat:
                _pin.Show(language, T.PinRepeat);
                break;
            case NewPinStep.Mismatch:
                _pin.Show(language, T.PinSetNew, Texts.Get(language, T.PinMismatch));
                break;
            case NewPinStep.Done:
                var pin = _newPin.Pin!;
                _newPin = null;
                var set = await SendQuietly("set-pin", new() { ["pin"] = pin }, admin: true);
                if (set is { Ok: true })
                {
                    // promena PIN-a ponistava sve sesije; prijava novim PIN-om
                    _mode = Mode.Login;
                    await Login(pin);
                }
                else if (set?.Error == "admin-required")
                {
                    Lock(T.SessionExpired);
                }
                else
                {
                    _newPin = new NewPinFlow();
                    _pin.Show(language, T.PinSetNew, Texts.Get(language, T.CommandFailed));
                }

                break;
            default:
                _pin.Show(language, T.PinSetNew);
                break;
        }
    }

    private async Task Login(string digits)
    {
        var language = AdminLanguage;
        var response = await SendQuietly("login", new() { ["pin"] = digits });
        if (response is { Ok: true } && response.Data?.GetValueOrDefault("session") is { Length: > 0 } session)
        {
            _radio.AdminSession = session;
            _sinceTouch.Restart();
            ShowAdmin(AdminSection.Library);
            return;
        }

        _mode = Mode.Login;
        var message = response?.Error switch
        {
            "wrong-pin" => Texts.Get(language, T.PinWrong),
            "locked-out" => Texts.Format(language, T.PinLocked, Minutes(response.Data?.GetValueOrDefault("retryAfterSeconds"))),
            _ => Texts.Get(language, T.CommandFailed),
        };
        _pin.Show(language, T.PinEnter, message);
        Refresh();
    }

    private void StartPinChange()
    {
        _mode = Mode.NewPin;
        _newPin = new NewPinFlow();
        _pin.Clear();
        _pin.Show(AdminLanguage, T.PinSetNew);
        Refresh();
    }

    /// <summary>Zakljucava admin deo i vraca ekran za zaposlene.</summary>
    private void Lock(T? reason)
    {
        _ = reason; // razlog se ne prikazuje preko ekrana za zaposlene; sledeci ulazak ionako trazi PIN
        if (_radio.AdminSession is not null)
        {
            _ = _radio.SendAsync("logout", admin: true);
            _radio.AdminSession = null;
        }

        _newPin = null;
        _pin.Clear();
        _mode = Mode.Staff;
        Refresh();
    }

    private static int Minutes(string? seconds) =>
        int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(1, (int)Math.Ceiling(value / 60.0)) : 5;
}
