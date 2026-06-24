using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Core.Interfaces;
using Momo.Infrastructure.Db;

namespace Momo.Infrastructure.Queue;

public class QueueService : IQueueService
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public QueueService(DbContextOptions<AppDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
    }

    private AppDbContext CreateContext() => new AppDbContext(_dbOptions);

    public async Task EnqueueJobAsync(string projectId, string mediaFileId, int priority = 5, bool diarization = false, bool alignment = false, string? selectedGlossaries = null, string? selectedRole = null, string? selectedTemplate = null)
    {
        using var context = CreateContext();
        var job = new Job
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            MediaFileId = mediaFileId,
            Status = "PENDING",
            Priority = priority,
            Diarization = diarization,
            Alignment = alignment,
            SelectedGlossaries = selectedGlossaries,
            SelectedRole = selectedRole,
            SelectedTemplate = selectedTemplate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Jobs.Add(job);
        await context.SaveChangesAsync();
    }

    public async Task<Job?> GetNextJobAsync()
    {
        using var context = CreateContext();
        
        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var nextJob = await context.Jobs
                .Include(j => j.MediaFile)
                .Where(j => j.Status == "PENDING" || j.Status == "RETRY")
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.CreatedAt)
                .FirstOrDefaultAsync();

            if (nextJob != null)
            {
                nextJob.Status = "RUNNING";
                nextJob.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return nextJob;
            }

            await transaction.RollbackAsync();
            return null;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task UpdateJobStatusAsync(string jobId, string status, string? errorMessage = null)
    {
        using var context = CreateContext();
        var job = await context.Jobs.FindAsync(jobId);
        if (job != null)
        {
            job.Status = status;
            job.ErrorMessage = errorMessage;
            job.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    public async Task IncrementRetryCountAsync(string jobId, string errorMessage)
    {
        using var context = CreateContext();
        var job = await context.Jobs.FindAsync(jobId);
        if (job != null)
        {
            job.RetryCount += 1;
            job.ErrorMessage = errorMessage;
            job.UpdatedAt = DateTime.UtcNow;

            if (job.RetryCount < job.MaxRetries)
            {
                job.Status = "RETRY";
            }
            else
            {
                job.Status = "FAILED";
            }

            await context.SaveChangesAsync();
        }
    }

    public async Task PauseJobAsync(string jobId)
    {
        await UpdateJobStatusAsync(jobId, "PAUSED");
    }

    public async Task ResumeJobAsync(string jobId)
    {
        await UpdateJobStatusAsync(jobId, "PENDING");
    }

    public async Task CancelJobAsync(string jobId)
    {
        await UpdateJobStatusAsync(jobId, "CANCELLED");
    }
}
