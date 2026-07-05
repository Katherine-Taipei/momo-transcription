using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Momo.Infrastructure.Updates
{
    public static class WinTrustVerifier
    {
        private static readonly Guid WVTPolicyGUID = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
            ref WinTrustData pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string FilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;

            public WinTrustFileInfo(string filePath)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                FilePath = filePath;
                hFile = IntPtr.Zero;
                pgKnownSubject = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SIPClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public string? URLReference;
            public uint ProviderFlags;
            public uint UIContext;
            public IntPtr SignatureSettings;

            public WinTrustData(IntPtr fileInfoPtr)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
                PolicyCallbackData = IntPtr.Zero;
                SIPClientData = IntPtr.Zero;
                UIChoice = 2; // WTD_UI_NONE
                RevocationChecks = 0; // WTD_REVOKE_NONE
                UnionChoice = 1; // WTD_CHOICE_FILE
                FileInfo = fileInfoPtr;
                StateAction = 1; // WTD_STATEACTION_VERIFY
                StateData = IntPtr.Zero;
                URLReference = null;
                ProviderFlags = 0x00000040; // WTD_REVOCATION_CHECK_NONE
                UIContext = 0;
                SignatureSettings = IntPtr.Zero;
            }
        }

        public static bool VerifyEmbeddedSignature(string fileName)
        {
            // Fallback for non-Windows platforms (CI environment / Linux)
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return true;
            }

            if (!File.Exists(fileName)) return false;

            var fileInfo = new WinTrustFileInfo(fileName);
            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var trustData = new WinTrustData(fileInfoPtr);
            
            try
            {
                int result = WinVerifyTrust(IntPtr.Zero, WVTPolicyGUID, ref trustData);
                
                // Free state action data
                trustData.StateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, WVTPolicyGUID, ref trustData);

                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }
    }

    public class PublicKeysConfig
    {
        [JsonPropertyName("AllowedThumbprints")]
        public string[] AllowedThumbprints { get; set; } = Array.Empty<string>();
    }

    public class GithubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public GithubReleaseAsset[] Assets { get; set; } = Array.Empty<GithubReleaseAsset>();
    }

    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string PatchUrl { get; set; } = string.Empty;
        public string ExpectedHash { get; set; } = string.Empty;
        public string ExpectedPatchHash { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
    }

    public class UpdateManager
    {
        private readonly HttpClient _httpClient;
        private readonly string _currentVersion;

        private static string[]? _cachedThumbprints = null;
        private static readonly object _cacheLock = new object();
        private static DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

        public UpdateManager(HttpClient httpClient, string currentVersion)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _currentVersion = currentVersion;
            
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Momo-UpdateManager");
            }
        }

        public static string[] LoadAllowedThumbprints(string jsonPath)
        {
            lock (_cacheLock)
            {
                if (_cachedThumbprints != null)
                {
                    return _cachedThumbprints;
                }

                if (!File.Exists(jsonPath))
                {
                    return Array.Empty<string>();
                }

                try
                {
                    var json = File.ReadAllText(jsonPath);
                    var config = JsonSerializer.Deserialize<PublicKeysConfig>(json);
                    if (config?.AllowedThumbprints != null)
                    {
                        _cachedThumbprints = config.AllowedThumbprints;
                        return _cachedThumbprints;
                    }
                }
                catch
                {
                    // Ignore reading failures
                }
                return Array.Empty<string>();
            }
        }

        public static void ClearCachedThumbprints()
        {
            lock (_cacheLock)
            {
                _cachedThumbprints = null;
            }
        }

        public static void LogUpdateMessage(string message)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momo", "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "update.log");

                // Daily rotation check
                if (File.Exists(logPath))
                {
                    var lastWrite = File.GetLastWriteTime(logPath);
                    if (lastWrite.Date != DateTime.Today)
                    {
                        var rotatedPath = Path.Combine(logDir, $"update_{lastWrite:yyyy-MM-dd}.log");
                        if (!File.Exists(rotatedPath))
                        {
                            try
                            {
                                File.Move(logPath, rotatedPath);
                            }
                            catch
                            {
                                // If move fails, we will just append to existing
                            }
                        }
                    }
                }

                var logLine = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, logLine);

                // Retention cleanup (delete logs older than 30 days)
                var files = Directory.GetFiles(logDir, "update_*.log");
                var cutOff = DateTime.Today.AddDays(-30);
                foreach (var file in files)
                {
                    try
                    {
                        var fileDate = File.GetLastWriteTime(file);
                        if (fileDate < cutOff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Ignore deletion failures
                    }
                }
            }
            catch
            {
                // Ignore logging failures
            }
        }

        public static void VerifyUpdateFile(string filePath, string? expectedHash = null, string? publicKeysJsonPath = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var actualHash = CalculateSha256(filePath);
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CryptographicException($"Hash validation failed for file {filePath}. Expected {expectedHash}, got {actualHash}.");
                    }
                    LogUpdateMessage($"[INFO] Signature verification skipped on non-Windows: {Path.GetFileName(filePath)} (SHA-256 hash verified)");
                }
                else
                {
                    LogUpdateMessage($"[INFO] Signature verification skipped on non-Windows: {Path.GetFileName(filePath)} (No hash provided)");
                }
                return;
            }

            // 1. Check WinVerifyTrust
            bool signatureOk = WinTrustVerifier.VerifyEmbeddedSignature(filePath);
            if (!signatureOk)
            {
                throw new CryptographicException($"Signature verification failed via WinVerifyTrust for file: {filePath}");
            }

            // 2. Check Certificate Pinning (SHA-256 fingerprint check)
            var keysPath = publicKeysJsonPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public_keys.json");
            if (File.Exists(keysPath))
            {
                var allowed = LoadAllowedThumbprints(keysPath);
                if (allowed.Length > 0)
                {
                    X509Certificate2? cert = null;
                    try
                    {
                        cert = new X509Certificate2(filePath);
                    }
                    catch (Exception ex)
                    {
                        throw new CryptographicException($"Failed to extract certificate from signed file: {filePath}", ex);
                    }

                    using (cert)
                    {
                        var sha256Fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
                        bool matches = false;
                        foreach (var pin in allowed)
                        {
                            if (sha256Fingerprint.Equals(pin, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                                break;
                            }
                        }

                        if (!matches)
                        {
                            throw new CryptographicException($"Certificate pinning check failed. The certificate SHA-256 fingerprint '{sha256Fingerprint}' is not trusted for file: {filePath}");
                        }
                    }
                }
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string repository, string channel)
        {
            var result = new UpdateCheckResult();
            if (DateTimeOffset.UtcNow < _suppressUntil)
            {
                LogUpdateMessage("Update check skipped: suppressed due to consecutive HTTP 429 errors.");
                return result;
            }

            try
            {
                var url = $"https://api.github.com/repos/{repository}/releases/latest";
                if (channel.Equals("beta", StringComparison.OrdinalIgnoreCase))
                {
                    url = $"https://api.github.com/repos/{repository}/releases";
                }

                HttpResponseMessage? response = null;
                int maxRetries = 5;
                int delayMs = 1000;
                int consecutive429s = 0;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        response = await _httpClient.GetAsync(url);
                    }
                    catch (Exception ex)
                    {
                        LogUpdateMessage($"HTTP request failed on attempt {attempt}: {ex.Message}");
                        if (attempt == maxRetries) throw;
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        break;
                    }
                    else if (response.StatusCode == (HttpStatusCode)429)
                    {
                        consecutive429s++;
                        LogUpdateMessage($"Received HTTP 429 on attempt {attempt}.");
                        if (consecutive429s >= maxRetries)
                        {
                            _suppressUntil = DateTimeOffset.UtcNow.AddHours(24);
                            LogUpdateMessage("Consecutive 429 failures reached 5. Suppressing update checks for 24 hours.");
                            return result;
                        }

                        // Check Retry-After header
                        if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
                        {
                            await Task.Delay(response.Headers.RetryAfter.Delta.Value);
                        }
                        else
                        {
                            await Task.Delay(delayMs);
                            delayMs *= 2;
                        }
                    }
                    else
                    {
                        LogUpdateMessage($"HTTP request failed with status code {response.StatusCode}");
                        return result;
                    }
                }

                if (response == null || response.StatusCode != HttpStatusCode.OK)
                {
                    return result;
                }

                var content = await response.Content.ReadAsStringAsync();
                GithubRelease? latestRelease = null;

                if (channel.Equals("beta", StringComparison.OrdinalIgnoreCase))
                {
                    var releases = JsonSerializer.Deserialize<GithubRelease[]>(content);
                    if (releases != null && releases.Length > 0)
                    {
                        latestRelease = releases[0];
                    }
                }
                else
                {
                    latestRelease = JsonSerializer.Deserialize<GithubRelease>(content);
                }

                if (latestRelease == null) return result;

                var remoteTag = latestRelease.TagName.TrimStart('v');
                if (IsNewerVersion(remoteTag, _currentVersion))
                {
                    result.UpdateAvailable = true;
                    result.LatestVersion = remoteTag;
                    result.Changelog = latestRelease.Body;

                    foreach (var asset in latestRelease.Assets)
                    {
                        if (asset.Name.EndsWith(".exe") && !asset.Name.Contains("patch"))
                        {
                            result.DownloadUrl = asset.BrowserDownloadUrl;
                        }
                        else if (asset.Name.EndsWith(".patch"))
                        {
                            result.PatchUrl = asset.BrowserDownloadUrl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogUpdateMessage($"Error checking for updates: {ex.Message}");
            }
            return result;
        }

        public bool IsNewerVersion(string remote, string local)
        {
            // Parse pre-release labels (e.g. 1.2.3-beta vs 1.2.3)
            var remoteCore = remote.Split('-')[0];
            var localCore = local.Split('-')[0];

            if (Version.TryParse(remoteCore, out var remoteVer) &&
                Version.TryParse(localCore, out var localVer))
            {
                if (remoteVer > localVer) return true;
                if (remoteVer < localVer) return false;
            }

            // If versions are equal, compare labels. Version with label is older than same version without label.
            // E.g. "1.2.3-beta" (local) < "1.2.3" (remote)
            bool remoteHasLabel = remote.Contains("-");
            bool localHasLabel = local.Contains("-");

            if (!remoteHasLabel && localHasLabel) return true;
            if (remoteHasLabel && !localHasLabel) return false;

            return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
        }

        public async Task<bool> DownloadFileWithHashCheckAsync(string url, string destinationPath, string expectedHash)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return false;

                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var actualHash = CalculateSha256(destinationPath);
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(destinationPath);
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> DownloadFileWithHashCheckAndVerifyAsync(string url, string destinationPath, string expectedHash, string? publicKeysJsonPath = null)
        {
            bool downloaded = await DownloadFileWithHashCheckAsync(url, destinationPath, expectedHash);
            if (!downloaded) return false;

            try
            {
                VerifyUpdateFile(destinationPath, expectedHash, publicKeysJsonPath);
                return true;
            }
            catch (Exception ex)
            {
                LogUpdateMessage($"Verification failed for downloaded file {destinationPath}: {ex.Message}");
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                }
                catch
                {
                    // Ignore delete errors
                }
                throw;
            }
        }

        public static string CalculateSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
