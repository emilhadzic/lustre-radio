using System.Windows;
using System.Windows.Controls;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

/// <summary>Podaci o najavi i njenoj licenci pri rucnom uvozu (spec. 15). Proveru licence radi audio proces.</summary>
internal sealed class VoiceImportWindow : Window
{
    private readonly Dictionary<string, TextBox> _texts = [];
    private readonly Dictionary<string, ComboBox> _choices = [];

    public VoiceImportWindow(string language)
    {
        Title = Texts.Get(language, T.VoiceFormTitle);
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Black;
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Field(T label, string key, bool multiline = false)
        {
            panel.Children.Add(Theme.Label(Texts.Get(language, label), 16, Theme.Muted));
            var box = new TextBox { FontSize = 18, Margin = new Thickness(0, 4, 0, 12), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 70 : 0 };
            _texts[key] = box;
            panel.Children.Add(box);
        }

        void Choice(T label, string key, params string[] values)
        {
            panel.Children.Add(Theme.Label(Texts.Get(language, label), 16, Theme.Muted));
            var box = new ComboBox { FontSize = 18, Margin = new Thickness(0, 4, 0, 12), ItemsSource = values, SelectedIndex = 0 };
            _choices[key] = box;
            panel.Children.Add(box);
        }

        Field(T.VoiceFieldText, "text", multiline: true);
        Choice(T.VoiceFieldCategory, "category", "Ident", "Welcome", "Long", "Seasonal", "Holiday", "Event");
        Choice(T.VoiceFieldLanguage, "language", "Serbian", "English");
        Choice(T.VoiceFieldVoice, "voice", "Female", "Male");
        Field(T.VoiceFieldService, "service");
        Field(T.VoiceFieldModel, "model");
        Field(T.VoiceFieldPlan, "plan");
        Field(T.VoiceFieldPayment, "payment");
        Field(T.VoiceFieldGenerated, "generated");
        Field(T.VoiceFieldTerms, "terms");
        _texts["generated"].Text = DateTime.Now.ToString("dd.MM.yyyy.", System.Globalization.CultureInfo.InvariantCulture);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Theme.Button(Texts.Get(language, T.Cancel), () => DialogResult = false);
        cancel.Margin = new Thickness(0, 0, 12, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Theme.Button(Texts.Get(language, T.VoiceSave), () => DialogResult = true, primary: true));
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 900 };
    }

    public Dictionary<string, string> Values
    {
        get
        {
            var values = _texts.ToDictionary(t => t.Key, t => t.Value.Text);
            foreach (var (key, box) in _choices)
            {
                values[key] = box.SelectedItem as string ?? string.Empty;
            }

            return values;
        }
    }
}
