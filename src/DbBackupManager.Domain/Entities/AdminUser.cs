namespace DbBackupManager.Domain.Entities;

public sealed class AdminUser : ConcurrentEntity
{
    private AdminUser()
    {
    }

    public AdminUser(
        Guid id,
        string username,
        string normalizedUsername,
        string passwordHash,
        string securityStamp)
        : base(id)
    {
        Username = RequireValue(username, 100, nameof(username));
        NormalizedUsername = RequireValue(normalizedUsername, 100, nameof(normalizedUsername));
        PasswordHash = RequireValue(passwordHash, 500, nameof(passwordHash));
        SecurityStamp = RequireValue(securityStamp, 64, nameof(securityStamp));
        IsEnabled = true;
    }

    public string Username { get; private set; } = string.Empty;

    public string NormalizedUsername { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string SecurityStamp { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? FailedLoginWindowStartedAtUtc { get; private set; }

    public DateTimeOffset? LockoutEndUtc { get; private set; }

    public bool IsLockedOut(DateTimeOffset utcNow)
    {
        return LockoutEndUtc is not null && LockoutEndUtc > utcNow;
    }

    public void RecordFailedLogin(
        DateTimeOffset utcNow,
        TimeSpan failureWindow,
        int failureThreshold,
        TimeSpan lockoutDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(failureWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockoutDuration, TimeSpan.Zero);

        if (FailedLoginWindowStartedAtUtc is null
            || utcNow < FailedLoginWindowStartedAtUtc
            || utcNow - FailedLoginWindowStartedAtUtc >= failureWindow)
        {
            FailedLoginWindowStartedAtUtc = utcNow;
            FailedLoginCount = 1;
        }
        else
        {
            FailedLoginCount++;
        }

        if (FailedLoginCount >= failureThreshold)
        {
            LockoutEndUtc = utcNow.Add(lockoutDuration);
            FailedLoginCount = 0;
            FailedLoginWindowStartedAtUtc = null;
        }
    }

    public void RecordSuccessfulLogin(string? replacementPasswordHash = null)
    {
        if (replacementPasswordHash is not null)
        {
            PasswordHash = RequireValue(replacementPasswordHash, 500, nameof(replacementPasswordHash));
        }

        ResetLoginFailureState();
    }

    public void ChangePassword(string passwordHash, string securityStamp)
    {
        PasswordHash = RequireValue(passwordHash, 500, nameof(passwordHash));
        SecurityStamp = RequireValue(securityStamp, 64, nameof(securityStamp));
        ResetLoginFailureState();
    }

    private void ResetLoginFailureState()
    {
        FailedLoginCount = 0;
        FailedLoginWindowStartedAtUtc = null;
        LockoutEndUtc = null;
    }

    private static string RequireValue(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"值的长度不能超过 {maximumLength} 个字符。");
        }

        return value;
    }
}
