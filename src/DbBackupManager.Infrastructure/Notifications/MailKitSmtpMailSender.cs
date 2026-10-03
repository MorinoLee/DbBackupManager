using System.Net.Sockets;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace DbBackupManager.Infrastructure.Notifications;

internal sealed class MailKitSmtpMailSender(
    IDbContextFactory<PlatformDbContext> factory,
    IConfiguration configuration) : ISmtpMailSender
{
    public async Task<SmtpSendResult> SendAsync(
        NotificationSendWorkItem work,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var loaded = await LoadSnapshotAsync(cancellationToken);
        if (loaded.Immediate is not null)
        {
            return loaded.Immediate;
        }

        var snapshot = loaded.Snapshot!;
        try
        {
            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(snapshot.FromAddress));
            foreach (var recipient in snapshot.Recipients)
            {
                message.To.Add(MailboxAddress.Parse(recipient));
            }

            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            client.CheckCertificateRevocation = true;
            client.Timeout = checked(snapshot.TimeoutSeconds * 1000);
            var options = snapshot.SecurityMode switch
            {
                SmtpSecurityMode.TlsOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurityMode.StartTls => SecureSocketOptions.StartTls,
                SmtpSecurityMode.Plaintext => SecureSocketOptions.None,
                _ => throw new InvalidOperationException("SMTP 安全模式无效。"),
            };
            await client.ConnectAsync(snapshot.Host, snapshot.Port, options, cancellationToken);
            if (snapshot.SecurityMode == SmtpSecurityMode.Plaintext)
            {
                if (client.IsSecure)
                {
                    await client.DisconnectAsync(true, cancellationToken);
                    return SmtpSendResult.Failed("smtp_tls_failed");
                }
            }
            else if (!client.IsSecure)
            {
                await client.DisconnectAsync(true, cancellationToken);
                return SmtpSendResult.Failed("smtp_tls_failed");
            }
            if (snapshot.Username is not null && snapshot.Password is not null)
            {
                await client.AuthenticateAsync(snapshot.Username, new string(snapshot.Password), cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return SmtpSendResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Classify(exception);
        }
        finally
        {
            snapshot.Clear();
        }
    }

    private async Task<(Snapshot? Snapshot, SmtpSendResult? Immediate)> LoadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await context.SmtpSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == SmtpSettings.SingletonId, cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            return (null, SmtpSendResult.NotReady());
        }

        var recipients = await context.SmtpRecipients.AsNoTracking()
            .Where(x => x.SmtpSettingsId == settings.Id)
            .Select(x => x.Address)
            .ToArrayAsync(cancellationToken);
        if (recipients.Length == 0)
        {
            return (null, SmtpSendResult.NotReady());
        }

        string? username = null;
        char[]? password = null;
        if (settings.CredentialReferenceId is { } credentialId)
        {
            var credential = await context.CredentialReferences.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == credentialId, cancellationToken);
            if (credential is not { IsEnabled: true, Kind: CredentialKind.SmtpPassword })
            {
                return (null, SmtpSendResult.NotReady());
            }

            if (credential.ProtectionVersion != BusinessCredentialDataProtector.SmtpPasswordProtectionVersion)
            {
                return (null, SmtpSendResult.Failed("smtp_credential_unavailable"));
            }

            var protector = new BusinessCredentialDataProtector(
                configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey],
                purpose: BusinessCredentialDataProtector.SmtpPasswordPurpose);
            var decrypted = protector.UnprotectSqlPassword(credential.ProtectedSecret);
            if (decrypted.Status != BusinessCredentialProtectionStatus.Succeeded)
            {
                return (null, SmtpSendResult.Failed("smtp_credential_unavailable"));
            }

            username = credential.Username;
            password = decrypted.Secret;
        }

        return (new Snapshot(
            settings.Host,
            settings.Port,
            settings.SecurityMode,
            settings.FromAddress,
            recipients,
            username,
            password,
            settings.TimeoutSeconds), null);
    }

    private static SmtpSendResult Classify(Exception exception)
    {
        return exception switch
        {
            SslHandshakeException => SmtpSendResult.Failed("smtp_tls_failed"),
            AuthenticationException => SmtpSendResult.Failed("smtp_authentication_failed"),
            SmtpCommandException command when (int)command.StatusCode == 535 =>
                SmtpSendResult.Failed("smtp_authentication_failed"),
            SmtpCommandException command when command.ErrorCode is SmtpErrorCode.RecipientNotAccepted
                or SmtpErrorCode.SenderNotAccepted =>
                SmtpSendResult.Failed("smtp_recipient_rejected"),
            SmtpCommandException command when (int)command.StatusCode is >= 400 and < 500 =>
                SmtpSendResult.Failed("smtp_temporary_failure"),
            SmtpCommandException command when (int)command.StatusCode >= 500 =>
                SmtpSendResult.Failed("smtp_permanent_failure"),
            SmtpProtocolException or IOException => SmtpSendResult.Indeterminate("smtp_indeterminate"),
            SocketException => SmtpSendResult.Failed("smtp_temporary_failure"),
            TimeoutException => SmtpSendResult.Failed("smtp_temporary_failure"),
            System.Security.Authentication.AuthenticationException => SmtpSendResult.Failed("smtp_tls_failed"),
            NotSupportedException => SmtpSendResult.Failed("smtp_tls_failed"),
            _ => SmtpSendResult.Failed("smtp_permanent_failure"),
        };
    }

    private sealed class Snapshot(
        string host,
        int port,
        SmtpSecurityMode securityMode,
        string fromAddress,
        IReadOnlyList<string> recipients,
        string? username,
        char[]? password,
        int timeoutSeconds)
    {
        public string Host { get; } = host;
        public int Port { get; } = port;
        public SmtpSecurityMode SecurityMode { get; } = securityMode;
        public string FromAddress { get; } = fromAddress;
        public IReadOnlyList<string> Recipients { get; } = recipients;
        public string? Username { get; } = username;
        public char[]? Password { get; } = password;
        public int TimeoutSeconds { get; } = timeoutSeconds;

        public void Clear()
        {
            if (Password is not null)
            {
                Array.Clear(Password);
            }
        }
    }
}
