using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Tests;

public sealed class AdminUserTests
{
    [Fact]
    public void ConstructorCreatesEnabledUser()
    {
        var id = Guid.NewGuid();

        var user = new AdminUser(id, "管理员", "管理员", "synthetic-hash", "synthetic-stamp");

        Assert.Equal(id, user.Id);
        Assert.Equal("管理员", user.Username);
        Assert.True(user.IsEnabled);
        Assert.Empty(user.RowVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ConstructorRejectsBlankUsername(string username)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AdminUser(Guid.NewGuid(), username, "ADMIN", "synthetic-hash", "synthetic-stamp"));
    }

    [Fact]
    public void FailedLoginsInsideWindowLockAccountAtThreshold()
    {
        var user = new AdminUser(
            Guid.NewGuid(),
            "管理员",
            "管理员",
            "synthetic-hash",
            "synthetic-stamp");
        var now = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 5; index++)
        {
            user.RecordFailedLogin(now.AddSeconds(index), TimeSpan.FromMinutes(5), 5, TimeSpan.FromMinutes(15));
        }

        Assert.True(user.IsLockedOut(now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(now.AddMinutes(16)));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.FailedLoginWindowStartedAtUtc);
    }

    [Fact]
    public void SuccessfulLoginClearsFailuresAndCanReplaceHash()
    {
        var user = new AdminUser(
            Guid.NewGuid(),
            "管理员",
            "管理员",
            "old-hash",
            "synthetic-stamp");
        user.RecordFailedLogin(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            5,
            TimeSpan.FromMinutes(15));

        user.RecordSuccessfulLogin("new-hash");

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
    }

    [Fact]
    public void ChangePasswordChangesSecurityStampAndClearsLockout()
    {
        var user = new AdminUser(
            Guid.NewGuid(),
            "管理员",
            "管理员",
            "old-hash",
            "old-stamp");
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 5; index++)
        {
            user.RecordFailedLogin(now, TimeSpan.FromMinutes(5), 5, TimeSpan.FromMinutes(15));
        }

        user.ChangePassword("new-hash", "new-stamp");

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal("new-stamp", user.SecurityStamp);
        Assert.False(user.IsLockedOut(now));
    }
}
