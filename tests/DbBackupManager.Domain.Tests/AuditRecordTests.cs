using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Tests;

public sealed class AuditRecordTests
{
    [Fact]
    public void ConstructorStoresOnlyStructuredAuditFields()
    {
        var actorId = Guid.NewGuid();

        var record = new AuditRecord(
            actorId,
            "admin.disabled",
            "AdminUser",
            Guid.NewGuid().ToString("N"),
            "succeeded",
            "manual_action");

        Assert.Equal(actorId, record.ActorAdminUserId);
        Assert.Equal("admin.disabled", record.Action);
        Assert.Equal("succeeded", record.Result);
        Assert.Equal(default, record.OccurredAtUtc);
    }

    [Fact]
    public void ConstructorRejectsEmptyActorGuid()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditRecord(Guid.Empty, "admin.disabled", "AdminUser", null, "denied", null));
    }
}
