using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;

namespace Momo.Infrastructure.Telemetry
{
    public class TelemetryService
    {
        private readonly DbContextOptions<AppDbContext> _dbOptions;
        private readonly HttpClient _httpClient;
        private readonly string _sessionId;
        private bool? _optIn;

        public string SessionId => _sessionId;

        public TelemetryService(DbContextOptions<AppDbContext> dbOptions, HttpClient httpClient, bool? optIn)
        {
            _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _sessionId = Guid.NewGuid().ToString();
            _optIn = optIn;
        }

        public void SetOptIn(bool optIn)
        {
            _optIn = optIn;
        }

        public async Task TrackEventAsync(string eventName, object payload)
        {
            // If opt-in is not true (disabled or unconfigured), do not record any telemetry
            if (_optIn != true)
            {
                return;
            }

            try
            {
                var payloadJson = JsonSerializer.Serialize(payload);
                using (var context = new AppDbContext(_dbOptions))
                {
                    var log = new TelemetryLog
                    {
                        SessionId = _sessionId,
                        EventName = eventName,
                        PayloadJson = payloadJson,
                        Timestamp = DateTime.UtcNow
                    };
                    context.TelemetryLogs.Add(log);
                    await context.SaveChangesAsync();
                }
            }
            catch
            {
                // Silence telemetry logging issues to avoid impacting main application
            }
        }

        public async Task<bool> UploadPendingTelemetryAsync(string endpointUrl)
        {
            // If opt-in is not true, we don't upload (and shouldn't have any anyway)
            if (_optIn != true)
            {
                return false;
            }

            try
            {
                using (var context = new AppDbContext(_dbOptions))
                {
                    var logs = await context.TelemetryLogs.OrderBy(l => l.Timestamp).Take(100).ToListAsync();
                    if (!logs.Any()) return true;

                    var payload = logs.Select(l => new
                    {
                        session_id = l.SessionId,
                        event_name = l.EventName,
                        payload = JsonSerializer.Deserialize<JsonElement>(l.PayloadJson),
                        timestamp = l.Timestamp.ToString("o")
                    }).ToList();

                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(endpointUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        context.TelemetryLogs.RemoveRange(logs);
                        await context.SaveChangesAsync();
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore upload failures
            }
            return false;
        }
    }
}
