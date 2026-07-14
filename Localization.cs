using System.Globalization;
using System.Reflection;
using System.Resources;

namespace AndroidLogViewer;

public static class Localization
{
    private static readonly ResourceManager Resources = new("AndroidLogViewer.Resources.Strings", Assembly.GetExecutingAssembly());

    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentUICulture;

    public static void SetCulture(string cultureName)
    {
        CurrentCulture = string.IsNullOrWhiteSpace(cultureName)
            ? CultureInfo.CurrentUICulture
            : CultureInfo.GetCultureInfo(cultureName);
    }

    public static string Text(string key)
    {
        return Resources.GetString(key, CurrentCulture) ?? key;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CurrentCulture, Text(key), args);
    }
}
