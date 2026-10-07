using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupSets;

public enum BackupCompletionTimeSource
{
    PlatformObserved = 1,
    SqlLocalTime = 2,
    Unknown = 3
}

public static class BackupCompletionTimeReason
{
    public const string PlatformObserved = "completion.platform_observed";
    public const string SqlLocalConverted = "completion.sql_local_converted";
    public const string SqlFinishMissing = "completion.sql_finish_missing";
    public const string ServerOffsetUnknown = "completion.server_offset_unknown";
    public const string ServerTimeZoneUnknown = "completion.server_time_zone_unknown";
    public const string AmbiguousLocalTime = "completion.ambiguous_local_time";
    public const string InvalidLocalTime = "completion.invalid_local_time";
    public const string ServerOffsetMismatch = "completion.server_offset_mismatch";
    public const string OlderThan24Hours = "completion.older_than_24_hours";
    public const string FutureSqlTime = "completion.future_sql_time";
    public const string ConversionOutOfRange = "completion.conversion_out_of_range";
}

public sealed record BackupCompletionTimeDecision(
    DateTimeOffset? CompletedAtUtc,
    BackupCompletionTimeSource Source,
    string ReasonCode);

public static class BackupCompletionTimeRules
{
    public static BackupCompletionTimeDecision Evaluate(
        DateTimeOffset? platformObservedCompletionUtc,
        DateTime? sqlFinishLocal,
        DateTimeOffset nowUtc,
        TimeSpan? currentServerOffset,
        TimeZoneInfo? serverTimeZone)
    {
        ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc));
        if (platformObservedCompletionUtc is { } observed)
        {
            ConfigurationValues.RequireUtc(observed, nameof(platformObservedCompletionUtc));
            // 时钟回拨可能让持久化完成时刻晚于 now，仍保留平台已观察到的事实。
            return new(observed, BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved);
        }

        if (sqlFinishLocal is not { } local)
        {
            return Unknown(BackupCompletionTimeReason.SqlFinishMissing);
        }

        if (local.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("SQL 本地时间必须保留为未指定时区的原值。", nameof(sqlFinishLocal));
        }

        if (currentServerOffset is not { } offset)
        {
            return Unknown(BackupCompletionTimeReason.ServerOffsetUnknown);
        }

        if (serverTimeZone is null)
        {
            return Unknown(BackupCompletionTimeReason.ServerTimeZoneUnknown);
        }

        if (serverTimeZone.IsAmbiguousTime(local))
        {
            return Unknown(BackupCompletionTimeReason.AmbiguousLocalTime);
        }

        if (serverTimeZone.IsInvalidTime(local))
        {
            return Unknown(BackupCompletionTimeReason.InvalidLocalTime);
        }

        // 当前偏移不能冒充跨夏令时切换之前的偏移；时区规则由适配层提供，不使用 Worker 的本机时区。
        if (offset != serverTimeZone.GetUtcOffset(nowUtc) || offset != serverTimeZone.GetUtcOffset(local))
        {
            return Unknown(BackupCompletionTimeReason.ServerOffsetMismatch);
        }

        DateTimeOffset completed;
        try
        {
            completed = new DateTimeOffset(local, offset).ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return Unknown(BackupCompletionTimeReason.ConversionOutOfRange);
        }

        var age = nowUtc - completed;
        if (age < TimeSpan.Zero)
        {
            return Unknown(BackupCompletionTimeReason.FutureSqlTime);
        }

        if (age > TimeSpan.FromHours(24))
        {
            return Unknown(BackupCompletionTimeReason.OlderThan24Hours);
        }

        return new(completed, BackupCompletionTimeSource.SqlLocalTime, BackupCompletionTimeReason.SqlLocalConverted);
    }

    private static BackupCompletionTimeDecision Unknown(string reason) => new(null, BackupCompletionTimeSource.Unknown, reason);
}
