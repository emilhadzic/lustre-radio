namespace LustreRadio.Audio;

/// <summary>Equal-power krive: zbir snaga ulazne i izlazne pesme ostaje stalan tokom prelaza.</summary>
public static class FadeCurve
{
    public static float EqualPowerIn(double t) => (float)Math.Sin(Math.Clamp(t, 0.0, 1.0) * (Math.PI / 2.0));

    public static float EqualPowerOut(double t) => (float)Math.Cos(Math.Clamp(t, 0.0, 1.0) * (Math.PI / 2.0));
}
