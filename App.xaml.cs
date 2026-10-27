using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SnmpBrowser;

public partial class App : Application
{
    private static readonly string ThemeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "theme.txt");

    private static readonly Dictionary<string, string> Light = new()
    {
        ["BgBrush"] = "#F3F5F9",
        ["CardBrush"] = "#FFFFFF",
        ["LineBrush"] = "#E1E5EC",
        ["AccentBrush"] = "#2563EB",
        ["AccentSoftBrush"] = "#EAF0FF",
        ["DangerBrush"] = "#DC2626",
        ["TextBrush"] = "#1F2937",
        ["MutedBrush"] = "#6B7280",
        ["InputBrush"] = "#FFFFFF",
        ["RowAltBrush"] = "#FAFBFD",
        ["HeaderBrush"] = "#F8FAFC",
        ["GridLineBrush"] = "#EEF0F4",
        ["ChipTextBrush"] = "#3730A3",
        ["SelectTextBrush"] = "#1E3A8A",
        ["PlaceholderBrush"] = "#9CA3AF",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["BgBrush"] = "#0B1220",
        ["CardBrush"] = "#131C31",
        ["LineBrush"] = "#26324A",
        ["AccentBrush"] = "#60A5FA",
        ["AccentSoftBrush"] = "#1E2F57",
        ["DangerBrush"] = "#EF4444",
        ["TextBrush"] = "#E5E7EB",
        ["MutedBrush"] = "#9CA3AF",
        ["InputBrush"] = "#0E1626",
        ["RowAltBrush"] = "#16203A",
        ["HeaderBrush"] = "#16213A",
        ["GridLineBrush"] = "#1F2A44",
        ["ChipTextBrush"] = "#BFD0F7",
        ["SelectTextBrush"] = "#FFFFFF",
        ["PlaceholderBrush"] = "#6B7280",
    };

    public static bool IsDark { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme(LoadPreference() ?? SystemPrefersDark());
        Services.MibStore.Instance.LoadStartup();
        Services.MibRepoService.LoadInstalled();
        Services.PersonalSourceService.LoadInstalled();
        base.OnStartup(e);
    }

    /// <summary>Applica la palette chiara o scura sostituendo i pennelli dell'applicazione.</summary>
    public static void ApplyTheme(bool dark, bool save = false)
    {
        IsDark = dark;
        var res = Current.Resources;

        foreach (var (key, hex) in dark ? Dark : Light)
            res[key] = Frozen(hex);

        // Selezione di DataGrid / TreeView
        res[SystemColors.HighlightBrushKey] = Frozen(dark ? "#1E3A8A" : "#DBEAFE");
        res[SystemColors.HighlightTextBrushKey] = Frozen(dark ? "#FFFFFF" : "#1E3A8A");
        res[SystemColors.InactiveSelectionHighlightBrushKey] = Frozen(dark ? "#24345C" : "#E0E7FF");
        res[SystemColors.InactiveSelectionHighlightTextBrushKey] = Frozen(dark ? "#FFFFFF" : "#1E3A8A");

        if (save)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
                File.WriteAllText(ThemeFile, dark ? "dark" : "light");
            }
            catch
            {
                // la preferenza e' solo una comodita': ignoro errori di scrittura
            }
        }
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static bool? LoadPreference()
    {
        try
        {
            if (File.Exists(ThemeFile))
                return File.ReadAllText(ThemeFile).Trim() == "dark";
        }
        catch
        {
        }
        return null;
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
