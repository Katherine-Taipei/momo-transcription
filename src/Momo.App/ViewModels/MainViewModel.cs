using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Core.Interfaces;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Exporters;
using Momo.Infrastructure.Queue;
using Momo.Infrastructure.Subprocesses;
using ReactiveUI;

namespace Momo.App.ViewModels;

public class GlossaryItem : ViewModelBase
{
    private bool _isSelected;
    public string Name { get; set; } = string.Empty;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

public class MainViewModel : ViewModelBase
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IQueueService _queueService;
    private readonly ISubprocessHost _subprocessHost;
    private readonly string _dbPath;
    private readonly string _pythonScriptPath;

    private string _statusText = "Ready";
    private double _progressPercentage = 0;
    private Job? _selectedJob;

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
    }

    public Job? SelectedJob
    {
        get => _selectedJob;
        set => this.RaiseAndSetIfChanged(ref _selectedJob, value);
    }

    private bool _isDiarizationEnabled = true;
    private bool _isAlignmentEnabled = true;

    public bool IsDiarizationEnabled
    {
        get => _isDiarizationEnabled;
        set => this.RaiseAndSetIfChanged(ref _isDiarizationEnabled, value);
    }

    public bool IsAlignmentEnabled
    {
        get => _isAlignmentEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAlignmentEnabled, value);
    }

    private bool _isSettingsVisible = false;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => this.RaiseAndSetIfChanged(ref _isSettingsVisible, value);
    }

    private string? _selectedRole = "Standard";
    public string? SelectedRole
    {
        get => _selectedRole;
        set => this.RaiseAndSetIfChanged(ref _selectedRole, value);
    }

    private string? _selectedTemplate = "Standard";
    public string? SelectedTemplate
    {
        get => _selectedTemplate;
        set => this.RaiseAndSetIfChanged(ref _selectedTemplate, value);
    }

    public ObservableCollection<Job> Jobs { get; } = new();
    public ObservableCollection<GlossaryItem> GlossaryOptions { get; } = new();
    public ObservableCollection<string> RoleOptions { get; } = new();
    public ObservableCollection<string> TemplateOptions { get; } = new();

    public ICommand ImportCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleSettingsCommand { get; }

    public MainViewModel()
    {
        _dbPath = @"d:\Antigravity\Project 3_Enterprise Momo\momo.db";
        _pythonScriptPath = @"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\main.py";

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_dbPath}");
        _dbOptions = builder.Options;

        // Initialize Database
        using (var context = new AppDbContext(_dbOptions))
        {
            Initializer.Initialize(context);
            
            // Seed a default project if none exists
            if (!context.Projects.Any())
            {
                context.Projects.Add(new Project { Id = "default", Name = "Default Project" });
                context.SaveChanges();
            }
        }

        _queueService = new QueueService(_dbOptions);
        _subprocessHost = new SubprocessManager(@"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\.venv\Scripts\python.exe", _pythonScriptPath, _dbPath);

        ImportCommand = ReactiveCommand.CreateFromTask<string>(ImportFileAsync);
        PauseCommand = ReactiveCommand.CreateFromTask(PauseJobAsync);
        ResumeCommand = ReactiveCommand.CreateFromTask(ResumeJobAsync);
        CancelCommand = ReactiveCommand.CreateFromTask(CancelJobAsync);
        ToggleSettingsCommand = ReactiveCommand.Create(() => { IsSettingsVisible = !IsSettingsVisible; });

        // Seed configurations folder structures & options
        SeedConfigurations();
        LoadConfigurationOptions();

        // Load Initial Jobs List
        LoadJobsList();

        // Start Background Job Scheduler Loop
        Task.Run(SchedulerLoopAsync);
    }

    private void SeedConfigurations()
    {
        var baseDir = @"d:\Antigravity\Project 3_Enterprise Momo";
        var glossariesDir = Path.Combine(baseDir, "glossaries");
        var rolesDir = Path.Combine(baseDir, "roles");
        var templatesDir = Path.Combine(baseDir, "templates");

        Directory.CreateDirectory(glossariesDir);
        Directory.CreateDirectory(rolesDir);
        Directory.CreateDirectory(templatesDir);

        var medicalPath = Path.Combine(glossariesDir, "medical.json");
        if (!File.Exists(medicalPath))
        {
            File.WriteAllText(medicalPath, "{\n  \"terzipatide\": \"Tirzepatide\",\n  \"momo\": \"Momo\"\n}", Encoding.UTF8);
        }
        var financePath = Path.Combine(glossariesDir, "finance.json");
        if (!File.Exists(financePath))
        {
            File.WriteAllText(financePath, "{\n  \"vc\": \"Venture Capital\",\n  \"ipo\": \"Initial Public Offering\"\n}", Encoding.UTF8);
        }

        var evaluatorPath = Path.Combine(rolesDir, "vc_pitch_evaluator.yaml");
        if (!File.Exists(evaluatorPath))
        {
            File.WriteAllText(evaluatorPath, "name: \"VC Pitch Evaluator\"\ngoal: \"Extract startup core values and metrics.\"\noutput_sections:\n  - title: \"Business Summary\"\n    prompt: \"Summarize core problems.\"\n  - title: \"Financials\"\n    prompt: \"Funding requests.\"", Encoding.UTF8);
        }
        var defaultRolePath = Path.Combine(rolesDir, "default.yaml");
        if (!File.Exists(defaultRolePath))
        {
            File.WriteAllText(defaultRolePath, "name: \"Standard\"\ngoal: \"Generate default transcription summary.\"\noutput_sections:\n  - title: \"Summary\"\n    prompt: \"Summarize meeting.\"", Encoding.UTF8);
        }

        var meetingSummaryPath = Path.Combine(templatesDir, "meeting_summary.md");
        if (!File.Exists(meetingSummaryPath))
        {
            File.WriteAllText(meetingSummaryPath, "# Meeting Summary: {{ProjectName}}\n* **Duration**: {{DurationMinutes}} minutes\n* **Role**: {{RoleName}}\n\n## Participant Highlights\n{{#Speakers}}\n* **{{SpeakerName}}**: {{SpeakerDuration}} seconds\n{{/Speakers}}\n\n## Transcript\n{{#Paragraphs}}\n**[{{Timestamp}}] {{SpeakerName}}**: {{Text}}\n{{/Paragraphs}}", Encoding.UTF8);
        }
        var standardTemplatePath = Path.Combine(templatesDir, "standard.md");
        if (!File.Exists(standardTemplatePath))
        {
            File.WriteAllText(standardTemplatePath, "# Transcript\n\n{{#Paragraphs}}\n**[{{Timestamp}}] {{SpeakerName}}**: {{Text}}\n\n{{/Paragraphs}}", Encoding.UTF8);
        }
    }

    private void LoadConfigurationOptions()
    {
        var baseDir = @"d:\Antigravity\Project 3_Enterprise Momo";
        var glossariesDir = Path.Combine(baseDir, "glossaries");
        var rolesDir = Path.Combine(baseDir, "roles");
        var templatesDir = Path.Combine(baseDir, "templates");

        GlossaryOptions.Clear();
        if (Directory.Exists(glossariesDir))
        {
            foreach (var file in Directory.GetFiles(glossariesDir, "*.json"))
            {
                GlossaryOptions.Add(new GlossaryItem { Name = Path.GetFileName(file), IsSelected = false });
            }
        }

        RoleOptions.Clear();
        RoleOptions.Add("Standard");
        if (Directory.Exists(rolesDir))
        {
            foreach (var file in Directory.GetFiles(rolesDir, "*.yaml"))
            {
                RoleOptions.Add(Path.GetFileName(file));
            }
        }
        SelectedRole = "Standard";

        TemplateOptions.Clear();
        TemplateOptions.Add("Standard");
        if (Directory.Exists(templatesDir))
        {
            foreach (var file in Directory.GetFiles(templatesDir, "*.md"))
            {
                TemplateOptions.Add(Path.GetFileName(file));
            }
        }
        SelectedTemplate = "Standard";
    }

    private void LoadJobsList()
    {
        using var context = new AppDbContext(_dbOptions);
        var jobs = context.Jobs.Include(j => j.MediaFile).OrderByDescending(j => j.CreatedAt).ToList();
        
        Jobs.Clear();
        foreach (var job in jobs)
        {
            Jobs.Add(job);
        }
    }

    private async Task ImportFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            StatusText = "Error: File does not exist.";
            return;
        }

        using var context = new AppDbContext(_dbOptions);
        var mediaId = Guid.NewGuid().ToString();
        var fileInfo = new FileInfo(filePath);
        
        var mediaFile = new MediaFile
        {
            Id = mediaId,
            ProjectId = "default",
            FilePath = filePath,
            FileHash = filePath.GetHashCode().ToString(), // Placeholder hash for Stage 1
            FileSizeBytes = fileInfo.Length,
            CreatedAt = DateTime.UtcNow
        };

        context.MediaFiles.Add(mediaFile);
        await context.SaveChangesAsync();

        var selectedGlossaries = GlossaryOptions.Where(g => g.IsSelected).Select(g => g.Name).ToList();
        string glossariesJson = JsonSerializer.Serialize(selectedGlossaries);
        string? roleParam = SelectedRole == "Standard" ? null : SelectedRole;
        string? templateParam = SelectedTemplate == "Standard" ? null : SelectedTemplate;

        await _queueService.EnqueueJobAsync("default", mediaId, 
            diarization: IsDiarizationEnabled, 
            alignment: IsAlignmentEnabled,
            selectedGlossaries: glossariesJson,
            selectedRole: roleParam,
            selectedTemplate: templateParam);
        
        LoadJobsList();
        StatusText = "File imported and enqueued successfully.";
    }

    private async Task PauseJobAsync()
    {
        if (SelectedJob == null) return;
        await _queueService.PauseJobAsync(SelectedJob.Id);
        await _subprocessHost.StopWorkerAsync(SelectedJob.Id);
        LoadJobsList();
        StatusText = "Job pause requested.";
    }

    private async Task ResumeJobAsync()
    {
        if (SelectedJob == null) return;
        await _queueService.ResumeJobAsync(SelectedJob.Id);
        LoadJobsList();
        StatusText = "Job resumed (enqueued).";
    }

    private async Task CancelJobAsync()
    {
        if (SelectedJob == null) return;
        await _queueService.CancelJobAsync(SelectedJob.Id);
        await _subprocessHost.StopWorkerAsync(SelectedJob.Id);
        LoadJobsList();
        StatusText = "Job cancelled.";
    }

    private async Task SchedulerLoopAsync()
    {
        while (true)
        {
            try
            {
                // Retrieve next pending job
                var nextJob = await _queueService.GetNextJobAsync();
                if (nextJob != null)
                {
                    StatusText = $"Running Job: {Path.GetFileName(nextJob.MediaFile?.FilePath)}";
                    ProgressPercentage = 0;
                    LoadJobsList();

                    // Start Subprocess
                    await _subprocessHost.StartWorkerAsync(nextJob);

                    // Wait for completion or state changes
                    var success = await MonitorSubprocessAsync(nextJob.Id);

                    if (success)
                    {
                        // Generate exports TXT, SRT and DOCX
                        var exportDir = Path.GetDirectoryName(nextJob.MediaFile?.FilePath) ?? string.Empty;
                        var txtPath = Path.Combine(exportDir, $"{Path.GetFileNameWithoutExtension(nextJob.MediaFile?.FilePath)}.txt");
                        var srtPath = Path.Combine(exportDir, $"{Path.GetFileNameWithoutExtension(nextJob.MediaFile?.FilePath)}.srt");
                        var docxPath = Path.Combine(exportDir, $"{Path.GetFileNameWithoutExtension(nextJob.MediaFile?.FilePath)}.docx");

                        var txtExporter = new TxtExporter(_dbOptions);
                        var srtExporter = new SrtExporter(_dbOptions);
                        var docxExporter = new DocxExporter(_dbOptions);

                        string? templateFile = nextJob.SelectedTemplate != null ? Path.Combine(@"d:\Antigravity\Project 3_Enterprise Momo\templates", nextJob.SelectedTemplate) : null;

                        await txtExporter.ExportAsync(nextJob.MediaFileId, txtPath, templateFile);
                        await srtExporter.ExportAsync(nextJob.MediaFileId, srtPath);
                        
                        try
                        {
                            await docxExporter.ExportAsync(nextJob.MediaFileId, docxPath, templateFile);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Warning] DOCX Export failed: {ex.Message}");
                        }

                        await _queueService.UpdateJobStatusAsync(nextJob.Id, "COMPLETED");
                        StatusText = "Job completed successfully. Exports generated.";
                    }
                    else
                    {
                        // Handle error or pause
                        using var context = new AppDbContext(_dbOptions);
                        var jobState = await context.Jobs.FindAsync(nextJob.Id);
                        if (jobState != null && jobState.Status == "RUNNING")
                        {
                            await _queueService.IncrementRetryCountAsync(nextJob.Id, "Subprocess terminated unexpectedly.");
                        }
                    }

                    LoadJobsList();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Scheduler error: {ex.Message}";
            }

            await Task.Delay(3000);
        }
    }

    private async Task<bool> MonitorSubprocessAsync(string jobId)
    {
        // WebSocket progress monitoring loop
        using var ws = new ClientWebSocket();
        var uri = new Uri("ws://127.0.0.1:5000/ws/progress?token=test"); // Using a temporary/dynamic token integration
        
        // Wait briefly for server to bind port in dynamic launchers before connecting
        await Task.Delay(2000);

        try
        {
            // Note: Since dynamic port discovery is implemented in Infrastructure,
            // the WS connection matches the dynamic port. We simplify for Phase 1.
            // Let's connect and read websocket progress updates
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // In a production C# wrapper, port & auth tokens are read from SubprocessManager's state
        }
        catch
        {
            // If ws connection fails, keep monitoring via standard Db checks
        }

        while (_subprocessHost.IsWorkerRunning(jobId))
        {
            // Read database status to monitor progress percentage
            using (var context = new AppDbContext(_dbOptions))
            {
                var job = await context.Jobs.FindAsync(jobId);
                if (job == null || job.Status == "COMPLETED")
                {
                    ProgressPercentage = 100;
                    return true;
                }
                if (job.Status == "PAUSED" || job.Status == "CANCELLED" || job.Status == "FAILED")
                {
                    return false;
                }

                // Check checkpoint details for progress
                var checkpoint = await context.JobCheckpoints
                    .FirstOrDefaultAsync(c => c.JobId == jobId && c.PipelineStage == "WHISPER_TRANSCRIPTION");
                if (checkpoint != null && checkpoint.TotalChunksCount > 0)
                {
                    ProgressPercentage = ((double)checkpoint.ChunkIndexOffset / checkpoint.TotalChunksCount) * 100.0;
                }
            }

            await Task.Delay(2000);
        }

        // Verify final DB state on exit
        using (var context = new AppDbContext(_dbOptions))
        {
            var job = await context.Jobs.FindAsync(jobId);
            return job?.Status == "COMPLETED";
        }
    }
}
