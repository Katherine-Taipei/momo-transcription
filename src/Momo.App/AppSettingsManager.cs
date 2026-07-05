using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;

namespace Momo.App;

public class AppSettings
{
    [JsonPropertyName("Theme")]
    public string Theme { get; set; } = "Auto";

    [JsonPropertyName("BrandLogoPath")]
    public string BrandLogoPath { get; set; } = "Assets/logo_placeholder.png";
}

public static class AppSettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Momo",
        "appsettings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Momo");
            Directory.CreateDirectory(appDataDir);

            if (!File.Exists(SettingsPath))
            {
                var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(templatePath))
                 {
                     File.Copy(templatePath, SettingsPath, true);
                 }
            }

            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // Resolve relative logo path to base directory if needed
                    if (!string.IsNullOrEmpty(settings.BrandLogoPath) && !Path.IsPathRooted(settings.BrandLogoPath))
                    {
                        var resolvedLogo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.BrandLogoPath);
                        if (File.Exists(resolvedLogo))
                        {
                            settings.BrandLogoPath = resolvedLogo;
                        }
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            var appDataDir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(appDataDir))
            {
                Directory.CreateDirectory(appDataDir);
            }
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore write failures in read-only environments
        }
    }

    public static void ApplyTheme(string themeName)
    {
        if (Application.Current == null) return;

        if (themeName.Equals("Dark", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        }
        else if (themeName.Equals("Light", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Default; // System default / Auto
        }
    }
}
