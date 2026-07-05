using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Momo.Infrastructure.Updates;

namespace Momo.App.Updates
{
    public class UpdateBackgroundWorker
    {
        private readonly UpdateManager _updateManager;
        private readonly string _repository;
        private readonly Func<AppSettings> _loadSettings;
        private readonly Action<AppSettings> _saveSettings;
        private readonly Action<UpdateCheckResult> _onUpdateFound;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _runTask;

        public UpdateBackgroundWorker(
            UpdateManager updateManager,
            string repository,
            Func<AppSettings> loadSettings,
            Action<AppSettings> saveSettings,
            Action<UpdateCheckResult> onUpdateFound)
        {
            _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _loadSettings = loadSettings ?? throw new ArgumentNullException(nameof(loadSettings));
            _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
            _onUpdateFound = onUpdateFound ?? throw new ArgumentNullException(nameof(onUpdateFound));
        }

        public void Start()
        {
            _runTask = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private async Task LoopAsync(CancellationToken token)
        {
            // Initial startup delay to let the app initialize fully
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (TaskCanceledException) { return; }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = _loadSettings();
                    if (settings.UpdateFrequency != UpdateFrequency.Off)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (now >= settings.NextUpdatePromptAt && now >= settings.UpdateCheckSuppressUntil)
                        {
                            var interval = settings.UpdateFrequency == UpdateFrequency.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
                            if (now - settings.LastUpdateCheck >= interval)
                            {
                                UpdateManager.LogUpdateMessage("Background update check trigger started.");
                                var result = await _updateManager.CheckForUpdatesAsync(_repository, settings.UpdateChannel);
                                
                                // Reload settings to prevent overwriting other modifications
                                settings = _loadSettings();
                                settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                                _saveSettings(settings);

                                if (result.UpdateAvailable)
                                {
                                    UpdateManager.LogUpdateMessage($"New update version {result.LatestVersion} found by background worker.");
                                    _onUpdateFound(result);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UpdateManager.LogUpdateMessage($"Error in background update worker: {ex.Message}");
                }

                // Sleep for 1 hour before next periodic check
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }
}
