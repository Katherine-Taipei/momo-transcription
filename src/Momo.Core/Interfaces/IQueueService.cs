using System.Threading.Tasks;
using Momo.Core.Entities;

namespace Momo.Core.Interfaces;

public interface IQueueService
{
    Task EnqueueJobAsync(string projectId, string mediaFileId, int priority = 5, bool diarization = false, bool alignment = false, string? selectedGlossaries = null, string? selectedRole = null, string? selectedTemplate = null);
    Task<Job?> GetNextJobAsync();
    Task UpdateJobStatusAsync(string jobId, string status, string? errorMessage = null);
    Task IncrementRetryCountAsync(string jobId, string errorMessage);
    Task PauseJobAsync(string jobId);
    Task ResumeJobAsync(string jobId);
    Task CancelJobAsync(string jobId);
}
