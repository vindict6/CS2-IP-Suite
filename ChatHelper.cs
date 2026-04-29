using CounterStrikeSharp.API.Modules.Utils;

namespace CS2IPSuite;

/// <summary>
/// Centralizes all colored chat strings used by the plugin so that
/// formatting/branding stays consistent.
/// </summary>
public static class ChatHelper
{
    // Prefix every line with the plugin tag.
    public static readonly string Prefix =
        $" {ChatColors.Gold}[{ChatColors.LightBlue}CS2-IP-Suite{ChatColors.Gold}]{ChatColors.Default} ";

    public static string Info(string msg)    => $"{Prefix}{ChatColors.Default}{msg}";
    public static string Success(string msg) => $"{Prefix}{ChatColors.LightGreen}{msg}";
    public static string Error(string msg)   => $"{Prefix}{ChatColors.LightRed}{msg}";
    public static string Warn(string msg)    => $"{Prefix}{ChatColors.Gold}{msg}";

    /// <summary>A subdued, gray "you typed:" echo so the admin can see what
    /// they entered even though it was hidden from the public chat.</summary>
    public static string Echo(string msg)
        => $" {ChatColors.Grey}[hidden] {msg}{ChatColors.Default}";

    public static string Header(string msg)
        => $"{Prefix}{ChatColors.Gold}{msg}";

    public static string Field(string label, string value)
        => $" {ChatColors.LightBlue}{label}:{ChatColors.Default} {value}";

    /// <summary>Strip CS2 chat color codes so the same line is readable in
    /// the server console too.</summary>
    public static string StripColors(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (c >= ' ') sb.Append(c);   // drop control bytes \x01..\x10
        return sb.ToString();
    }
}
