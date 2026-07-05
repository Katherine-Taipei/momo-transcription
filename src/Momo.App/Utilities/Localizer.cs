using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Momo.App.Utilities
{
    public static class Localizer
    {
        private static Dictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase);
        private static string _currentLanguage = "zh-TW";
        private static readonly object _lock = new object();

        public static string CurrentLanguage => _currentLanguage;

        public static event Action? LanguageChanged;

        public static void SetLanguage(string lang)
        {
            lock (_lock)
            {
                _currentLanguage = lang;
                
                // Synchronize system culture info
                try
                {
                    var culture = new CultureInfo(lang);
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;
                }
                catch
                {
                    // Ignore culture initialization failures
                }

                // Load translations from JSON file
                _translations.Clear();
                var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", $"Strings.{lang}.json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        var content = File.ReadAllText(jsonPath);
                        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
                        if (parsed != null)
                        {
                            foreach (var kv in parsed)
                            {
                                _translations[kv.Key] = kv.Value;
                            }
                        }
                    }
                    catch
                    {
                        // Fallback to empty if read fails
                    }
                }

                LanguageChanged?.Invoke();
            }
        }

        public static string GetString(string key)
        {
            lock (_lock)
            {
                if (_translations.TryGetValue(key, out var val))
                {
                    return val;
                }
                return key; // Fallback to key itself if not found
            }
        }

        public static string GetStringWithFallback(string key, string fallback)
        {
            lock (_lock)
            {
                if (_translations.TryGetValue(key, out var val))
                {
                    return val;
                }
                return fallback;
            }
        }
    }
}
