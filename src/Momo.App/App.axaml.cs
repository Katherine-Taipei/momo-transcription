using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Momo.App.ViewModels;
using Momo.App.Views;
using Momo.Infrastructure.Updates;

namespace Momo.App
{
    public partial class App : Application
    {
        internal static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);
        }

        private void ShowMessageBox(string message, string title, uint type = 0x10)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    NativeMethods.MessageBox(IntPtr.Zero, message, title, type);
                    return;
                }
                catch {}
            }
            Console.Error.WriteLine($"[{title}] {message}");
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            var localDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momo");
            var backupDir = Path.Combine(localDataDir, "backup");
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Check if a previous rollback attempt failed
            if (RollbackManager.IsRollbackFailedFlagSet())
            {
                RollbackManager.ClearRollbackFailedFlag();
                ShowMessageBox("系統重置失敗。\n請至官網重新下載並安裝 Momo Transcription Platform。", "系統錯誤", 0x10); // MB_ICONERROR
                Environment.Exit(1);
            }

            // 2. Load settings and track consecutive startup failures
            var settings = AppSettingsManager.LoadSettings();
            settings.ConsecutiveFailures++;
            AppSettingsManager.SaveSettings(settings);

            // 3. Trigger automatic rollback if failures reach threshold
            if (settings.ConsecutiveFailures >= 3)
            {
                UpdateManager.LogUpdateMessage($"Consecutive failures reached {settings.ConsecutiveFailures}. Initiating automatic rollback.");
                try
                {
                    RollbackManager.RestoreBackup(backupDir, appDir);
                    
                    settings.RolledBackFromVersion = settings.LastGoodVersion;
                    settings.ConsecutiveFailures = 0;
                    AppSettingsManager.SaveSettings(settings);

                    UpdateManager.LogUpdateMessage("Rollback successful. Restarting application...");
                    RollbackManager.RestartApplication();
                    return;
                }
                catch (Exception ex)
                {
                    UpdateManager.LogUpdateMessage($"Rollback failed during execution: {ex.Message}");
                    RollbackManager.SetRollbackFailedFlag();
                    ShowMessageBox("系統重置失敗。\n請至官網重新下載並安裝 Momo Transcription Platform。", "系統錯誤", 0x10);
                    Environment.Exit(1);
                }
            }

            AppSettingsManager.ApplyTheme(settings.Theme);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel()
                };

                // Upon successful window opening, reset failure counters and alert user of any rollback
                desktop.MainWindow.Opened += (sender, args) =>
                {
                    var settings = AppSettingsManager.LoadSettings();
                    settings.ConsecutiveFailures = 0;
                    
                    if (!string.IsNullOrEmpty(settings.RolledBackFromVersion))
                    {
                        var msg = $"已還原到安全版本 {settings.LastGoodVersion}；請回報錯誤或重新嘗試更新。";
                        ShowMessageBox(msg, "系統還原", 0x40); // MB_ICONINFORMATION
                        settings.RolledBackFromVersion = string.Empty;
                    }
                    
                    AppSettingsManager.SaveSettings(settings);
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
