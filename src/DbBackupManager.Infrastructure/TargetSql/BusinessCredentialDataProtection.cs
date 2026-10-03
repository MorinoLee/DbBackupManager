using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace DbBackupManager.Infrastructure.TargetSql;

internal enum BusinessCredentialProtectionStatus
{
    Succeeded,
    Unavailable,
    Invalid,
}

internal sealed class BusinessCredentialUnprotectResult
{
    private BusinessCredentialUnprotectResult(
        BusinessCredentialProtectionStatus status,
        char[]? secret)
    {
        if ((status == BusinessCredentialProtectionStatus.Succeeded) != (secret is not null))
        {
            throw new ArgumentException("成功解密结果必须且只能包含秘密字符缓冲区。", nameof(secret));
        }

        Status = status;
        Secret = secret;
    }

    public BusinessCredentialProtectionStatus Status { get; }

    public char[]? Secret { get; }

    public static BusinessCredentialUnprotectResult Succeeded(char[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return new BusinessCredentialUnprotectResult(
            BusinessCredentialProtectionStatus.Succeeded,
            secret);
    }

    public static BusinessCredentialUnprotectResult Failed(
        BusinessCredentialProtectionStatus status)
    {
        if (status == BusinessCredentialProtectionStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new BusinessCredentialUnprotectResult(status, null);
    }
}

internal interface IBusinessCredentialProtector
{
    BusinessCredentialUnprotectResult UnprotectSqlPassword(string protectedSecret);
}

internal sealed class BusinessCredentialDataProtector : IBusinessCredentialProtector
{
    public const string KeyRingPathConfigurationKey =
        "DataProtection:BusinessCredentialKeyRingPath";

    public const string ApplicationName = "DbBackupManager.BusinessCredentials.v1";

    public const string SqlPasswordPurpose = "SqlPassword.v1";

    public const string SqlPasswordProtectionVersion = "dp-sql-password-v1";

    public const string SmtpPasswordPurpose = "SmtpPassword.v1";

    public const string SmtpPasswordProtectionVersion = "dp-smtp-password-v1";

    private readonly string? _keyRingPath;
    private readonly string _applicationName;
    private readonly string _purpose;
    private readonly object _syncRoot = new();
    private IDataProtector? _protector;

    public BusinessCredentialDataProtector(
        string? keyRingPath,
        string applicationName = ApplicationName,
        string purpose = SqlPasswordPurpose)
    {
        _keyRingPath = NormalizeKeyRingPath(keyRingPath);
        _applicationName = RequireStableValue(applicationName, nameof(applicationName));
        _purpose = RequireStableValue(purpose, nameof(purpose));
    }

    public BusinessCredentialUnprotectResult UnprotectSqlPassword(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return BusinessCredentialUnprotectResult.Failed(
                BusinessCredentialProtectionStatus.Invalid);
        }

        byte[]? protectedBytes = null;
        byte[]? clearBytes = null;
        try
        {
            var protector = GetProtector();
            if (protector is null)
            {
                return BusinessCredentialUnprotectResult.Failed(
                    BusinessCredentialProtectionStatus.Unavailable);
            }

            protectedBytes = Convert.FromBase64String(protectedSecret);
            clearBytes = protector.Unprotect(protectedBytes);
            var secret = Encoding.UTF8.GetChars(clearBytes);
            return secret.Length > 0
                ? BusinessCredentialUnprotectResult.Succeeded(secret)
                : BusinessCredentialUnprotectResult.Failed(
                    BusinessCredentialProtectionStatus.Invalid);
        }
        catch (FormatException)
        {
            return BusinessCredentialUnprotectResult.Failed(
                BusinessCredentialProtectionStatus.Invalid);
        }
        catch (CryptographicException)
        {
            return BusinessCredentialUnprotectResult.Failed(
                BusinessCredentialProtectionStatus.Invalid);
        }
        catch (IOException)
        {
            return BusinessCredentialUnprotectResult.Failed(
                BusinessCredentialProtectionStatus.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return BusinessCredentialUnprotectResult.Failed(
                BusinessCredentialProtectionStatus.Unavailable);
        }
        finally
        {
            Clear(protectedBytes);
            Clear(clearBytes);
        }
    }

    internal string ProtectSqlPassword(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty || secret.Length > 128)
        {
            throw new ArgumentException("SQL 密码不能为空且长度不能超过 128。", nameof(secret));
        }

        return ProtectSecret(secret);
    }

    internal string ProtectFilePassword(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty || secret.Length > 1024)
        {
            throw new ArgumentException("文件访问密码长度必须在 1 到 1024 之间。", nameof(secret));
        }

        return ProtectSecret(secret);
    }

    internal string ProtectSmtpPassword(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty || secret.Length > 1024)
        {
            throw new ArgumentException("SMTP 密码长度必须在 1 到 1024 之间。", nameof(secret));
        }

        return ProtectSecret(secret);
    }

    private string ProtectSecret(ReadOnlySpan<char> secret)
    {
        var protector = GetProtector()
            ?? throw new InvalidOperationException(
                $"缺少或无效的 {KeyRingPathConfigurationKey} 安全配置。");
        byte[]? clearBytes = null;
        byte[]? protectedBytes = null;
        try
        {
            clearBytes = new byte[Encoding.UTF8.GetByteCount(secret)];
            Encoding.UTF8.GetBytes(secret, clearBytes);
            protectedBytes = protector.Protect(clearBytes);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            Clear(clearBytes);
            Clear(protectedBytes);
        }
    }

    private IDataProtector? GetProtector()
    {
        if (_keyRingPath is null)
        {
            return null;
        }

        if (_protector is not null)
        {
            return _protector;
        }

        lock (_syncRoot)
        {
            if (_protector is not null)
            {
                return _protector;
            }

            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(_keyRingPath),
                builder => builder.SetApplicationName(_applicationName));
            _protector = provider.CreateProtector(_purpose);
            return _protector;
        }
    }

    private static string? NormalizeKeyRingPath(string? keyRingPath)
    {
        if (string.IsNullOrWhiteSpace(keyRingPath))
        {
            return null;
        }

        try
        {
            var normalized = keyRingPath.Trim();
            return Path.IsPathFullyQualified(normalized)
                ? Path.GetFullPath(normalized)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string RequireStableValue(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Data Protection 隔离值不能为空或包含控制字符。", parameterName);
        }

        return normalized;
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
