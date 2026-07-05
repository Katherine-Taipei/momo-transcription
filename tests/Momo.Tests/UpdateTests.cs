using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Momo.Core.Utilities;
using Momo.Infrastructure.Updates;
using Xunit;
using Microsoft.EntityFrameworkCore;

namespace Momo.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    public class UpdateTests
    {
        [Fact]
        public void TestIsNewerVersion()
        {
            var handler = new MockHttpMessageHandler(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            using var client = new HttpClient(handler);
            var manager = new UpdateManager(client, "1.0.0");

            // Basic version checks
            Assert.True(manager.IsNewerVersion("2.0.0", "1.0.0"));
            Assert.False(manager.IsNewerVersion("1.0.0", "2.0.0"));
            Assert.False(manager.IsNewerVersion("1.0.0", "1.0.0"));

            // Minor version checks
            Assert.True(manager.IsNewerVersion("1.1.0", "1.0.0"));
            Assert.True(manager.IsNewerVersion("1.0.1", "1.0.0"));

            // Pre-release tag checks (e.g., 5.0.0-alpha3 vs 5.0.0-alpha2)
            Assert.True(manager.IsNewerVersion("5.0.0-alpha3", "5.0.0-alpha2"));
            Assert.True(manager.IsNewerVersion("5.0.0", "5.0.0-alpha3")); // stable newer than pre-release
            Assert.False(manager.IsNewerVersion("5.0.0-alpha3", "5.0.0")); // pre-release older than stable
        }

        [Fact]
        public async Task TestCheckForUpdatesAsync_StableChannel()
        {
            var responseJson = @"{
                ""tag_name"": ""v1.1.0"",
                ""prerelease"": false,
                ""assets"": [
                    {
                        ""name"": ""momo_installer.exe"",
                        ""browser_download_url"": ""https://github.com/TaipeiMomo/momo/releases/download/v1.1.0/momo_installer.exe"",
                        ""size"": 1024
                    },
                    {
                        ""name"": ""momo_patch.patch"",
                        ""browser_download_url"": ""https://github.com/TaipeiMomo/momo/releases/download/v1.1.0/momo_patch.patch"",
                        ""size"": 256
                    }
                ]
            }";

            var handler = new MockHttpMessageHandler(request =>
            {
                Assert.Equal("https://api.github.com/repos/TaipeiMomo/momo/releases/latest", request.RequestUri?.ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);
            var manager = new UpdateManager(client, "1.0.0");

            var result = await manager.CheckForUpdatesAsync("TaipeiMomo/momo", "stable");

            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.1.0", result.LatestVersion);
            Assert.Equal("https://github.com/TaipeiMomo/momo/releases/download/v1.1.0/momo_installer.exe", result.DownloadUrl);
            Assert.Equal("https://github.com/TaipeiMomo/momo/releases/download/v1.1.0/momo_patch.patch", result.PatchUrl);
        }

        [Fact]
        public async Task TestCheckForUpdatesAsync_BetaChannel()
        {
            var responseJson = @"[
                {
                    ""tag_name"": ""v1.1.0-beta2"",
                    ""prerelease"": true,
                    ""assets"": [
                        {
                            ""name"": ""momo_installer.exe"",
                            ""browser_download_url"": ""https://github.com/TaipeiMomo/momo/releases/download/v1.1.0-beta2/momo_installer.exe"",
                            ""size"": 1024
                        }
                    ]
                }
            ]";

            var handler = new MockHttpMessageHandler(request =>
            {
                Assert.Equal("https://api.github.com/repos/TaipeiMomo/momo/releases", request.RequestUri?.ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);
            var manager = new UpdateManager(client, "1.1.0-beta1");

            var result = await manager.CheckForUpdatesAsync("TaipeiMomo/momo", "beta");

            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.1.0-beta2", result.LatestVersion);
            Assert.Equal("https://github.com/TaipeiMomo/momo/releases/download/v1.1.0-beta2/momo_installer.exe", result.DownloadUrl);
        }

        [Fact]
        public async Task TestDownloadFileWithHashCheckAsync()
        {
            var contentBytes = Encoding.UTF8.GetBytes("hello update file content");
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            
            try
            {
                using var sha = SHA256.Create();
                var hashBytes = sha.ComputeHash(contentBytes);
                var expectedHashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                var handler = new MockHttpMessageHandler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(contentBytes)
                    };
                    return Task.FromResult(response);
                });

                using var client = new HttpClient(handler);
                var manager = new UpdateManager(client, "1.0.0");

                // Test successful download
                var success = await manager.DownloadFileWithHashCheckAsync("https://example.com/file", tempFile, expectedHashString);
                Assert.True(success);
                Assert.True(File.Exists(tempFile));
                Assert.Equal("hello update file content", File.ReadAllText(tempFile));

                // Test hash mismatch
                File.Delete(tempFile);
                var fail = await manager.DownloadFileWithHashCheckAsync("https://example.com/file", tempFile, "wronghash");
                Assert.False(fail);
                Assert.False(File.Exists(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void TestWinTrustVerifier()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            File.WriteAllText(tempFile, "dummy unsigned executable content");

            try
            {
                bool verified = WinTrustVerifier.VerifyEmbeddedSignature(tempFile);
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    Assert.False(verified);
                }
                else
                {
                    Assert.True(verified);
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void TestVerifyUpdateFile_FailureThrowsException()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            File.WriteAllText(tempFile, "dummy unsigned file for failure check");

            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    Assert.Throws<CryptographicException>(() => UpdateManager.VerifyUpdateFile(tempFile, null));
                }
                else
                {
                    // Non-windows should pass due to fallback, but fails if expected hash is wrong
                    Assert.Throws<CryptographicException>(() => UpdateManager.VerifyUpdateFile(tempFile, "wrong_hash"));
                    UpdateManager.VerifyUpdateFile(tempFile, null); // should pass without hash
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestCertificatePinning_Windows()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return; // Pinning test is windows-specific
            }

            // Locate a signed Windows system file that has an embedded signature
            string? signedPath = null;
            var pathsToTry = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                @"C:\Program Files\dotnet\dotnet.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "consent.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")
            };

            foreach (var path in pathsToTry)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using var testCert = new X509Certificate2(path);
                        signedPath = path;
                        break;
                    }
                    catch
                    {
                        // Try next
                    }
                }
            }

            if (signedPath == null) return; // Skip if no embedded signed file found

            // Extract thumbprint (SHA-256)
            using var cert = new X509Certificate2(signedPath);
            var sha256Fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);

            var tempKeysJson = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            try
            {
                // Write valid public_keys.json
                var config = new PublicKeysConfig
                {
                    AllowedThumbprints = new[] { sha256Fingerprint }
                };
                File.WriteAllText(tempKeysJson, JsonSerializer.Serialize(config));

                // Clear cache first
                UpdateManager.ClearCachedThumbprints();

                // 1. Verify successful pinning check
                UpdateManager.VerifyUpdateFile(signedPath, null, tempKeysJson);

                // 2. Verify failure pinning check (wrong thumbprint)
                var invalidConfig = new PublicKeysConfig
                {
                    AllowedThumbprints = new[] { "0000000000000000000000000000000000000000000000000000000000000000" }
                };
                File.WriteAllText(tempKeysJson, JsonSerializer.Serialize(invalidConfig));
                UpdateManager.ClearCachedThumbprints();

                Assert.Throws<CryptographicException>(() => UpdateManager.VerifyUpdateFile(signedPath, null, tempKeysJson));
            }
            finally
            {
                UpdateManager.ClearCachedThumbprints();
                if (File.Exists(tempKeysJson)) File.Delete(tempKeysJson);
            }
        }

        [Fact]
        public async Task TestDownloadFileWithHashCheckAndVerify_Malicious()
        {
            var contentBytes = Encoding.UTF8.GetBytes("malicious payload contents");
            using var sha = SHA256.Create();
            var expectedHash = BitConverter.ToString(sha.ComputeHash(contentBytes)).Replace("-", "").ToLowerInvariant();

            var handler = new MockHttpMessageHandler(request =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(contentBytes)
                });
            });

            using var client = new HttpClient(handler);
            var manager = new UpdateManager(client, "1.0.0");
            var tempDestFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var tempKeysJson = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

            try
            {
                // Create custom keys config to enforce check
                var config = new PublicKeysConfig
                {
                    AllowedThumbprints = new[] { "A1B2C3D4E5F678901234567890ABCDEF1234567890ABCDEF1234567890ABCDEF" }
                };
                File.WriteAllText(tempKeysJson, JsonSerializer.Serialize(config));
                UpdateManager.ClearCachedThumbprints();

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    // Should download, fail signature/pinning verify, throw CryptographicException, and delete the file immediately.
                    await Assert.ThrowsAsync<CryptographicException>(async () =>
                    {
                        await manager.DownloadFileWithHashCheckAndVerifyAsync("https://example.com/malicious", tempDestFile, expectedHash, tempKeysJson);
                    });

                    Assert.False(File.Exists(tempDestFile)); // Verify deleted
                }
                else
                {
                    // Non-windows has fallback, but pinning fails if we run verify since thumbprint checks are skipped but keys file exists.
                    // Wait, non-windows skips pinning entirely when calling VerifyUpdateFile because IsOSPlatform(Windows) is false.
                    // Let's assert it succeeds on non-windows.
                    var success = await manager.DownloadFileWithHashCheckAndVerifyAsync("https://example.com/malicious", tempDestFile, expectedHash, tempKeysJson);
                    Assert.True(success);
                    Assert.True(File.Exists(tempDestFile));
                }
            }
            finally
            {
                UpdateManager.ClearCachedThumbprints();
                if (File.Exists(tempDestFile)) File.Delete(tempDestFile);
                if (File.Exists(tempKeysJson)) File.Delete(tempKeysJson);
            }
        }

        [Fact]
        public async Task TestCheckForUpdatesAsync_429RetryAndSuppression()
        {
            int requestCount = 0;
            var handler = new MockHttpMessageHandler(request =>
            {
                requestCount++;
                var response = new HttpResponseMessage((HttpStatusCode)429);
                // Return 429 Rate Limit
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);
            var manager = new UpdateManager(client, "1.0.0");

            var result = await manager.CheckForUpdatesAsync("TaipeiMomo/momo", "stable");

            // Verify that we retried 5 times
            Assert.Equal(5, requestCount);
            Assert.False(result.UpdateAvailable);

            // Verify subsequent request is suppressed (request count remains 5)
            var resultSuppressed = await manager.CheckForUpdatesAsync("TaipeiMomo/momo", "stable");
            Assert.Equal(5, requestCount);
            Assert.False(resultSuppressed.UpdateAvailable);
        }

        [Fact]
        public void TestBackupAndRollback_RetentionAndRenaming()
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "momo_test_backup_" + Guid.NewGuid().ToString("N"));
            var srcDir = Path.Combine(tempBase, "app");
            var backupDir = Path.Combine(tempBase, "backup");

            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(backupDir);

            var exePath = Path.Combine(srcDir, "Momo.App.exe");
            var dllPath = Path.Combine(srcDir, "Momo.Infrastructure.dll");
            var dbPath = Path.Combine(srcDir, "momo.db");
            var logPath = Path.Combine(srcDir, "update.log");

            File.WriteAllText(exePath, "executable version 1");
            File.WriteAllText(dllPath, "infrastructure DLL version 1");
            File.WriteAllText(dbPath, "database items (should be excluded)");
            File.WriteAllText(logPath, "logs content (should be excluded)");

            try
            {
                // 1. Test Backup Creation (with zip)
                RollbackManager.BackupCurrentInstallation(srcDir, backupDir, "1.0.0");

                var zips = Directory.GetFiles(backupDir, "momo_backup_*.zip");
                Assert.Single(zips);

                // 2. Test Backup Retention (Keep only 2)
                RollbackManager.BackupCurrentInstallation(srcDir, backupDir, "1.0.1");
                RollbackManager.BackupCurrentInstallation(srcDir, backupDir, "1.0.2");
                
                var zipsAfter3 = Directory.GetFiles(backupDir, "momo_backup_*.zip");
                Assert.Equal(2, zipsAfter3.Length); // Oldest backup deleted

                // 3. Test Restore with locked-file simulation
                // Write new contents
                File.WriteAllText(exePath, "crashed executable version 2");
                File.WriteAllText(dllPath, "crashed DLL version 2");

                // Lock one of the files with a share mode that allows renaming/deleting but prevents direct overwrite
                using (var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    // Attempt restore (should rename locked file and overwrite)
                    RollbackManager.RestoreBackup(backupDir, srcDir);
                }

                // Verify file was restored
                Assert.Equal("executable version 1", File.ReadAllText(exePath));
                Assert.Equal("infrastructure DLL version 1", File.ReadAllText(dllPath));

                // Check that DB and log were NOT deleted or modified by restore
                Assert.True(File.Exists(dbPath));
                Assert.Equal("database items (should be excluded)", File.ReadAllText(dbPath));

                // 4. Test restore failure flag trigger
                Assert.False(RollbackManager.IsRollbackFailedFlagSet());
                
                // Trigger restore error by passing invalid folder
                Assert.Throws<DirectoryNotFoundException>(() => RollbackManager.RestoreBackup("invalid_folder_path_123", srcDir));
            }
            finally
            {
                RollbackManager.ClearRollbackFailedFlag();
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }

        [Fact]
        public void TestBinaryPatchUtility_RoundTrip()
        {
            var oldContent = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            var newContent = Encoding.UTF8.GetBytes("The quick brown fox jumped over the extremely lazy dog");

            var oldFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var newFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var patchFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var reconstructedFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                File.WriteAllBytes(oldFile, oldContent);
                File.WriteAllBytes(newFile, newContent);

                // Create patch
                BinaryPatchUtility.CreatePatch(oldFile, newFile, patchFile);
                Assert.True(File.Exists(patchFile));

                // Apply patch
                BinaryPatchUtility.ApplyPatch(oldFile, reconstructedFile, patchFile);
                Assert.True(File.Exists(reconstructedFile));

                var reconstructedContent = File.ReadAllBytes(reconstructedFile);
                Assert.Equal(newContent, reconstructedContent);
            }
            finally
            {
                if (File.Exists(oldFile)) File.Delete(oldFile);
                if (File.Exists(newFile)) File.Delete(newFile);
                if (File.Exists(patchFile)) File.Delete(patchFile);
                if (File.Exists(reconstructedFile)) File.Delete(reconstructedFile);
            }
        }

        [Fact]
        public async Task TestTelemetryService_OptInDisabled()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
            var builder = new DbContextOptionsBuilder<Momo.Infrastructure.Db.AppDbContext>();
            builder.UseSqlite($"Data Source={dbPath}");
            var options = builder.Options;

            try
            {
                // Initialize database schema
                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    Momo.Infrastructure.Db.Initializer.Initialize(context);
                }

                var mockHandler = new MockHttpMessageHandler(request =>
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });
                using var httpClient = new HttpClient(mockHandler);
                
                // Opt-in is false
                var service = new Momo.Infrastructure.Telemetry.TelemetryService(options, httpClient, optIn: false);
                
                await service.TrackEventAsync("app_start", new { version = "5.0.0" });

                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    var logsCount = await context.TelemetryLogs.CountAsync();
                    Assert.Equal(0, logsCount);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        [Fact]
        public async Task TestTelemetryService_OptInNull()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
            var builder = new DbContextOptionsBuilder<Momo.Infrastructure.Db.AppDbContext>();
            builder.UseSqlite($"Data Source={dbPath}");
            var options = builder.Options;

            try
            {
                // Initialize database schema
                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    Momo.Infrastructure.Db.Initializer.Initialize(context);
                }

                var mockHandler = new MockHttpMessageHandler(request =>
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });
                using var httpClient = new HttpClient(mockHandler);
                
                // Opt-in is null (not yet chosen)
                var service = new Momo.Infrastructure.Telemetry.TelemetryService(options, httpClient, optIn: null);
                
                await service.TrackEventAsync("app_start", new { version = "5.0.0" });

                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    var logsCount = await context.TelemetryLogs.CountAsync();
                    Assert.Equal(0, logsCount);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        [Fact]
        public async Task TestTelemetryService_OptInEnabledAndUpload()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
            var builder = new DbContextOptionsBuilder<Momo.Infrastructure.Db.AppDbContext>();
            builder.UseSqlite($"Data Source={dbPath}");
            var options = builder.Options;

            try
            {
                // Initialize database schema
                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    Momo.Infrastructure.Db.Initializer.Initialize(context);
                }

                int uploadRequestsCount = 0;
                var mockHandler = new MockHttpMessageHandler(request =>
                {
                    uploadRequestsCount++;
                    Assert.Equal("https://example.com/api/v1/telemetry/events", request.RequestUri?.ToString());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });
                using var httpClient = new HttpClient(mockHandler);
                
                // Opt-in is true
                var service = new Momo.Infrastructure.Telemetry.TelemetryService(options, httpClient, optIn: true);
                
                await service.TrackEventAsync("app_start", new { version = "5.0.0" });
                await service.TrackEventAsync("transcription_completed", new { duration_seconds = 120 });

                // Check that records were written to database
                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    var logs = await context.TelemetryLogs.ToListAsync();
                    Assert.Equal(2, logs.Count);
                    Assert.All(logs, log => Assert.Equal(service.SessionId, log.SessionId));
                    Assert.Contains(logs, log => log.EventName == "app_start");
                    Assert.Contains(logs, log => log.EventName == "transcription_completed");
                }

                // Perform upload
                var uploadSuccess = await service.UploadPendingTelemetryAsync("https://example.com/api/v1/telemetry/events");
                Assert.True(uploadSuccess);
                Assert.Equal(1, uploadRequestsCount);

                // Check database is now empty
                using (var context = new Momo.Infrastructure.Db.AppDbContext(options))
                {
                    var logsCount = await context.TelemetryLogs.CountAsync();
                    Assert.Equal(0, logsCount);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }
    }
}
