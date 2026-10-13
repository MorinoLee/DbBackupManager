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
