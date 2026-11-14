using LustreRadio.Core.Security;

namespace LustreRadio.Presentation;

/// <summary>Unos PIN-a sopstvenom tastaturom na ekranu (spec. 3.7): samo cifre, 4 do 8.</summary>
public sealed class PinEntry
{
    public const int MaxLength = 8;

    public string Digits { get; private set; } = string.Empty;

    public bool CanSubmit => PinVault.IsValidPin(Digits);

    /// <summary>Prikaz bez cifara: po jedna tacka za svaku unetu cifru.</summary>
    public string Masked => new('●', Digits.Length);

    public bool Add(char digit)
    {
        if (!char.IsAsciiDigit(digit) || Digits.Length >= MaxLength)
        {
            return false;
        }

        Digits += digit;
        return true;
    }

    public void Backspace()
    {
        if (Digits.Length > 0)
        {
            Digits = Digits[..^1];
        }
    }

    public void Clear() => Digits = string.Empty;
}

public enum NewPinStep
{
    First,
    Repeat,
    Done,
    Mismatch,
}

/// <summary>Postavljanje novog PIN-a: unos pa ponavljanje; ako se ne poklope, krece se iz pocetka.</summary>
public sealed class NewPinFlow
{
    private string? _first;

    public NewPinStep Step { get; private set; } = NewPinStep.First;

    public string? Pin { get; private set; }

    public NewPinStep Submit(string digits)
    {
        if (!PinVault.IsValidPin(digits))
        {
            return Step;
        }

        if (_first is null)
        {
            _first = digits;
            return Step = NewPinStep.Repeat;
        }

        if (_first == digits)
        {
            Pin = digits;
            return Step = NewPinStep.Done;
        }

        _first = null;
        Step = NewPinStep.First;
        return NewPinStep.Mismatch;
    }
}
