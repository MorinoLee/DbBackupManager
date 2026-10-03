namespace DbBackupManager.Domain.Entities;

public sealed class AuditRecord
{
    private AuditRecord()
    {
    }

    public AuditRecord(
        Guid? actorAdminUserId,
        string action,
        string targetType,
        string? targetId,
        string result,
        string? reasonCode)
    {
        if (actorAdminUserId == Guid.Empty)
        {
            throw new ArgumentException("行为主体标识不能是空 Guid。", nameof(actorAdminUserId));
        }

        ActorAdminUserId = actorAdminUserId;
        Action = RequireValue(action, 100, nameof(action));
        TargetType = RequireValue(targetType, 100, nameof(targetType));
        TargetId = OptionalValue(targetId, 100, nameof(targetId));
        Result = RequireValue(result, 30, nameof(result));
        ReasonCode = OptionalValue(reasonCode, 100, nameof(reasonCode));
    }

    public long Id { get; private set; }

    public Guid? ActorAdminUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    public string Result { get; private set; } = string.Empty;

    public string? ReasonCode { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    private static string RequireValue(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        return value.Length <= maximumLength
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                $"值的长度不能超过 {maximumLength} 个字符。");
    }

    private static string? OptionalValue(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maximumLength
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                $"值的长度不能超过 {maximumLength} 个字符。");
    }
}
