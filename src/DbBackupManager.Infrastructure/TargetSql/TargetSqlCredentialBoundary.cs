using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using DbBackupManager.Application.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlCredentialResolver
{
    ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
        Guid credentialReferenceId,
        CancellationToken cancellationToken);
}

internal sealed class TargetSqlCredentialResolution : IDisposable
{
    private TargetSqlCredentialResolution(
        TargetSqlCredentialLease? credential,
        TargetSqlFailureCode? failureCode)
    {
        if ((credential is null) == (failureCode is null))
        {
            throw new ArgumentException("凭据解析结果必须且只能包含凭据或失败码之一。");
        }

        if (failureCode is not null
            and not TargetSqlFailureCode.CredentialUnavailable
            and not TargetSqlFailureCode.CredentialInvalid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode),
                failureCode,
                "凭据解析只能返回凭据不可用或凭据无效。");
        }

        Credential = credential;
        FailureCode = failureCode;
    }

    public TargetSqlCredentialLease? Credential { get; }

    public TargetSqlFailureCode? FailureCode { get; }

    public static TargetSqlCredentialResolution Succeeded(TargetSqlCredentialLease credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new TargetSqlCredentialResolution(credential, null);
    }

    public static TargetSqlCredentialResolution Failed(TargetSqlFailureCode failureCode)
    {
        return new TargetSqlCredentialResolution(null, failureCode);
    }

    public void Dispose()
    {
        Credential?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class TargetSqlCredentialLease : IDisposable
{
    private SecureString? _password;

    private TargetSqlCredentialLease(string userName, SecureString password)
    {
        UserName = userName;
        _password = password;
    }

    public string UserName { get; }

    public static TargetSqlCredentialLease CreateAndClear(string userName, char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);

        SecureString? securePassword = null;
        try
        {
            var normalizedUserName = userName?.Trim();
            if (string.IsNullOrEmpty(normalizedUserName)
                || normalizedUserName.Length > 128
                || normalizedUserName.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "SQL 登录名不能为空、不能包含控制字符且长度不能超过 128。",
                    nameof(userName));
            }

            if (password.Length is 0 or > 128)
            {
                throw new ArgumentException(
                    "SQL 密码不能为空且长度不能超过 128。",
                    nameof(password));
            }

            securePassword = new SecureString();
            foreach (var character in password)
            {
                securePassword.AppendChar(character);
            }

            securePassword.MakeReadOnly();
            var credential = new TargetSqlCredentialLease(normalizedUserName, securePassword);
            securePassword = null;
            return credential;
        }
        finally
        {
            securePassword?.Dispose();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        }
    }

    public SqlCredential CreateSqlCredential()
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        return new SqlCredential(UserName, _password);
    }

    public void Dispose()
    {
        _password?.Dispose();
        _password = null;
        GC.SuppressFinalize(this);
    }
}
