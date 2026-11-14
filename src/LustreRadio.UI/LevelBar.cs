using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LustreRadio.UI;

/// <summary>
/// Klizac za prst: cela visina od 56 px reaguje na dodir, vrednost se menja prevlacenjem ili dodirom.
/// (Ugradjeni WPF klizac ima premali hvatac za monitor na dodir.)
/// </summary>
internal sealed class LevelBar : FrameworkElement
{
    private const double ThumbRadius = 16;
    private double _minimum;
    private double _maximum = 1;
    private double _value;

    public LevelBar()
    {
        Height = Theme.Touch;
        MinWidth = 160;
        Focusable = false;
        Cursor = Cursors.Hand;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Vrednost se menja dok korisnik vuce.</summary>
    public event Action<double>? Changing;

    /// <summary>Korisnik je pustio klizac.</summary>
    public event Action<double>? Committed;

    public bool IsDragging { get; private set; }

    public double Value => _value;

    public void Set(double minimum, double maximum, double value)
    {
        if (IsDragging)
        {
            return;
        }

        _minimum = minimum;
        _maximum = Math.Max(minimum, maximum);
        var clamped = Math.Clamp(value, _minimum, _maximum);
        if (Math.Abs(clamped - _value) > 0.0005)
        {
            _value = clamped;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 240 : Math.Max(160, availableSize.Width), Theme.Touch);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var middle = ActualHeight / 2;

        // providna podloga: ceo pravougaonik prima dodir
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        var left = ThumbRadius;
        var right = Math.Max(left, width - ThumbRadius);
        var x = left + (right - left) * Fraction();
        var opacity = IsEnabled ? 1.0 : 0.3;
        drawingContext.PushOpacity(opacity);
        drawingContext.DrawRoundedRectangle(Theme.Line, null, new Rect(left, middle - 3, right - left, 6), 3, 3);
        drawingContext.DrawRoundedRectangle(Theme.Gold, null, new Rect(left, middle - 3, Math.Max(0, x - left), 6), 3, 3);
        drawingContext.DrawEllipse(Theme.Gold, null, new Point(x, middle), ThumbRadius, ThumbRadius);
        drawingContext.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        IsDragging = true;
        CaptureMouse();
        Move(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging)
        {
            Move(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        Move(e.GetPosition(this).X);
        IsDragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(_value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (IsDragging)
        {
            IsDragging = false;
            Committed?.Invoke(_value);
        }
    }

    private double Fraction() => _maximum > _minimum ? (_value - _minimum) / (_maximum - _minimum) : 1;

    private void Move(double x)
    {
        var left = ThumbRadius;
        var right = Math.Max(left + 1, ActualWidth - ThumbRadius);
        var fraction = Math.Clamp((x - left) / (right - left), 0, 1);
        _value = _minimum + (_maximum - _minimum) * fraction;
        InvalidateVisual();
        Changing?.Invoke(_value);
    }
}
