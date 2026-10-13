using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Domain.Tests.Notifications;

public sealed class RemoteGapEpisodeTests
{
    [Fact]
    public void RecoveryClosesTheEpisodeAndRejectsAnEarlierTime()
    {
        var opened = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
        var episode = new RemoteGapEpisode(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "remote_base_unconfirmed", Guid.NewGuid(), opened);

        Assert.True(episode.IsOpen);
        episode.MarkRecovered(opened.AddMinutes(5));
        Assert.False(episode.IsOpen);
        episode.MarkRecovered(opened.AddMinutes(5));
        Assert.Throws<ArgumentException>(() => episode.MarkRecovered(opened.AddMinutes(-1)));
        Assert.Throws<InvalidOperationException>(() => episode.MarkRecovered(opened.AddMinutes(6)));
    }
}

public sealed class RemoteGapProjectionPendingTests
{
    [Fact]
    public void RepeatedTargetStaysOneRowAndTheLatestTaskIsKept()
    {
        var opened = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
        var pending = new RemoteGapProjectionPending(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BackupTaskStage.Transfer, opened);
        var target = Guid.NewGuid();
        pending.IncludeTarget(target);
        pending.IncludeTarget(target);
        var laterTask = Guid.NewGuid();
        pending.Note(laterTask, null, opened.AddMinutes(2));

        Assert.Equal([target], pending.TargetIds());
        Assert.Equal(laterTask, pending.TaskId);
        Assert.Null(pending.Stage);
        Assert.Equal(opened.AddMinutes(2), pending.RequestedAtUtc);
    }
}
