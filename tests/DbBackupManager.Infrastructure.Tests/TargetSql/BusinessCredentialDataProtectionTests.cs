using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class BusinessCredentialDataProtectionTests : IDisposable
{
    private const string TestDirectoryPrefix = "DbBackupManagerP56Keys_";
    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"{TestDirectoryPrefix}{Guid.NewGuid():N}");

    [Fact]
    public void SameApplicationAndPurposeCanRoundTripAcrossProtectorInstances()
    {
        var writer = new BusinessCredentialDataProtector(_keyRingPath);
        var protectedSecret = writer.ProtectSqlPassword("synthetic-password".AsSpan());
        var reader = new BusinessCredentialDataProtector(_keyRingPath);

        var result = reader.UnprotectSqlPassword(protectedSecret);

        Assert.Equal(BusinessCredentialProtectionStatus.Succeeded, result.Status);
        Assert.Equal("synthetic-password", new string(result.Secret!));
        Clear(result.Secret);
    }

    [Fact]
    public void DifferentPurposeCannotUnprotectSqlPasswordPayload()
    {
        var writer = new BusinessCredentialDataProtector(_keyRingPath);
        var protectedSecret = writer.ProtectSqlPassword("synthetic-password".AsSpan());
        var reader = new BusinessCredentialDataProtector(
            _keyRingPath,
            BusinessCredentialDataProtector.ApplicationName,
            "DifferentCredentialPurpose.v1");

        var result = reader.UnprotectSqlPassword(protectedSecret);

        Assert.Equal(BusinessCredentialProtectionStatus.Invalid, result.Status);
        Assert.Null(result.Secret);
    }

    [Fact]
    public void MissingOrRelativeKeyRingFailsClosedWithoutCreatingAKeyRing()
    {
        var missing = new BusinessCredentialDataProtector(null);
        var relative = new BusinessCredentialDataProtector("relative-key-ring");

        var missingResult = missing.UnprotectSqlPassword("synthetic-payload");
        var relativeResult = relative.UnprotectSqlPassword("synthetic-payload");

        Assert.Equal(BusinessCredentialProtectionStatus.Unavailable, missingResult.Status);
        Assert.Equal(BusinessCredentialProtectionStatus.Unavailable, relativeResult.Status);
        Assert.False(Directory.Exists("relative-key-ring"));
    }

    [Fact]
    public void MalformedCiphertextIsInvalidAndDoesNotExposeRawFailure()
    {
        var protector = new BusinessCredentialDataProtector(_keyRingPath);

        var result = protector.UnprotectSqlPassword("not-base64");

        Assert.Equal(BusinessCredentialProtectionStatus.Invalid, result.Status);
        Assert.Null(result.Secret);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_keyRingPath))
        {
            return;
        }

        var directoryName = Path.GetFileName(_keyRingPath);
        if (!directoryName.StartsWith(TestDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理不属于 P5.6 的临时 Key Ring。 ");
        }

        Directory.Delete(_keyRingPath, recursive: true);
    }

    private static void Clear(char[]? secret)
    {
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secret.AsSpan()));
        }
    }
}
