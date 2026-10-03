using System.Text;

namespace DbBackupManager.Application.Identity;

public static class AdminCredentialPolicy
{
    public const int MinimumUsernameLength = 3;
    public const int MaximumUsernameLength = 100;
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;
    public const int LoginFailureThreshold = 5;

    public static readonly TimeSpan LoginFailureWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static CredentialValidationResult Validate(string? username, string? password)
    {
        var failures = new List<CredentialValidationFailure>();
        var preparedUsername = PrepareUsername(username, failures);

        if (password is null
            || password.Length < MinimumPasswordLength
            || password.Length > MaximumPasswordLength
            || password.Contains('\0', StringComparison.Ordinal))
        {
            failures.Add(new CredentialValidationFailure("password", "invalid_length"));
        }

        return failures.Count == 0
            ? new CredentialValidationResult(preparedUsername, NormalizeUsername(preparedUsername!), [])
            : new CredentialValidationResult(null, null, failures);
    }

    public static CredentialValidationResult ValidateUsername(string? username)
    {
        var failures = new List<CredentialValidationFailure>();
        var preparedUsername = PrepareUsername(username, failures);

        return failures.Count == 0
            ? new CredentialValidationResult(preparedUsername, NormalizeUsername(preparedUsername!), [])
            : new CredentialValidationResult(null, null, failures);
    }

    public static IReadOnlyList<CredentialValidationFailure> ValidatePassword(string? password)
    {
        if (password is not null
            && password.Length >= MinimumPasswordLength
            && password.Length <= MaximumPasswordLength
            && !password.Contains('\0', StringComparison.Ordinal))
        {
            return [];
        }

        return [new CredentialValidationFailure("newPassword", "invalid_length")];
    }

    private static string? PrepareUsername(
        string? username,
        List<CredentialValidationFailure> failures)
    {
        var prepared = username?.Trim().Normalize(NormalizationForm.FormKC);

        if (prepared is null
            || prepared.Length < MinimumUsernameLength
            || prepared.Length > MaximumUsernameLength)
        {
            failures.Add(new CredentialValidationFailure("username", "invalid_length"));
            return null;
        }

        if (!prepared.EnumerateRunes().All(IsAllowedUsernameRune))
        {
            failures.Add(new CredentialValidationFailure("username", "invalid_characters"));
            return null;
        }

        var normalized = NormalizeUsername(prepared);
        if (normalized.Length > MaximumUsernameLength)
        {
            failures.Add(new CredentialValidationFailure("username", "invalid_length"));
            return null;
        }

        return prepared;
    }

    private static bool IsAllowedUsernameRune(Rune rune)
    {
        return Rune.IsLetterOrDigit(rune)
            || rune.Value is '.' or '_' or '-' or '@';
    }

    private static string NormalizeUsername(string username)
    {
        return username.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }
}

public sealed record CredentialValidationFailure(string Field, string Code);

public sealed record CredentialValidationResult(
    string? Username,
    string? NormalizedUsername,
    IReadOnlyList<CredentialValidationFailure> Failures)
{
    public bool IsValid => Failures.Count == 0;
}
