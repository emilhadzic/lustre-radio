using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LustreRadio.Presentation;
using ShapePath = System.Windows.Shapes.Path;

namespace LustreRadio.UI;

/// <summary>Izgled: crna i antracit, zlatni detalji, bez efekata (spec. 12). Sve kontrole su najmanje 56 px (spec. 3.7).</summary>
internal static class Theme
{
    public const double Touch = 56;
    public const double Gap = 12;

    public static readonly Brush Black = Frozen(0x0E, 0x0E, 0x10);
    public static readonly Brush Anthracite = Frozen(0x1B, 0x1C, 0x20);
    public static readonly Brush Panel = Frozen(0x26, 0x27, 0x2C);
    public static readonly Brush Line = Frozen(0x3A, 0x3B, 0x42);
    public static readonly Brush Gold = Frozen(0xC9, 0xA2, 0x4B);
    public static readonly Brush GoldDim = Frozen(0x7A, 0x64, 0x32);
    public static readonly Brush Text = Frozen(0xF2, 0xF0, 0xEA);
    public static readonly Brush Muted = Frozen(0x9A, 0x99, 0x95);
    public static readonly Brush Good = Frozen(0x5F, 0xB3, 0x7A);
    public static readonly Brush Attention = Frozen(0xE0, 0xB0, 0x4A);
    public static readonly Brush Problem = Frozen(0xD9, 0x5B, 0x4F);

    public static readonly FontFamily Font = new("Segoe UI");

    private static readonly ControlTemplate ButtonTemplate = BuildButtonTemplate();

    public static Brush For(Health health) => health switch
    {
        Health.Good => Good,
        Health.Attention => Attention,
        _ => Problem,
    };

    public static TextBlock Label(string text, double size, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        Foreground = color ?? Text,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Dugme sa tekstom. Dugme bez funkcije ne moze da se napravi: radnja je obavezna (T-UI-01).</summary>
    public static Button Button(string text, Action onClick, bool primary = false, double minWidth = 120)
    {
        var button = Bare(Label(text, 18, primary ? Black : Text, FontWeights.SemiBold), onClick, primary);
        button.MinWidth = minWidth;
        button.Padding = new Thickness(20, 0, 20, 0);
        return button;
    }

    public static Button IconButton(string geometry, Action onClick, double size, bool primary = false)
    {
        var icon = new ShapePath
        {
            Data = Geometry.Parse(geometry),
            Fill = primary ? Black : Text,
            Stretch = Stretch.Uniform,
            Width = size * 0.34,
            Height = size * 0.34,
        };
        var button = Bare(icon, onClick, primary);
        button.Width = size;
        button.Height = size;
        return button;
    }

    public static void SetIcon(Button button, string geometry)
    {
        if (button.Content is ShapePath path)
        {
            path.Data = Geometry.Parse(geometry);
        }
    }

    public static void SetText(Button button, string text)
    {
        if (button.Content is TextBlock label)
        {
            label.Text = text;
        }
    }

    /// <summary>Oznacava izabrano dugme (npr. jezik ili odeljak).</summary>
    public static void SetSelected(Button button, bool selected)
    {
        button.Background = selected ? Gold : Panel;
        button.BorderBrush = selected ? Gold : Line;
        if (button.Content is TextBlock label)
        {
            label.Foreground = selected ? Black : Text;
        }
    }

    private static Button Bare(object content, Action onClick, bool primary)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        var button = new Button
        {
            Content = content,
            Template = ButtonTemplate,
            Background = primary ? Gold : Panel,
            BorderBrush = primary ? Gold : Line,
            BorderThickness = new Thickness(1),
            MinHeight = Touch,
            MinWidth = Touch,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static ControlTemplate BuildButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.3));
        template.Triggers.Add(pressed);
        template.Triggers.Add(disabled);
        template.Seal();
        return template;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

internal static class Icons
{
    public const string Play = "M0,0 L0,24 L21,12 Z";
    public const string Pause = "M0,0 H7 V24 H0 Z M13,0 H20 V24 H13 Z";
    public const string Next = "M0,0 L0,24 L16,12 Z M17,0 H22 V24 H17 Z";
}
