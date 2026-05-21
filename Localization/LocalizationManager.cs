using System.Globalization;
using System.Resources;

namespace LiveCaptioner.Localization;

public static class LocalizationManager
{
    private static readonly ResourceManager ResourceManager =
        new("LiveCaptioner.Resources.Strings", typeof(LocalizationManager).Assembly);

    public static string Language { get; private set; } = "zh-CN";

    public static void ApplyCulture(string language)
    {
        Language = NormalizeLanguage(language);
        var culture = new CultureInfo(Language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string T(string key)
    {
        return ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    private static string NormalizeLanguage(string language)
    {
        return language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-CN";
    }
}
