#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Publish')]
    [string]$Command = 'Plan',

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidateSet('SelfContained', 'FrameworkDependent')]
    [string]$DeploymentMode = 'SelfContained',

    [switch]$ForDistribution
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($PathValue)) {
        throw '输出根目录必须是绝对路径。'
    }

    return [System.IO.Path]::GetFullPath($PathValue).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-IsSamePath {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right
    )

    return [string]::Equals(
        (Get-AbsolutePath -PathValue $Left),
        (Get-AbsolutePath -PathValue $Right),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NormalDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue
    )

    if (-not (Test-Path -LiteralPath $PathValue -PathType Container)) {
        throw '输出根目录不存在或不是目录。请先创建受控的专用目录。'
    }

    $directory = Get-Item -LiteralPath $PathValue -Force
    if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw '输出根目录不能是重解析点。'
    }
}

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    $modeProperties = @()
    if (-not $selfContained) {
        $modeProperties = @('-p:RollForward=LatestPatch', '-p:AppHostDotNetSearch=Global')
    }
    & dotnet publish $ProjectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained $selfContained.ToString().ToLowerInvariant() `
        --output $Destination `
        --nologo `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        @modeProperties

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish 执行失败。'
    }
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptDirectory 'RuntimeRequirements.ps1')
. (Join-Path $scriptDirectory 'LicenseMaterials.ps1')
$selfContained = $DeploymentMode -eq 'SelfContained'
$requiredFrameworks = @(Get-RequiredFrameworks -SelfContained $selfContained)
$repositoryRoot = Get-AbsolutePath -PathValue (Join-Path $scriptDirectory '..\..')
$resolvedOutputRoot = Get-AbsolutePath -PathValue $OutputRoot
$outputDriveRoot = [System.IO.Path]::GetPathRoot($resolvedOutputRoot)

if (Test-IsSamePath -Left $resolvedOutputRoot -Right $outputDriveRoot) {
    throw '输出根目录不能是磁盘根目录。'
}

if (Test-IsSamePath -Left $resolvedOutputRoot -Right $repositoryRoot) {
    throw '输出根目录不能是仓库根目录。'
}

Assert-NormalDirectory -PathValue $resolvedOutputRoot

$packageName = "DbBackupManager-$Version-$RuntimeIdentifier"
if (-not $selfContained) { $packageName += '-framework-dependent' }
$targetDirectory = Join-Path $resolvedOutputRoot $packageName
$stagingDirectory = Join-Path $resolvedOutputRoot ".$packageName.staging-$([Guid]::NewGuid().ToString('N'))"

if (Test-Path -LiteralPath $targetDirectory) {
    throw '目标版本目录已存在；发布脚本不会覆盖现有版本。'
}

$plan = [ordered]@{
    schemaVersion = 2
    command = $Command
    product = 'DbBackupManager'
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $selfContained
    requiredFrameworks = $requiredFrameworks
    runtimeRollForward = if ($selfContained) { $null } else { 'LatestPatch' }
    appHostDotNetSearch = if ($selfContained) { $null } else { 'Global' }
    forDistribution = [bool]$ForDistribution
    licenseMaterials = @('LICENSE', 'THIRD-PARTY-NOTICES.txt', 'licenses/index.json')
    targetDirectory = $targetDirectory
    webProject = Join-Path $repositoryRoot 'src\DbBackupManager.Web\DbBackupManager.Web.csproj'
    workerProject = Join-Path $repositoryRoot 'src\DbBackupManager.Worker\DbBackupManager.Worker.csproj'
}

if ($Command -eq 'Plan') {
    $plan | ConvertTo-Json -Depth 4
    return
}

if ($ForDistribution) {
    $dirtyFiles = @(& git -C $repositoryRoot status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $dirtyFiles.Count -ne 0) {
        throw '分发候选工作树必须干净，才能登记可靠来源提交。'
    }
}

New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    $webOutput = Join-Path $stagingDirectory 'web'
    $workerOutput = Join-Path $stagingDirectory 'worker'

    Invoke-DotNetPublish -ProjectPath $plan.webProject -Destination $webOutput
    Invoke-DotNetPublish -ProjectPath $plan.workerProject -Destination $workerOutput

    Assert-PackageRuntimeConfiguration -PackageRoot $stagingDirectory -SelfContained $selfContained

    $expectedExecutables = @(
        (Join-Path $webOutput 'DbBackupManager.Web.exe'),
        (Join-Path $workerOutput 'DbBackupManager.Worker.exe')
    )

    foreach ($expectedExecutable in $expectedExecutables) {
        if (-not (Test-Path -LiteralPath $expectedExecutable -PathType Leaf)) {
            throw '发布结果缺少预期的 Windows 可执行文件。'
        }
    }

    $licenseReport = Write-PackageLicenseMaterials -RepositoryRoot $repositoryRoot -PackageRoot $stagingDirectory `
        -AssetsPaths @(
            (Join-Path $repositoryRoot 'src/DbBackupManager.Web/obj/project.assets.json'),
            (Join-Path $repositoryRoot 'src/DbBackupManager.Worker/obj/project.assets.json')
        ) -SelfContained $selfContained -RuntimeIdentifier $RuntimeIdentifier
    if ($ForDistribution) { Assert-DistributionLicenseMaterials -Report $licenseReport }

    $gitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitCommit -notmatch '^[0-9a-f]{40}$') {
        throw '无法读取发布来源提交。'
    }

    $files = Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [System.IO.Path]::GetRelativePath($stagingDirectory, $_.FullName).Replace('\', '/')
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }

    $manifest = [ordered]@{
        schemaVersion = 2
        product = 'DbBackupManager'
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        selfContained = $selfContained
        requiredFrameworks = $requiredFrameworks
        runtimeRollForward = $plan.runtimeRollForward
        appHostDotNetSearch = $plan.appHostDotNetSearch
        sourceCommit = $gitCommit
        licenseMaterialsStatus = $licenseReport.status
        licenseReportPath = 'licenses/index.json'
        distributionTermsPath = $licenseReport.distributionTermsPath
        requiresDistributionTermsAcceptance = $null -ne $licenseReport.distributionTermsPath
        forDistribution = [bool]$ForDistribution
        files = @($files)
    }

    $manifestPath = Join-Path $stagingDirectory 'manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    Move-Item -LiteralPath $stagingDirectory -Destination $targetDirectory

    [ordered]@{
        status = 'Published'
        targetDirectory = $targetDirectory
        manifestPath = Join-Path $targetDirectory 'manifest.json'
        sourceCommit = $gitCommit
        licenseMaterialsStatus = $licenseReport.status
        fileCount = $files.Count
    } | ConvertTo-Json -Depth 4
}
catch {
    Write-Error "发布失败；为避免误删，暂存目录已保留以供检查：$stagingDirectory" -ErrorAction Continue
    throw
}
