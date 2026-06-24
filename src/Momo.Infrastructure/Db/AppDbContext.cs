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
