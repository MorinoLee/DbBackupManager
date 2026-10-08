using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    private void PrepareExecutionContractEntries()
    {
        foreach (var entry in ChangeTracker.Entries<BackupPlanExecutionOperation>())
        {
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("操作回执不能删除。");
            if (entry.State != EntityState.Modified) continue;
            foreach (var name in new[] { "TaskId", "AttemptId", "Kind", "Sequence", "IntendedBackupSetId", "ObservedAtUtc" })
                RejectChangedProperty(entry, name, "操作身份不能修改。");
            var original = entry.Property(x => x.State).OriginalValue;
            if (entry.ComplexProperty(x => x.Facts).IsModified && original != BackupExecutionOperationState.Reserved)
                throw new InvalidOperationException("冻结事实不能覆盖。");
            var current = entry.Entity.State;
            if (current != original && !((original == BackupExecutionOperationState.Reserved
                    && current is BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Abandoned)
                || original == BackupExecutionOperationState.Frozen && current == BackupExecutionOperationState.Applied))
                throw new InvalidOperationException("操作状态不能回退。");
        }
        foreach (var entry in ChangeTracker.Entries<BackupPlanExecutionObservation>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("操作观察只能追加。");
        foreach (var entry in ChangeTracker.Entries<BackupInvocationAuthorization>())
        {
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("调用授权历史不能删除。");
            if (entry.State != EntityState.Modified) continue;
            foreach (var name in new[] { "DatabaseId", "TaskId", "AttemptId", "MutationId", "SqlOperationId", "GrantedAtUtc" })
                RejectChangedProperty(entry, name, "调用授权身份不能修改。");
            if (entry.ComplexProperty(x => x.Binding).IsModified) throw new InvalidOperationException("原调用会话不能反填或替换。");
            foreach (var name in new[] { "TerminalObservedAtUtc", "TerminationKind", "TerminationEvidenceId", "TerminationMutationId" })
            {
                var property = entry.Property(name);
                if (property.IsModified && property.OriginalValue is not null
                    && !Equals(property.OriginalValue, property.CurrentValue))
                    throw new InvalidOperationException("调用终止证据不能覆盖。");
            }
        }
    }
}
