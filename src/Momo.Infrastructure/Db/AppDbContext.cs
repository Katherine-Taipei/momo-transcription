using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;

namespace Momo.Infrastructure.Db;

public class AppDbContext : DbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobCheckpoint> JobCheckpoints => Set<JobCheckpoint>();
    public DbSet<AudioChunk> AudioChunks => Set<AudioChunk>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<TranscriptWord> TranscriptWords => Set<TranscriptWord>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<MomoTask> Tasks => Set<MomoTask>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<EmbeddingCache> EmbeddingCaches => Set<EmbeddingCache>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Projects Table Configuration
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
        });

        // MediaFiles Table Configuration
        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.ToTable("media_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.FileHash).IsRequired();
            entity.HasOne(e => e.Project)
                  .WithMany(p => p!.MediaFiles)
                  .HasForeignKey(e => e.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Jobs Table Configuration
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("job_queue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired();
            entity.HasOne(e => e.Project)
                  .WithMany(p => p!.Jobs)
                  .HasForeignKey(e => e.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.MediaFile)
                  .WithMany()
                  .HasForeignKey(e => e.MediaFileId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // JobCheckpoints Table Configuration
        modelBuilder.Entity<JobCheckpoint>(entity =>
        {
            entity.ToTable("job_checkpoints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PipelineStage).IsRequired();
            entity.HasOne(e => e.Job)
                  .WithMany()
                  .HasForeignKey(e => e.JobId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // AudioChunks Table Configuration
        modelBuilder.Entity<AudioChunk>(entity =>
        {
            entity.ToTable("audio_chunks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.HasOne(e => e.MediaFile)
                  .WithMany(m => m!.AudioChunks)
                  .HasForeignKey(e => e.MediaFileId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Transcripts Table Configuration
        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.ToTable("transcripts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawText).IsRequired();
            entity.HasOne(e => e.Project)
                  .WithMany()
                  .HasForeignKey(e => e.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.MediaFile)
                  .WithMany()
                  .HasForeignKey(e => e.MediaFileId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // TranscriptWords Table Configuration
        modelBuilder.Entity<TranscriptWord>(entity =>
        {
            entity.ToTable("transcript_words");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Word).IsRequired();
            entity.Property(e => e.SpeakerId).IsRequired();
            entity.HasOne(e => e.Transcript)
                  .WithMany(t => t!.TranscriptWords)
                  .HasForeignKey(e => e.TranscriptId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // SpeakerProfiles Table Configuration
        modelBuilder.Entity<SpeakerProfile>(entity =>
        {
            entity.ToTable("speaker_profiles");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalId).IsRequired();
            entity.Property(e => e.DisplayName).IsRequired();
            entity.Property(e => e.VoiceprintEmbedding).IsRequired();
        });

        // Comments Table Configuration
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.ToTable("comments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.ParagraphId).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.HasOne(e => e.Transcript)
                  .WithMany()
                  .HasForeignKey(e => e.TranscriptId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Parent)
                  .WithMany()
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ParagraphId, e.CreatedAt })
                  .HasDatabaseName("idx_comments_paragraph_created");
        });

        // Tasks Table Configuration
        modelBuilder.Entity<MomoTask>(entity =>
        {
            entity.ToTable("tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Assignee).IsRequired();
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.ParagraphId).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.HasOne(e => e.Transcript)
                  .WithMany()
                  .HasForeignKey(e => e.TranscriptId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ParagraphId, e.CreatedAt })
                  .HasDatabaseName("idx_tasks_paragraph_created");
        });

        // Revisions Table Configuration
        modelBuilder.Entity<Revision>(entity =>
        {
            entity.ToTable("revisions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VersionNumber).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.SnapshotText).IsRequired();
            entity.Property(e => e.Status).HasDefaultValue("pending").IsRequired();
            entity.HasOne(e => e.Transcript)
                  .WithMany()
                  .HasForeignKey(e => e.TranscriptId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TranscriptId, e.VersionNumber })
                  .IsUnique()
                  .HasDatabaseName("idx_revisions_transcript_version");
        });

        // EmbeddingCache Table Configuration
        modelBuilder.Entity<EmbeddingCache>(entity =>
        {
            entity.ToTable("embedding_cache");
            entity.HasKey(e => e.TextHash);
            entity.Property(e => e.EmbeddingJson).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.CreatedAt)
                  .HasDatabaseName("idx_embedding_cache_created_at");
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var columnName = property.Name;
                var snakeCaseName = System.Text.RegularExpressions.Regex.Replace(columnName, @"([a-z0-9])([A-Z])", "$1_$2").ToLower();
                property.SetColumnName(snakeCaseName);
            }
        }
    }
}
