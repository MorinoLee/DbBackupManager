using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DbBackupManager.ArchitectureTests;

public sealed class ProjectDependencyTests
{
    private const string TargetFramework = "net10.0";

    private static readonly IReadOnlyDictionary<string, ProjectPolicy> ExpectedPolicies =
        new Dictionary<string, ProjectPolicy>(StringComparer.Ordinal)
        {
            ["DbBackupManager.Domain"] = new(
                ["Microsoft.NET.Sdk"],
                [],
                [],
                ["Microsoft.NETCore.App"]),
            ["DbBackupManager.Application"] = new(
                ["Microsoft.NET.Sdk"],
                ["DbBackupManager.Domain"],
                [],
                ["Microsoft.NETCore.App"]),
            ["DbBackupManager.Infrastructure"] = new(
                ["Microsoft.NET.Sdk"],
                ["DbBackupManager.Application", "DbBackupManager.Domain"],
                ["MailKit", "Microsoft.AspNetCore.DataProtection.Extensions", "Microsoft.Data.SqlClient", "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.Extensions.Configuration.Json", "SSH.NET"],
                ["Microsoft.NETCore.App"]),
            ["DbBackupManager.Contracts"] = new(
                ["Microsoft.NET.Sdk"],
                [],
                [],
                ["Microsoft.NETCore.App"]),
            ["DbBackupManager.Web"] = new(
                ["Microsoft.NET.Sdk.Web"],
                ["DbBackupManager.Application", "DbBackupManager.Contracts", "DbBackupManager.Infrastructure"],
                ["Microsoft.AspNetCore.OpenApi", "Microsoft.EntityFrameworkCore.Design", "Microsoft.Extensions.ApiDescription.Server", "Microsoft.Extensions.Hosting.WindowsServices", "MudBlazor"],
                ["Microsoft.AspNetCore.App", "Microsoft.NETCore.App"]),
            ["DbBackupManager.Worker"] = new(
                ["Microsoft.NET.Sdk.Worker"],
                ["DbBackupManager.Application", "DbBackupManager.Infrastructure"],
                ["Microsoft.Extensions.Hosting", "Microsoft.Extensions.Hosting.WindowsServices"],
                ["Microsoft.NETCore.App"]),
        };

    [Fact]
    public void ProductionProjectSetMatchesArchitectureBaseline()
    {
        var actualProjects = GetProductionProjects().Keys.Order(StringComparer.Ordinal).ToArray();
        var expectedProjects = ExpectedPolicies.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedProjects, actualProjects);
    }

    [Fact]
    public void ProductSolutionDoesNotDependOnToolProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var toolsRoot = Path.Combine(repositoryRoot, "tools");
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "DbBackupManager.slnx"));

        foreach (var project in solution.Descendants("Project"))
        {
            var projectPath = Path.GetFullPath(project.Attribute("Path")!.Value, repositoryRoot);
            Assert.False(IsUnderDirectory(projectPath, toolsRoot), $"产品 Solution 不得收录工具项目：{projectPath}");

            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "obj", "project.assets.json")));
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "project")
                {
                    continue;
                }

                var dependencyPath = Path.GetFullPath(library.Value.GetProperty("path").GetString()!, projectDirectory);
                Assert.False(
                    IsUnderDirectory(dependencyPath, toolsRoot),
                    $"产品 Solution 中的 {Path.GetFileName(projectPath)} 不得直接或间接依赖工具项目：{dependencyPath}");
            }
        }
    }

    [Theory]
    [InlineData("DbBackupManager.Domain")]
    [InlineData("DbBackupManager.Application")]
    [InlineData("DbBackupManager.Infrastructure")]
    [InlineData("DbBackupManager.Contracts")]
    [InlineData("DbBackupManager.Web")]
    [InlineData("DbBackupManager.Worker")]
    public void EvaluatedProjectDependenciesMatchArchitectureBaseline(string projectName)
    {
        var projectPath = GetProductionProjects()[projectName];
        var snapshot = ReadProjectSnapshot(projectName, projectPath);
        var policy = ExpectedPolicies[projectName];

        Assert.Equal([TargetFramework], snapshot.TargetFrameworks);
        Assert.Equal(policy.Sdks, snapshot.Sdks);
        Assert.True(
            snapshot.CentralPackageManagementEnabled,
            $"生产项目 {projectName} 必须启用中央包版本管理。");
        Assert.True(
            snapshot.CentralPackageVersionOverrideDisabled,
            $"生产项目 {projectName} 必须禁止项目级包版本覆盖。");
        Assert.Equal(policy.ProjectReferences, snapshot.ProjectReferences);
        Assert.Equal(policy.PackageReferences, snapshot.PackageReferences);
        Assert.Equal(policy.FrameworkReferences, snapshot.FrameworkReferences);
    }

    private static Dictionary<string, string> GetProductionProjects()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");

        return Directory
            .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                projectPath => Path.GetFileNameWithoutExtension(projectPath),
                projectPath => projectPath,
                StringComparer.Ordinal);
    }

    private static ProjectSnapshot ReadProjectSnapshot(string projectName, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("无法确定项目目录。");
        var dependencySpecPath = Path.Combine(
            projectDirectory,
            "obj",
            $"{Path.GetFileName(projectPath)}.nuget.dgspec.json");

        if (!File.Exists(dependencySpecPath))
        {
            throw new InvalidOperationException("缺少 NuGet 还原规格，请先还原整个 Solution 后再运行架构测试。");
        }

        using var stream = File.OpenRead(dependencySpecPath);
        using var document = JsonDocument.Parse(stream);
        var projectSpec = FindProjectSpec(document.RootElement, projectPath);
        var restore = projectSpec.GetProperty("restore");
        var restoreFrameworks = restore.GetProperty("frameworks");
        var targetFrameworks = GetPropertyNames(restoreFrameworks);
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var projectReferences = GetPropertyNames(
                restoreFrameworks.GetProperty(TargetFramework).GetProperty("projectReferences"))
            .Select(reference => MapProductionReference(projectName, reference, projectDirectory, sourceRoot))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var framework = projectSpec.GetProperty("frameworks").GetProperty(TargetFramework);

        return new ProjectSnapshot(
            targetFrameworks,
            ReadSdkNames(projectPath),
            restore.GetProperty("centralPackageVersionsManagementEnabled").GetBoolean(),
            restore.GetProperty("centralPackageVersionOverrideDisabled").GetBoolean(),
            projectReferences,
            GetDirectPackageReferences(framework),
            GetOptionalPropertyNames(framework, "frameworkReferences"));
    }

    private static JsonElement FindProjectSpec(JsonElement root, string projectPath)
    {
        var projects = root.GetProperty("projects");

        foreach (var project in projects.EnumerateObject())
        {
            var restoredPath = project.Value.GetProperty("restore").GetProperty("projectPath").GetString();
            if (restoredPath is not null && PathsEqual(restoredPath, projectPath))
            {
                return project.Value;
            }
        }

        throw new InvalidOperationException("NuGet 还原规格中缺少当前项目。");
    }

    private static string MapProductionReference(
        string projectName,
        string reference,
        string projectDirectory,
        string sourceRoot)
    {
        var referencePath = Path.GetFullPath(reference, projectDirectory);
        var relativePath = Path.GetRelativePath(sourceRoot, referencePath);

        Assert.True(
            IsUnderDirectory(referencePath, sourceRoot),
            $"生产项目 {projectName} 不得引用 src 以外的项目：{relativePath}");

        var referenceName = Path.GetFileNameWithoutExtension(referencePath)
            ?? throw new InvalidOperationException("无法确定引用项目名称。");

        Assert.True(
            ExpectedPolicies.ContainsKey(referenceName),
            $"生产项目 {projectName} 引用了未登记的生产项目：{referenceName}");

        return referenceName;
    }

    private static string[] ReadSdkNames(string projectPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(projectPath, settings);
        var document = XDocument.Load(reader);
        var attributeSdks = document.Root?
            .Attribute("Sdk")?
            .Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        var elementSdks = document
            .Descendants("Sdk")
            .Select(sdk => sdk.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);

        return attributeSdks
            .Concat(elementSdks)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetOptionalPropertyNames(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var property)
            ? GetPropertyNames(property)
            : [];
    }

    private static string[] GetDirectPackageReferences(JsonElement framework)
    {
        if (!framework.TryGetProperty("dependencies", out var dependencies))
        {
            return [];
        }

        return dependencies
            .EnumerateObject()
            .Where(dependency =>
                !dependency.Value.TryGetProperty("autoReferenced", out var autoReferenced)
                || !autoReferenced.GetBoolean())
            .Select(dependency => dependency.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetPropertyNames(JsonElement property)
    {
        return property
            .EnumerateObject()
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);

        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DbBackupManager.slnx")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("无法从测试输出目录定位仓库根目录。");
    }

    private sealed record ProjectPolicy(
        string[] Sdks,
        string[] ProjectReferences,
        string[] PackageReferences,
        string[] FrameworkReferences);

    private sealed record ProjectSnapshot(
        string[] TargetFrameworks,
        string[] Sdks,
        bool CentralPackageManagementEnabled,
        bool CentralPackageVersionOverrideDisabled,
        string[] ProjectReferences,
        string[] PackageReferences,
        string[] FrameworkReferences);
}
