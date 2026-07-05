using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Momo.Infrastructure.Updates
{
    public static class RollbackManager
    {
        public static void BackupCurrentInstallation(string appDir, string backupDir, string currentVersion)
        {
            Directory.CreateDirectory(backupDir);
            
            // Create a temporary staging directory inside backupDir to copy files before zipping
            var tempStaging = Path.Combine(backupDir, "staging_" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectoryFiltered(appDir, tempStaging);
                
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var zipPath = Path.Combine(backupDir, $"momo_backup_{currentVersion}_{timestamp}.zip");
                
                ZipFile.CreateFromDirectory(tempStaging, zipPath);
                
                UpdateManager.LogUpdateMessage($"Backup created successfully: {zipPath}");
                
                CleanupOldBackups(backupDir);
            }
            catch (Exception ex)
            {
                UpdateManager.LogUpdateMessage($"Error creating backup: {ex.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempStaging))
                {
                    try
                    {
                        Directory.Delete(tempStaging, true);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }

        private static void CopyDirectoryFiltered(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".db" || ext == ".log") continue; // Exclude DB and logs
                var name = Path.GetFileName(file).ToLowerInvariant();
                if (name.Contains("rollback_failed") || name.Contains("!rollback-failed")) continue;
                
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            }
            foreach (var sub in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(sub).ToLowerInvariant();
                // Exclude working/runtime folders
                if (name == "backup" || name == "updates" || name == "logs" || name == "temp" || name == "bin" || name == "obj" || name == ".git") continue;
                
                CopyDirectoryFiltered(sub, Path.Combine(dest, Path.GetFileName(sub)));
            }
        }

        private static void CleanupOldBackups(string backupDir)
        {
            try
            {
                var files = Directory.GetFiles(backupDir, "momo_backup_*.zip");
                if (files.Length > 2)
                {
                    var sorted = new List<string>(files);
                    // Sort descending by write time
                    sorted.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                    for (int i = 2; i < sorted.Count; i++)
                    {
                        File.Delete(sorted[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateManager.LogUpdateMessage($"Error cleaning up old backups: {ex.Message}");
            }
        }

        public static void RestoreBackup(string backupDir, string appDir)
        {
            if (!Directory.Exists(backupDir))
            {
                throw new DirectoryNotFoundException($"Backup directory not found: {backupDir}");
            }

            var files = Directory.GetFiles(backupDir, "momo_backup_*.zip");
            if (files.Length == 0)
            {
                throw new FileNotFoundException("No backup zip files found to restore.");
            }

            var sorted = new List<string>(files);
            // Sort descending to get the newest backup
            sorted.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            var newestBackup = sorted[0];

            UpdateManager.LogUpdateMessage($"Attempting to restore from backup: {newestBackup}");

            try
            {
                using (var archive = ZipFile.OpenRead(newestBackup))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        var destPath = Path.Combine(appDir, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        try
                        {
                            entry.ExtractToFile(destPath, true);
                        }
                        catch (IOException)
                        {
                            if (File.Exists(destPath))
                            {
                                var tempFile = destPath + "." + Guid.NewGuid().ToString("N") + ".old";
                                try
                                {
                                    File.Move(destPath, tempFile);
                                    entry.ExtractToFile(destPath, true);
                                }
                                catch
                                {
                                    throw;
                                }
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
                UpdateManager.LogUpdateMessage("Rollback restoration completed successfully.");
            }
            catch (Exception ex)
            {
                UpdateManager.LogUpdateMessage($"Rollback restoration failed: {ex.Message}");
                SetRollbackFailedFlag();
                throw;
            }
        }

        public static void SetRollbackFailedFlag()
        {
            try
            {
                var flagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momo", "rollback_failed.txt");
                File.WriteAllText(flagPath, "Rollback failed at: " + DateTime.UtcNow.ToString("o"));
            }
            catch {}
        }

        public static bool IsRollbackFailedFlagSet()
        {
            try
            {
                var flagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momo", "rollback_failed.txt");
                return File.Exists(flagPath);
            }
            catch
            {
                return false;
            }
        }

        public static void ClearRollbackFailedFlag()
        {
            try
            {
                var flagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momo", "rollback_failed.txt");
                if (File.Exists(flagPath))
                {
                    File.Delete(flagPath);
                }
            }
            catch {}
        }

        public static void RestartApplication()
        {
            var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Momo.App.exe");
            if (File.Exists(exePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            Environment.Exit(0);
        }
    }
}
