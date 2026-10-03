namespace DbBackupManager.Application.Identity;

public interface IAdminPasswordService
{
    string HashPassword(string password);

    PasswordVerificationStatus VerifyPassword(string? passwordHash, string providedPassword);
}
