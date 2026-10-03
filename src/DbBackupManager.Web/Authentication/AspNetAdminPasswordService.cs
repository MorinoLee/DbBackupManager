using System.Security.Cryptography;
using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DbBackupManager.Web.Authentication;

internal sealed class AspNetAdminPasswordService : IAdminPasswordService
{
    private readonly object _passwordSubject = new();
    private readonly PasswordHasher<object> _passwordHasher;
    private readonly string _dummyPasswordHash;

    public AspNetAdminPasswordService(IOptions<PasswordHasherOptions> options)
    {
        _passwordHasher = new PasswordHasher<object>(options);
        _dummyPasswordHash = _passwordHasher.HashPassword(
            _passwordSubject,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string HashPassword(string password)
    {
        return _passwordHasher.HashPassword(_passwordSubject, password);
    }

    public PasswordVerificationStatus VerifyPassword(string? passwordHash, string providedPassword)
    {
        var result = _passwordHasher.VerifyHashedPassword(
            _passwordSubject,
            passwordHash ?? _dummyPasswordHash,
            providedPassword);

        return result switch
        {
            PasswordVerificationResult.Success => PasswordVerificationStatus.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded =>
                PasswordVerificationStatus.SucceededRehashNeeded,
            _ => PasswordVerificationStatus.Failed,
        };
    }
}
