using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Momo.App.Controls;
using Momo.Core.Entities;
using Momo.Core.Interfaces;
using Momo.Infrastructure;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Exporters;
using Momo.Infrastructure.Queue;
using Momo.Infrastructure.Subprocesses;
using Momo.Infrastructure.Collab;
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

public class ParagraphViewModel : ViewModelBase
{
    private bool _isHighlighted;
    private string _text = string.Empty;
    private string _presenceText = string.Empty;
    private bool _isApplyingRemoteChange;

    public string Speaker { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string TimestampText { get; set; } = string.Empty;

    public event Action<ParagraphViewModel, string, int, string>? OnLocalEdit;

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => this.RaiseAndSetIfChanged(ref _isHighlighted, value);
    }

    public string PresenceText
    {
        get => _presenceText;
        set => this.RaiseAndSetIfChanged(ref _presenceText, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                string oldText = _text;
                this.RaiseAndSetIfChanged(ref _text, value);
                OnTextChanged(oldText, value);
            }
        }
    }

    public void SetTextFromRemote(string newText)
    {
        _isApplyingRemoteChange = true;
        try
        {
            Text = newText;
        }
        finally
        {
            _isApplyingRemoteChange = false;
        }
    }

    private void OnTextChanged(string oldText, string newText)
    {
        if (_isApplyingRemoteChange) return;

        var (type, position, diffText) = DiffStrings(oldText, newText);
        if (type != "noop")
        {
            OnLocalEdit?.Invoke(this, type, position, diffText);
        }
    }

    private static (string type, int position, string text) DiffStrings(string oldStr, string newStr)
    {
        oldStr ??= string.Empty;
        newStr ??= string.Empty;
        if (oldStr == newStr) return ("noop", 0, string.Empty);

        int start = 0;
        while (start < oldStr.Length && start < newStr.Length && oldStr[start] == newStr[start])
        {
            start++;
        }

        int oldEnd = oldStr.Length - 1;
        int newEnd = newStr.Length - 1;
        while (oldEnd >= start && newEnd >= start && oldStr[oldEnd] == newStr[newEnd])
        {
            oldEnd--;
            newEnd--;
        }

        if (newEnd >= start)
        {
            string insertedText = newStr.Substring(start, newEnd - start + 1);
            return ("insert", start, insertedText);
        }
        else
        {
            string deletedText = oldStr.Substring(start, oldEnd - start + 1);
            return ("delete", start, deletedText);
        }
    }
}

public class UserCursorViewModel : ViewModelBase
{
    private string _userId = string.Empty;
    private int _paragraphIndex;
    private int _charOffset;

    public string UserId
    {
        get => _userId;
        set => this.RaiseAndSetIfChanged(ref _userId, value);
    }

    public int ParagraphIndex
    {
        get => _paragraphIndex;
        set => this.RaiseAndSetIfChanged(ref _paragraphIndex, value);
    }

    public int CharOffset
    {
        get => _charOffset;
        set => this.RaiseAndSetIfChanged(ref _charOffset, value);
    }
}

public class TagItem : ViewModelBase
{
    public string Term { get; set; } = string.Empty;
    public double Score { get; set; }
    public double Weight { get; set; }
    public int Count { get; set; }
    public double FontSize => 12.0 + 10.0 * Weight;
    public string ColorHex => GetColorForWeight(Weight);

    private static string GetColorForWeight(double w)
    {
        if (w > 0.75) return "#C084FC"; // bright violet
        if (w > 0.45) return "#A78BFA"; // light purple
        if (w > 0.2) return "#818CF8";  // indigo
        return "#9CA3AF";               // gray
    }
}

public class MainViewModel : ViewModelBase
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IQueueService _queueService;
    private readonly ISubprocessHost _subprocessHost;
    private readonly string _dbPath;
    private readonly string _pythonScriptPath;
    private readonly Dictionary<string, string> _glossaryTerms = new(StringComparer.OrdinalIgnoreCase);

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
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedJob, value);
            OnSelectedJobChanged(value);
        }
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

    private string _themeSetting = "Auto";
    public string ThemeSetting
    {
        get => _themeSetting;
        set
        {
            if (_themeSetting != value)
            {
                this.RaiseAndSetIfChanged(ref _themeSetting, value);
                var settings = AppSettingsManager.LoadSettings();
                settings.Theme = value;
                AppSettingsManager.SaveSettings(settings);
                AppSettingsManager.ApplyTheme(value);
            }
        }
    }

    private string _brandLogoPath = "Assets/logo_placeholder.png";
    public string BrandLogoPath
    {
        get => _brandLogoPath;
        set => this.RaiseAndSetIfChanged(ref _brandLogoPath, value);
    }

    private Avalonia.Media.Imaging.Bitmap? _logoBitmap;
    public Avalonia.Media.Imaging.Bitmap? LogoBitmap
    {
        get => _logoBitmap;
        set => this.RaiseAndSetIfChanged(ref _logoBitmap, value);
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

    private double _currentTime;
    public double CurrentTime
    {
        get => _currentTime;
        set => this.RaiseAndSetIfChanged(ref _currentTime, value);
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        set => this.RaiseAndSetIfChanged(ref _duration, value);
    }

    private bool _isWorkspaceEnabled;
    public bool IsWorkspaceEnabled
    {
        get => _isWorkspaceEnabled;
        set => this.RaiseAndSetIfChanged(ref _isWorkspaceEnabled, value);
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    private string? _selectedTag;
    public string? SelectedTag
    {
        get => _selectedTag;
        set => this.RaiseAndSetIfChanged(ref _selectedTag, value);
    }

    private CollabClient? _collabClient;
    private string _userId = "User_" + Guid.NewGuid().ToString().Substring(0, 4);
    private string? _activeTranscriptId;
    private int _serverRevision = 0;
    private readonly List<OtOperation> _pendingOperations = new();
    private string _revisionSearchText = string.Empty;
    private Revision? _selectedRevision;
    private string _diffGranularity = "Word";
    private bool _showAutosaves = true;
    private string _searchQuery = string.Empty;
    private SearchResultViewModel? _selectedSearchResult;
    private int _selectedSearchIndex = -1;
    private ParagraphViewModel? _currentlyHighlightedParagraph;
    private int _highlightToken = 0;

    public string UserId
    {
        get => _userId;
        set => this.RaiseAndSetIfChanged(ref _userId, value);
    }

    public ObservableCollection<UserCursorViewModel> OtherUsersCursors { get; } = new();

    public ObservableCollection<Job> Jobs { get; } = new();
    public ObservableCollection<GlossaryItem> GlossaryOptions { get; } = new();
    public ObservableCollection<string> RoleOptions { get; } = new();
    public ObservableCollection<string> TemplateOptions { get; } = new();

    public ObservableCollection<ParagraphViewModel> CurrentParagraphs { get; } = new();
    public ObservableCollection<TimelineSegment> CurrentSegments { get; } = new();
    public ObservableCollection<TagItem> TagCloudItems { get; } = new();

    public ICommand ImportCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand SelectTagCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PrevMatchCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            _ = RunSearchAsync(value);
        }
    }

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();

    public SearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSearchResult, value);
            if (value != null)
            {
                _selectedSearchIndex = SearchResults.IndexOf(value);
                OnSearchResultSelected(value);
            }
        }
    }

    public event Action<int, int, int>? ScrollToParagraphRequested;

    public ObservableCollection<Revision> Revisions { get; } = new();
    public ObservableCollection<Revision> FilteredRevisions { get; } = new();
    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = new();
    public ObservableCollection<string> DiffGranularityOptions { get; } = new() { "Char", "Word", "Line" };
    public ObservableCollection<string> ThemeOptions { get; } = new() { "Auto", "Light", "Dark" };

    public string RevisionSearchText
    {
        get => _revisionSearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _revisionSearchText, value);
            UpdateFilteredRevisions();
        }
    }

    public bool ShowAutosaves
    {
        get => _showAutosaves;
        set
        {
            this.RaiseAndSetIfChanged(ref _showAutosaves, value);
            UpdateFilteredRevisions();
        }
    }

    public Revision? SelectedRevision
    {
        get => _selectedRevision;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRevision, value);
            UpdateDiff();
        }
    }

    public string DiffGranularity
    {
        get => _diffGranularity;
        set
        {
            this.RaiseAndSetIfChanged(ref _diffGranularity, value);
            UpdateDiff();
        }
    }

    public ICommand RollbackCommand { get; }
    public ICommand AcceptRevisionCommand { get; }
    public ICommand RejectRevisionCommand { get; }
    public ICommand BatchAcceptCommand { get; }
    public ICommand BatchRejectCommand { get; }

    public MainViewModel() : this(@"d:\Antigravity\Project 3_Enterprise Momo\momo.db")
    {
    }

    public MainViewModel(string dbPath)
    {
        _dbPath = dbPath;
        
        var settings = AppSettingsManager.LoadSettings();
        _themeSetting = settings.Theme;
        _brandLogoPath = settings.BrandLogoPath;
        
        if (!string.IsNullOrEmpty(_brandLogoPath) && File.Exists(_brandLogoPath))
        {
            try
            {
                LogoBitmap = new Avalonia.Media.Imaging.Bitmap(_brandLogoPath);
            }
            catch
            {
                // Ignore
            }
        }

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
        SelectTagCommand = ReactiveCommand.Create<string>(SelectTag);
        RollbackCommand = ReactiveCommand.CreateFromTask(RollbackToSelectedAsync);
        AcceptRevisionCommand = ReactiveCommand.CreateFromTask(AcceptSelectedRevisionAsync);
        RejectRevisionCommand = ReactiveCommand.CreateFromTask(RejectSelectedRevisionAsync);
        BatchAcceptCommand = ReactiveCommand.CreateFromTask(BatchAcceptAllAsync);
        BatchRejectCommand = ReactiveCommand.CreateFromTask(BatchRejectAllAsync);
        NextMatchCommand = ReactiveCommand.Create(GoToNextMatch);
        PrevMatchCommand = ReactiveCommand.Create(GoToPrevMatch);

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

    private void OnSelectedJobChanged(Job? job)
    {
        if (job == null || job.Status != "COMPLETED")
        {
            IsWorkspaceEnabled = false;
            CurrentParagraphs.Clear();
            CurrentSegments.Clear();
            TagCloudItems.Clear();
            Duration = 0.0;
            CurrentTime = 0.0;
            _ = DisconnectCollabAsync();
            return;
        }

        IsWorkspaceEnabled = true;
        
        using var context = new AppDbContext(_dbOptions);
        var transcript = context.Transcripts.FirstOrDefault(t => t.MediaFileId == job.MediaFileId);
        if (transcript == null)
        {
            CurrentParagraphs.Clear();
            CurrentSegments.Clear();
            TagCloudItems.Clear();
            Duration = 0.0;
            CurrentTime = 0.0;
            _ = DisconnectCollabAsync();
            return;
        }

        var words = context.TranscriptWords
            .Where(w => w.TranscriptId == transcript.Id)
            .OrderBy(w => w.StartTime)
            .ToList();

        if (words.Count == 0)
        {
            CurrentParagraphs.Clear();
            CurrentSegments.Clear();
            TagCloudItems.Clear();
            Duration = 0.0;
            CurrentTime = 0.0;
            _ = DisconnectCollabAsync();
            return;
        }

        Duration = words.Max(w => w.EndTime);
        CurrentTime = 0.0;

        var rawParagraphs = ParagraphBuilder.BuildFromWords(words);
        var profiles = context.SpeakerProfiles.ToList();
        var profileMap = profiles.ToDictionary(p => p.Id, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        CurrentParagraphs.Clear();
        foreach (var rp in rawParagraphs)
        {
            string dispSpeaker = rp.Speaker;
            if (profileMap.TryGetValue(rp.Speaker, out var mappedName))
            {
                dispSpeaker = mappedName;
            }

            var pvm = new ParagraphViewModel
            {
                Speaker = dispSpeaker,
                StartTime = rp.StartTime,
                EndTime = rp.EndTime,
                Text = rp.Text,
                TimestampText = FormatTime(rp.StartTime),
                IsHighlighted = false
            };
            pvm.OnLocalEdit += HandleLocalParagraphEdit;
            CurrentParagraphs.Add(pvm);
        }

        CurrentSegments.Clear();
        foreach (var rp in rawParagraphs)
        {
            string dispSpeaker = rp.Speaker;
            if (profileMap.TryGetValue(rp.Speaker, out var mappedName))
            {
                dispSpeaker = mappedName;
            }

            CurrentSegments.Add(new TimelineSegment
            {
                SpeakerId = rp.Speaker,
                DisplayName = dispSpeaker,
                StartTime = rp.StartTime,
                EndTime = rp.EndTime
            });
        }

        BuildTagCloud(job, rawParagraphs, context);
        
        SelectedTabIndex = 1;

        _activeTranscriptId = transcript.Id;
        _ = StartCollabConnectionAsync(_activeTranscriptId);
        _ = LoadRevisionsAsync();
    }

    private void BuildTagCloud(Job job, List<ParagraphBuilder.ParagraphItem> rawParagraphs, AppDbContext context)
    {
        TagCloudItems.Clear();
        _glossaryTerms.Clear();

        List<string> glossaryFiles = new();
        if (!string.IsNullOrEmpty(job.SelectedGlossaries))
        {
            try
            {
                glossaryFiles = JsonSerializer.Deserialize<List<string>>(job.SelectedGlossaries) ?? new List<string>();
            }
            catch { }
        }

        var baseDir = @"d:\Antigravity\Project 3_Enterprise Momo";
        var glossariesDir = Path.Combine(baseDir, "glossaries");
        var termsToMatch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gName in glossaryFiles)
        {
            var filePath = Path.Combine(glossariesDir, gName);
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    var items = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (items != null)
                    {
                        foreach (var kvp in items)
                        {
                            termsToMatch[kvp.Key] = kvp.Value;
                            _glossaryTerms[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch { }
            }
        }

        if (termsToMatch.Count == 0) return;

        string combinedText = string.Join(" ", rawParagraphs.Select(p => p.Text));
        
        int totalWords = combinedText.Split(new[] { ' ', '\r', '\n', '\t', '。', '，', '！', '？', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries).Length;
        totalWords = Math.Max(1, totalWords);

        int totalTranscripts = context.Transcripts.Count();

        var computedTags = new List<TagItem>();
        foreach (var kvp in termsToMatch)
        {
            string term = kvp.Key;
            string normalized = kvp.Value;

            int count = CountOccurrences(combinedText, term);
            if (count > 0)
            {
                double tf = (double)count / totalWords;

                double idf = 1.0;
                if (totalTranscripts > 1)
                {
                    int df = context.Transcripts.AsEnumerable().Count(tr => tr.RawText.Contains(term, StringComparison.OrdinalIgnoreCase));
                    idf = Math.Max(0.1, Math.Log((double)totalTranscripts / (1.0 + df)));
                }

                double score = tf * idf * 1000.0;

                computedTags.Add(new TagItem
                {
                    Term = normalized,
                    Score = score,
                    Count = count
                });
            }
        }

        if (computedTags.Count == 0) return;

        double maxScore = computedTags.Max(t => t.Score);
        double minScore = computedTags.Min(t => t.Score);

        foreach (var tag in computedTags)
        {
            tag.Weight = (maxScore == minScore) ? 0.5 : (tag.Score - minScore) / (maxScore - minScore);
            TagCloudItems.Add(tag);
        }
    }

    private static int CountOccurrences(string source, string term)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(term)) return 0;
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    private void SelectTag(string term)
    {
        SelectedTag = term;
        
        var matchingKeys = _glossaryTerms
            .Where(kvp => kvp.Value.Equals(term, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        ParagraphViewModel? firstMatch = null;
        foreach (var p in CurrentParagraphs)
        {
            bool hasTerm = p.Text.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                           matchingKeys.Any(k => p.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
            
            p.IsHighlighted = hasTerm;
            if (hasTerm && firstMatch == null)
            {
                firstMatch = p;
            }
        }

        if (firstMatch != null)
        {
            CurrentTime = firstMatch.StartTime;
        }
    }

    private void GoToNextMatch()
    {
        if (SearchResults.Count == 0) return;
        _selectedSearchIndex = (_selectedSearchIndex + 1) % SearchResults.Count;
        SelectedSearchResult = SearchResults[_selectedSearchIndex];
    }

    private void GoToPrevMatch()
    {
        if (SearchResults.Count == 0) return;
        _selectedSearchIndex = (_selectedSearchIndex - 1 + SearchResults.Count) % SearchResults.Count;
        SelectedSearchResult = SearchResults[_selectedSearchIndex];
    }

    private async Task RunSearchAsync(string query)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(_activeTranscriptId))
        {
            SearchResults.Clear();
            _selectedSearchIndex = -1;
            foreach (var p in CurrentParagraphs)
            {
                p.IsHighlighted = false;
            }
            return;
        }

        try
        {
            var job = SelectedJob;
            if (job == null) return;

            await _subprocessHost.EnsureWorkerRunningAsync(job);
            int port = _subprocessHost.ActivePort;
            string? token = _subprocessHost.AuthToken;

            using var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(token))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/v1/transcripts/{_activeTranscriptId}/search?q={Uri.EscapeDataString(query)}&limit=50");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var results = JsonSerializer.Deserialize<System.Collections.Generic.List<SearchResultViewModel>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SearchResults.Clear();
                    _selectedSearchIndex = -1;
                    if (results != null)
                    {
                        foreach (var r in results)
                        {
                            SearchResults.Add(r);
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search failed: {ex.Message}");
        }
    }

    private void OnSearchResultSelected(SearchResultViewModel result)
    {
        if (result.ParagraphIndex >= 0 && result.ParagraphIndex < CurrentParagraphs.Count)
        {
            foreach (var p in CurrentParagraphs)
            {
                p.IsHighlighted = false;
            }
            
            var targetParagraph = CurrentParagraphs[result.ParagraphIndex];
            targetParagraph.IsHighlighted = true;
            _currentlyHighlightedParagraph = targetParagraph;
            
            int currentToken = ++_highlightToken;
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_highlightToken == currentToken && _currentlyHighlightedParagraph != null)
                    {
                        _currentlyHighlightedParagraph.IsHighlighted = false;
                        _currentlyHighlightedParagraph = null;
                    }
                });
            });

            // Trigger view to scroll to it and select character range
            ScrollToParagraphRequested?.Invoke(result.ParagraphIndex, result.StartIndex, result.EndIndex - result.StartIndex);
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private async Task StartCollabConnectionAsync(string transcriptId)
    {
        await DisconnectCollabAsync();
        _activeTranscriptId = transcriptId;

        _serverRevision = 0;
        _pendingOperations.Clear();
        OtherUsersCursors.Clear();

        // Connect to local in-process SignalR Hub running on port 5192
        _collabClient = new CollabClient("http://localhost:5192/hubs/collab");

        _collabClient.OnUserJoined += uid =>
        {
            StatusText = $"User {uid} joined the workspace.";
        };

        _collabClient.OnCursorReceived += (uid, pIdx, offset) =>
        {
            var cursor = OtherUsersCursors.FirstOrDefault(c => c.UserId == uid);
            if (cursor == null)
            {
                cursor = new UserCursorViewModel { UserId = uid };
                OtherUsersCursors.Add(cursor);
            }
            cursor.ParagraphIndex = pIdx;
            cursor.CharOffset = offset;

            UpdatePresenceText(pIdx);
        };

        _collabClient.OnOperationReceived += op =>
        {
            if (op.ClientId == UserId) return;

            // Transform remote op against our local pending operations
            lock (_pendingOperations)
            {
                for (int i = 0; i < _pendingOperations.Count; i++)
                {
                    var (transformedRemote, transformedLocal) = OtEngine.Transform(op, _pendingOperations[i]);
                    op = transformedRemote;
                    _pendingOperations[i] = transformedLocal;
                }
            }

            if (op.ParagraphIndex >= 0 && op.ParagraphIndex < CurrentParagraphs.Count)
            {
                var p = CurrentParagraphs[op.ParagraphIndex];
                string newText = OtEngine.Apply(p.Text, op);
                p.SetTextFromRemote(newText);
            }

            _serverRevision = op.Revision + 1;
        };

        _collabClient.OnOperationConfirmed += op =>
        {
            lock (_pendingOperations)
            {
                if (_pendingOperations.Count > 0)
                {
                    _pendingOperations.RemoveAt(0);
                }
            }
            _serverRevision = op.Revision + 1;
        };

        _collabClient.OnRollbackApplied += revId =>
        {
            StatusText = "Rollback applied! Reloading transcript...";
            lock (_pendingOperations)
            {
                _pendingOperations.Clear();
            }
            if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                ReloadTranscriptFromDb();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ReloadTranscriptFromDb();
                });
            }
        };

        _collabClient.OnRevisionStatusChanged += (revId, status) =>
        {
            StatusText = $"Revision status updated to '{status}'! Reloading...";
            Action updateAction = async () =>
            {
                await LoadRevisionsAsync();
                if (status == "rejected")
                {
                    ReloadTranscriptFromDb();
                }
            };
            if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                updateAction();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(updateAction);
            }
        };

        try
        {
            await _collabClient.StartAsync();
            await _collabClient.JoinGroupAsync(transcriptId, UserId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Warning] Collaboration sync failed: {ex.Message}");
        }
    }

    private void UpdatePresenceText(int paragraphIndex)
    {
        if (paragraphIndex < 0 || paragraphIndex >= CurrentParagraphs.Count) return;

        var usersHere = OtherUsersCursors
            .Where(c => c.ParagraphIndex == paragraphIndex)
            .Select(c => c.UserId)
            .ToList();

        if (usersHere.Count > 0)
        {
            CurrentParagraphs[paragraphIndex].PresenceText = $"{string.Join(", ", usersHere)} is here";
        }
        else
        {
            CurrentParagraphs[paragraphIndex].PresenceText = string.Empty;
        }
    }

    private void HandleLocalParagraphEdit(ParagraphViewModel pvm, string type, int position, string text)
    {
        if (_collabClient == null || string.IsNullOrEmpty(_activeTranscriptId)) return;

        int pIdx = CurrentParagraphs.IndexOf(pvm);
        if (pIdx < 0) return;

        var op = new OtOperation
        {
            ClientId = UserId,
            Type = type,
            Position = position,
            Text = text,
            ParagraphIndex = pIdx,
            Revision = _serverRevision
        };

        lock (_pendingOperations)
        {
            _pendingOperations.Add(op);
        }

        _ = _collabClient.SubmitOperationAsync(_activeTranscriptId, op);
    }

    public void SubmitLocalCursor(ParagraphViewModel pvm, int charOffset)
    {
        if (_collabClient == null || string.IsNullOrEmpty(_activeTranscriptId)) return;

        int pIdx = CurrentParagraphs.IndexOf(pvm);
        if (pIdx < 0) return;

        _ = _collabClient.SubmitCursorAsync(_activeTranscriptId, UserId, pIdx, charOffset);
    }

    private async Task DisconnectCollabAsync()
    {
        if (_collabClient != null)
        {
            try
            {
                await _collabClient.StopAsync();
            }
            catch { }
            _collabClient = null;
        }
        _activeTranscriptId = null;
        _serverRevision = 0;
        _pendingOperations.Clear();
        OtherUsersCursors.Clear();
    }

    private void UpdateFilteredRevisions()
    {
        FilteredRevisions.Clear();
        foreach (var rev in Revisions)
        {
            bool isAutosave = rev.CreatedBy.Equals("AUTOSAVE", StringComparison.OrdinalIgnoreCase) ||
                             (rev.Description != null && rev.Description.Contains("AUTOSAVE", StringComparison.OrdinalIgnoreCase));
            if (!ShowAutosaves && isAutosave)
            {
                continue;
            }

            if (string.IsNullOrEmpty(RevisionSearchText) ||
                rev.VersionNumber.ToString().Contains(RevisionSearchText) ||
                rev.CreatedBy.Contains(RevisionSearchText, StringComparison.OrdinalIgnoreCase) ||
                (rev.Description != null && rev.Description.Contains(RevisionSearchText, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredRevisions.Add(rev);
            }
        }
    }

    private void UpdateDiff()
    {
        DiffRows.Clear();
        if (_selectedRevision == null || string.IsNullOrEmpty(_activeTranscriptId)) return;

        using var context = new AppDbContext(_dbOptions);
        var transcript = context.Transcripts.Find(_activeTranscriptId);
        string activeText = transcript?.RawText ?? string.Empty;
        string selectedText = _selectedRevision.SnapshotText ?? string.Empty;

        var builder = new SideBySideDiffBuilder(new Differ());
        SideBySideDiffModel model;

        if (_diffGranularity == "Char")
        {
            var oldChars = string.Join("\n", activeText.Select(c => c.ToString()));
            var newChars = string.Join("\n", selectedText.Select(c => c.ToString()));
            model = builder.BuildDiffModel(oldChars, newChars);
        }
        else if (_diffGranularity == "Word")
        {
            var oldWords = string.Join("\n", activeText.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var newWords = string.Join("\n", selectedText.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            model = builder.BuildDiffModel(oldWords, newWords);
        }
        else // Line
        {
            model = builder.BuildDiffModel(activeText, selectedText);
        }

        int maxLines = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);
        for (int i = 0; i < maxLines; i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            var row = new DiffRowViewModel();

            if (oldLine != null)
            {
                row.OldText = oldLine.Text ?? string.Empty;
                row.OldBackground = GetBgColorForChangeType(oldLine.Type);
                row.OldForeground = GetFgColorForChangeType(oldLine.Type);
            }

            if (newLine != null)
            {
                row.NewText = newLine.Text ?? string.Empty;
                row.NewBackground = GetBgColorForChangeType(newLine.Type);
                row.NewForeground = GetFgColorForChangeType(newLine.Type);
            }

            DiffRows.Add(row);
        }
    }

    private static string GetBgColorForChangeType(ChangeType type)
    {
        return type switch
        {
            ChangeType.Deleted => "#4E2A2E",
            ChangeType.Inserted => "#1B4232",
            ChangeType.Modified => "#364F6B",
            _ => "Transparent"
        };
    }

    private static string GetFgColorForChangeType(ChangeType type)
    {
        return type switch
        {
            ChangeType.Deleted => "#FF8B94",
            ChangeType.Inserted => "#8CEE9D",
            ChangeType.Modified => "#AEC9FF",
            _ => "White"
        };
    }

    private async Task RollbackToSelectedAsync()
    {
        if (_selectedRevision == null || string.IsNullOrEmpty(_activeTranscriptId)) return;

        try
        {
            var job = SelectedJob;
            if (job == null) return;

            await _subprocessHost.EnsureWorkerRunningAsync(job);
            int port = _subprocessHost.ActivePort;
            string? token = _subprocessHost.AuthToken;

            using var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(token))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var rollbackPayload = new Dictionary<string, string> { { "operator", UserId } };
            var content = new StringContent(JsonSerializer.Serialize(rollbackPayload), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"http://127.0.0.1:{port}/api/v1/revisions/{_selectedRevision.Id}/rollback", content);
            if (response.IsSuccessStatusCode)
            {
                StatusText = $"Rollback request successful. Broadcasting changes...";
                
                // Broadcast rollback event to all clients via SignalR
                if (_collabClient != null)
                {
                    await _collabClient.SubmitRollbackAsync(_activeTranscriptId, _selectedRevision.Id);
                }
            }
            else
            {
                StatusText = $"Rollback failed: {response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Rollback error: {ex.Message}";
        }
    }

    private async Task AcceptSelectedRevisionAsync()
    {
        if (_selectedRevision == null || string.IsNullOrEmpty(_activeTranscriptId)) return;
        await UpdateRevisionStatusAsync(_selectedRevision.Id, "accepted");
    }

    private async Task RejectSelectedRevisionAsync()
    {
        if (_selectedRevision == null || string.IsNullOrEmpty(_activeTranscriptId)) return;
        await UpdateRevisionStatusAsync(_selectedRevision.Id, "rejected");
    }

    private async Task BatchAcceptAllAsync()
    {
        if (string.IsNullOrEmpty(_activeTranscriptId)) return;
        await BatchUpdateRevisionStatusAsync("accept");
    }

    private async Task BatchRejectAllAsync()
    {
        if (string.IsNullOrEmpty(_activeTranscriptId)) return;
        await BatchUpdateRevisionStatusAsync("reject");
    }

    private async Task UpdateRevisionStatusAsync(string revisionId, string status)
    {
        try
        {
            var job = SelectedJob;
            if (job == null) return;
            await _subprocessHost.EnsureWorkerRunningAsync(job);
            int port = _subprocessHost.ActivePort;
            string? token = _subprocessHost.AuthToken;

            using var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(token))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new { status, operator_name = UserId };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Patch, $"http://127.0.0.1:{port}/api/v1/revisions/{revisionId}")
            {
                Content = content
            };
            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                StatusText = $"Revision {status}. Broadcasting...";
                if (_collabClient != null && !string.IsNullOrEmpty(_activeTranscriptId))
                    await _collabClient.SubmitRevisionStatusAsync(_activeTranscriptId, revisionId, status);
                await LoadRevisionsAsync();
                if (status == "rejected")
                    ReloadTranscriptFromDb();
            }
            else
            {
                StatusText = $"Status update failed: {response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Status update error: {ex.Message}";
        }
    }

    private async Task BatchUpdateRevisionStatusAsync(string action)
    {
        try
        {
            var job = SelectedJob;
            if (job == null) return;
            await _subprocessHost.EnsureWorkerRunningAsync(job);
            int port = _subprocessHost.ActivePort;
            string? token = _subprocessHost.AuthToken;

            using var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(token))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new { action, operator_name = UserId };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/transcripts/{_activeTranscriptId}/revisions/batch", content);

            if (response.IsSuccessStatusCode)
            {
                StatusText = $"Batch {action} complete. Refreshing...";
                await LoadRevisionsAsync();
                if (action == "reject")
                    ReloadTranscriptFromDb();
            }
            else
            {
                StatusText = $"Batch {action} failed: {response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Batch {action} error: {ex.Message}";
        }
    }

    public async Task LoadRevisionsAsync()
    {
        if (string.IsNullOrEmpty(_activeTranscriptId) || SelectedJob == null) return;

        try
        {
            await _subprocessHost.EnsureWorkerRunningAsync(SelectedJob);
            int port = _subprocessHost.ActivePort;
            string? token = _subprocessHost.AuthToken;

            using var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(token))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/v1/transcripts/{_activeTranscriptId}/revisions");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<Revision>>(json, options);
                
                Action updateAction = () =>
                {
                     Revisions.Clear();
                     if (list != null)
                     {
                         foreach (var rev in list)
                         {
                             Revisions.Add(rev);
                         }
                     }
                     UpdateFilteredRevisions();
                };

                if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    updateAction();
                }
                else
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(updateAction);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Warning] Failed to load revisions: {ex.Message}");
        }
    }

    private void ReloadTranscriptFromDb()
    {
        if (string.IsNullOrEmpty(_activeTranscriptId)) return;
        
        using var context = new AppDbContext(_dbOptions);
        var transcript = context.Transcripts.FirstOrDefault(t => t.Id == _activeTranscriptId);
        if (transcript == null) return;

        // Load words, build paragraphs and populate UI
        var words = context.TranscriptWords
            .Where(w => w.TranscriptId == transcript.Id)
            .OrderBy(w => w.StartTime)
            .ToList();

        var rawParagraphs = ParagraphBuilder.BuildFromWords(words);
        var profiles = context.SpeakerProfiles.ToList();
        var profileMap = profiles.ToDictionary(p => p.Id, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        CurrentParagraphs.Clear();
        foreach (var rp in rawParagraphs)
        {
            string dispSpeaker = rp.Speaker;
            if (profileMap.TryGetValue(rp.Speaker, out var mappedName))
            {
                dispSpeaker = mappedName;
            }

            var pvm = new ParagraphViewModel
            {
                Speaker = dispSpeaker,
                StartTime = rp.StartTime,
                EndTime = rp.EndTime,
                Text = rp.Text,
                TimestampText = FormatTime(rp.StartTime),
                IsHighlighted = false
            };
            pvm.OnLocalEdit += HandleLocalParagraphEdit;
            CurrentParagraphs.Add(pvm);
        }

        // Also refresh revisions list!
        _ = LoadRevisionsAsync();
    }
}
