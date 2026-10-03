using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.Tests;

public sealed class AdminIdentityServiceTests
{
    [Fact]
    public void CredentialPolicyNormalizesUnicodeAndCase()
    {
        var result = AdminCredentialPolicy.Validate("  Ａdmin-1  ", "synthetic-password");

        Assert.True(result.IsValid);
        Assert.Equal("Admin-1", result.Username);
        Assert.Equal("ADMIN-1", result.NormalizedUsername);
    }

    [Theory]
    [InlineData("ab", "synthetic-password", "username")]
    [InlineData("valid-user", "too-short", "password")]
    [InlineData("invalid user", "synthetic-password", "username")]
    public void CredentialPolicyRejectsInvalidInput(string username, string password, string field)
    {
        var result = AdminCredentialPolicy.Validate(username, password);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure => failure.Field == field);
    }

    [Fact]
    public async Task SetupHashesPasswordAndReturnsSession()
    {
        var store = new FakeAdminIdentityStore();
        var service = CreateService(store);

        var result = await service.SetupAsync("Admin-1", "synthetic-password");

        Assert.True(result.IsSucceeded);
        Assert.NotNull(result.Session);
        Assert.Equal("hash:synthetic-password", store.Credential?.PasswordHash);
        Assert.Equal(64, result.Session.SecurityStamp.Length);
    }

    [Fact]
    public async Task UnknownUsernameUsesGenericFailureAndDummyVerification()
    {
        var store = new FakeAdminIdentityStore();
        var passwords = new FakeAdminPasswordService();
        var service = new AdminIdentityService(store, passwords, TimeProvider.System);

        var result = await service.AuthenticateAsync("unknown", "synthetic-password");

        Assert.Equal(AdminIdentityResultCode.InvalidCredentials, result.Code);
        Assert.True(passwords.VerifiedDummyHash);
        Assert.Equal("invalid_credentials", store.LastRejectedReason);
    }

    [Fact]
    public async Task SuccessfulLoginClearsFailureStateAndReturnsExistingStamp()
    {
        var store = new FakeAdminIdentityStore
        {
            Credential = CreateCredential(),
        };
        var service = CreateService(store);

        var result = await service.AuthenticateAsync("Admin-1", "synthetic-password");

        Assert.True(result.IsSucceeded);
        Assert.True(store.SuccessfulLoginRecorded);
        Assert.Equal(store.Credential.SecurityStamp, result.Session?.SecurityStamp);
    }

    [Fact]
    public async Task SessionValidationFailsClosedWhenSecurityStampChanges()
    {
        var credential = CreateCredential();
        var store = new FakeAdminIdentityStore { Credential = credential };
        var service = CreateService(store);

        var result = await service.ValidateSessionAsync(credential.Id, "different-stamp");

        Assert.Equal(AdminIdentityResultCode.AuthenticationRequired, result.Code);
    }

    [Fact]
    public async Task ChangePasswordReplacesHashAndSecurityStamp()
    {
        var credential = CreateCredential();
        var store = new FakeAdminIdentityStore { Credential = credential };
        var service = CreateService(store);

        var result = await service.ChangePasswordAsync(
            credential.Id,
            credential.SecurityStamp,
            "synthetic-password",
            "replacement-password");

        Assert.True(result.IsSucceeded);
        Assert.Equal("hash:replacement-password", store.ChangedPasswordHash);
        Assert.NotEqual(credential.SecurityStamp, store.ChangedSecurityStamp);
    }

    private static AdminIdentityService CreateService(FakeAdminIdentityStore store)
    {
        return new AdminIdentityService(store, new FakeAdminPasswordService(), TimeProvider.System);
    }

    private static AdminCredentialSnapshot CreateCredential()
    {
        return new AdminCredentialSnapshot(
            Guid.NewGuid(),
            "Admin-1",
            "ADMIN-1",
            "hash:synthetic-password",
            "synthetic-security-stamp",
            true,
            0,
            null,
            null,
            [1]);
    }

    private sealed class FakeAdminPasswordService : IAdminPasswordService
    {
        public bool VerifiedDummyHash { get; private set; }

        public string HashPassword(string password)
        {
            return $"hash:{password}";
        }

        public PasswordVerificationStatus VerifyPassword(string? passwordHash, string providedPassword)
        {
            if (passwordHash is null)
            {
                VerifiedDummyHash = true;
                return PasswordVerificationStatus.Failed;
            }

            return passwordHash == $"hash:{providedPassword}"
                ? PasswordVerificationStatus.Succeeded
                : PasswordVerificationStatus.Failed;
        }
    }

    private sealed class FakeAdminIdentityStore : IAdminIdentityStore
    {
        public AdminCredentialSnapshot? Credential { get; set; }

        public bool SuccessfulLoginRecorded { get; private set; }

        public string? LastRejectedReason { get; private set; }

        public string? ChangedPasswordHash { get; private set; }

        public string? ChangedSecurityStamp { get; private set; }

        public Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Credential is null);
        }

        public Task<AdminStoreResult> TryCreateFirstAdminAsync(
            NewAdminCredential credential,
            CancellationToken cancellationToken = default)
        {
            if (Credential is not null)
            {
                return Task.FromResult(AdminStoreResult.SetupUnavailable);
            }

            Credential = new AdminCredentialSnapshot(
                credential.Id,
                credential.Username,
                credential.NormalizedUsername,
                credential.PasswordHash,
                credential.SecurityStamp,
                true,
                0,
                null,
                null,
                [1]);
            return Task.FromResult(AdminStoreResult.Succeeded);
        }

        public Task<AdminCredentialSnapshot?> FindByNormalizedUsernameAsync(
            string normalizedUsername,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Credential?.NormalizedUsername == normalizedUsername ? Credential : null);
        }

        public Task<AdminCredentialSnapshot?> FindByIdAsync(
            Guid adminUserId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Credential?.Id == adminUserId ? Credential : null);
        }

        public Task<AdminStoreResult> RecordFailedLoginAsync(
            AdminCredentialSnapshot credential,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AdminStoreResult.Succeeded);
        }

        public Task<AdminStoreResult> RecordSuccessfulLoginAsync(
            AdminCredentialSnapshot credential,
            string? replacementPasswordHash,
            CancellationToken cancellationToken = default)
        {
            SuccessfulLoginRecorded = true;
            return Task.FromResult(AdminStoreResult.Succeeded);
        }

        public Task<AdminStoreResult> ChangePasswordAsync(
            AdminCredentialSnapshot credential,
            string passwordHash,
            string securityStamp,
            CancellationToken cancellationToken = default)
        {
            ChangedPasswordHash = passwordHash;
            ChangedSecurityStamp = securityStamp;
            return Task.FromResult(AdminStoreResult.Succeeded);
        }

        public Task RecordRejectedAuthenticationAsync(
            Guid? actorAdminUserId,
            string action,
            string reasonCode,
            CancellationToken cancellationToken = default)
        {
            LastRejectedReason = reasonCode;
            return Task.CompletedTask;
        }

        public Task RecordCsrfFailureAsync(
            Guid? actorAdminUserId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
