namespace DbBackupManager.Domain.Entities;

public abstract class ConcurrentEntity
{
    protected ConcurrentEntity()
    {
    }

    protected ConcurrentEntity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("实体标识不能为空。", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
}
