using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class ApplicationPortBoundaryTests
{
    [Fact]
    public void PublicPortsDoNotExposePersistenceTransportOrStreamingTypes()
    {
        string[] forbiddenFragments =
        [
            "EntityFrameworkCore",
            "IQueryable",
            "PlatformDbContext",
            "SqlClient",
            "Renci.SshNet",
            "MailKit",
            "Hub",
            "Stream",
        ];
        var ports = typeof(IBackupTaskExecutionStore).Assembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .ToArray();
        Assert.NotEmpty(ports);

        var violations = ports
            .SelectMany(port => port.GetInterfaces().Append(port)
                .SelectMany(type => type.GetMethods())
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .Select(type => new { Port = port, Method = method, TypeName = type.FullName ?? type.Name })))
            .Where(signature => forbiddenFragments.Any(fragment =>
                signature.TypeName.Contains(fragment, StringComparison.Ordinal)))
            .Select(signature => $"{signature.Port.FullName}.{signature.Method.Name}: {signature.TypeName}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }
}
