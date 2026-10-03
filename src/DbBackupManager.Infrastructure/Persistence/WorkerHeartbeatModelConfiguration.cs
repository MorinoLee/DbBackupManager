using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class WorkerHeartbeatModelConfiguration : IEntityTypeConfiguration<WorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<WorkerHeartbeat> builder)
    {
        builder.ToTable("WorkerHeartbeats", table => table.HasCheckConstraint("CK_WorkerHeartbeats_SingleWorker", "[Id] = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
    }
}
