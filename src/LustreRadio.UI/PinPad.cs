using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

/// <summary>Sopstvena tastatura za PIN (ne koristi Windows tastaturu; spec. 3.7). Cifre se nikad ne prikazuju.</summary>
internal sealed class PinPad : Grid
{
    private readonly PinEntry _entry = new();
    private readonly TextBlock _prompt = Theme.Label(string.Empty, 26, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _rules = Theme.Label(string.Empty, 16, Theme.Muted);
    private readonly TextBlock _masked = Theme.Label(string.Empty, 34, Theme.Gold);
    private readonly TextBlock _message = Theme.Label(string.Empty, 18, Theme.Problem);
    private readonly Button _ok;
    private readonly Button _delete;
    private readonly Button _cancel;

    public PinPad(Action<string> submitted, Action cancelled)
    {
        Background = Theme.Black;
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 3 * 104 + 2 * Theme.Gap };
        foreach (var line in new[] { _prompt, _rules, _masked, _message })
        {
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.TextAlignment = TextAlignment.Center;
            line.TextTrimming = TextTrimming.None;
            line.TextWrapping = TextWrapping.Wrap;
        }

        _masked.Height = 48;
        _masked.Margin = new Thickness(0, 6, 0, 0);
        _message.MinHeight = 26;
        _message.Margin = new Thickness(0, 0, 0, 10);
        column.Children.Add(_prompt);
        column.Children.Add(_rules);
        column.Children.Add(_masked);
        column.Children.Add(_message);

        var keys = new UniformGrid { Columns = 3 };
        foreach (var digit in "123456789")
        {
            keys.Children.Add(Key(digit));
        }

        _delete = Theme.Button(string.Empty, () =>
        {
            _entry.Backspace();
            Refresh();
        }, minWidth: Theme.Touch);
        _ok = Theme.Button(string.Empty, () =>
        {
            if (_entry.CanSubmit)
            {
                var digits = _entry.Digits;
                _entry.Clear();
                Refresh();
                submitted(digits);
            }
        }, primary: true, minWidth: Theme.Touch);
        foreach (var key in new[] { _delete, Key('0'), _ok })
        {
            Size(key);
            keys.Children.Add(key);
        }

        column.Children.Add(keys);
        _cancel = Theme.Button(string.Empty, cancelled);
        _cancel.Margin = new Thickness(Theme.Gap / 2, Theme.Gap, Theme.Gap / 2, 0);
        column.Children.Add(_cancel);
        Children.Add(column);
        Refresh();
    }

    public void Show(string language, T prompt, string message = "")
    {
        _prompt.Text = Texts.Get(language, prompt);
        _rules.Text = Texts.Get(language, T.PinRules);
        _message.Text = message;
        Theme.SetText(_delete, Texts.Get(language, T.Delete));
        Theme.SetText(_ok, Texts.Get(language, T.Ok));
        Theme.SetText(_cancel, Texts.Get(language, T.Cancel));
        Refresh();
    }

    public void Clear()
    {
        _entry.Clear();
        Refresh();
    }

    private Button Key(char digit)
    {
        var key = Theme.Button(digit.ToString(), () =>
        {
            _entry.Add(digit);
            _message.Text = string.Empty;
            Refresh();
        }, minWidth: Theme.Touch);
        if (key.Content is TextBlock label)
        {
            label.FontSize = 28;
        }

        Size(key);
        return key;
    }

    private static void Size(Button key)
    {
        key.Height = 72;
        key.Margin = new Thickness(Theme.Gap / 2);
        key.Padding = new Thickness(4, 0, 4, 0);
    }

    private void Refresh()
    {
        _masked.Text = _entry.Masked;
        _ok.IsEnabled = _entry.CanSubmit;
        _delete.IsEnabled = _entry.Digits.Length > 0;
    }
}
