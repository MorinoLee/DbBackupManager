using System.ComponentModel;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Infrastructure.FileStorage;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class SmbNativeFailureTests
{
    [Theory]
    [InlineData(86)]
    [InlineData(1244)]
    [InlineData(1326)]
    [InlineData(1327)]
    [InlineData(1385)]
    [InlineData(1219)]
    public void LogonFailureHresultIsAuthenticationFailed(int native)
    {
        var exception = new IOException("synthetic", unchecked((int)(0x80070000 | native)));

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.AuthenticationFailed, translated.Code);
        Assert.Equal(BackupFileStorageFailurePhase.SourceInspection, translated.Phase);
        Assert.Equal(nameof(BackupFileStorageFailureCode.AuthenticationFailed), translated.Message);
        Assert.False(translated.Indeterminate);
    }

    [Fact]
    public void InnerWin32LogonFailureIsAuthenticationFailed()
    {
        var exception = new IOException("synthetic", new Win32Exception(1326));

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.AuthenticationFailed, translated.Code);
    }

    [Fact]
    public void AccessDeniedIsAuthorizationDenied()
    {
        var exception = new UnauthorizedAccessException("synthetic")
        {
            HResult = unchecked((int)0x80070005),
        };

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.AuthorizationDenied, translated.Code);
    }

    [Fact]
    public void NetworkPathHresultIsConnectionFailed()
    {
        var exception = new IOException("synthetic", unchecked((int)0x80070035));

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.ConnectionFailed, translated.Code);
    }

    [Fact]
    public void NtStatusLogonFailureIsAuthenticationFailed()
    {
        var exception = new IOException("synthetic", unchecked((int)0xC000006D));

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.AuthenticationFailed, translated.Code);
    }

    [Fact]
    public void SecurityLogonDeniedIsAuthenticationFailed()
    {
        var exception = new IOException("synthetic", unchecked((int)0x8009030C));

        var translated = SmbNativeFailure.Translate(
            exception,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Equal(BackupFileStorageFailureCode.AuthenticationFailed, translated.Code);
    }

    [Fact]
    public void ExistingAdapterExceptionIsReturnedUnchanged()
    {
        var original = new FileStorageAdapterException(
            BackupFileStorageFailureCode.PathRejected,
            BackupFileStorageFailurePhase.RequestValidation);

        var translated = SmbNativeFailure.Translate(
            original,
            BackupFileStorageFailurePhase.SourceInspection);

        Assert.Same(original, translated);
    }
}
