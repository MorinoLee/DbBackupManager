#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('Install', 'Upgrade', 'Uninstall')]
    [string]$Action = 'Install',

    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [string]$InstallRoot,

    [Parameter(Mandatory)]
    [string]$DataRoot,

    [switch]$Apply,

    [switch]$Stage,

    [switch]$AcceptDistributionTerms,

    [string]$Confirmation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'RuntimeRequirements.ps1')

$webServiceName = 'DbBackupManager.Web'
$workerServiceName = 'DbBackupManager.Worker'
$serviceNames = @($webServiceName, $workerServiceName)
$expectedConfirmation = "$Action $webServiceName $workerServiceName"

if ($Stage -and $Action -ne 'Install') {
    throw '仅首次安装支持暂不启动服务。'
}

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($PathValue)) {
        throw "$Description 必须是绝对路径。"
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
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    $relative = [System.IO.Path]::GetRelativePath($Parent, $Candidate)
    return -not [System.IO.Path]::IsPathFullyQualified($relative) -and
        $relative -ne '..' -and
        -not $relative.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)
}

function Assert-SafeRoot {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $driveRoot = [System.IO.Path]::GetPathRoot($PathValue)
    if (Test-IsSamePath -Left $PathValue -Right $driveRoot) {
        throw "$Description 不能是磁盘根目录。"
    }

    $cursor = $PathValue
    while ($cursor -and -not (Test-Path -LiteralPath $cursor)) {
        $cursor = Split-Path -Parent $cursor
    }

    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description 及其现有父目录不能包含重解析点。"
        }

        $parent = Split-Path -Parent $cursor
        if (-not $parent -or (Test-IsSamePath -Left $cursor -Right $parent)) {
            break
        }

        $cursor = $parent
    }
}

function Assert-NormalFile {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $PathValue -PathType Leaf)) {
        throw "$Description 不存在或不是普通文件。"
    }

    $item = Get-Item -LiteralPath $PathValue -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description 不能是重解析点。"
    }
}

function Assert-NormalDirectoryTree {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $PathValue -PathType Container)) {
        throw "$Description 不存在或不是目录。"
    }

    $items = @((Get-Item -LiteralPath $PathValue -Force)) +
        @(Get-ChildItem -LiteralPath $PathValue -Recurse -Force)
    if (@($items | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
        throw "$Description 不能包含重解析点。"
    }
}

function Get-VerifiedPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue
    )

    $resolvedPackageRoot = Get-AbsolutePath -PathValue $PathValue -Description '发布包根目录'
    Assert-SafeRoot -PathValue $resolvedPackageRoot -Description '发布包根目录'

    if (-not (Test-Path -LiteralPath $resolvedPackageRoot -PathType Container)) {
        throw '发布包根目录不存在或不是目录。'
    }

    $packageItem = Get-Item -LiteralPath $resolvedPackageRoot -Force
    if (($packageItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw '发布包根目录不能是重解析点。'
    }

    if (@(Get-ChildItem -LiteralPath $resolvedPackageRoot -Recurse -Force |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
        throw '发布包内部不能包含重解析点。'
    }

    $manifestPath = Join-Path $resolvedPackageRoot 'manifest.json'
    Assert-NormalFile -PathValue $manifestPath -Description '发布清单'

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 8
    }
    catch {
        throw '发布清单不是有效 JSON。'
    }

    if ($manifest.schemaVersion -notin @(1, 2) -or
        $manifest.product -ne 'DbBackupManager' -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.selfContained -isnot [bool] -or
        ($manifest.schemaVersion -eq 1 -and -not $manifest.selfContained) -or
        $manifest.version -notmatch '^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$' -or
        $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw '发布清单的产品、版本、运行时或来源提交不符合固定契约。'
    }

    $requiredFrameworks = @(Get-RequiredFrameworks -SelfContained $manifest.selfContained)
    if ($manifest.schemaVersion -eq 2) {
        if (@($manifest.requiredFrameworks).Count -ne $requiredFrameworks.Count) {
            throw '发布清单运行时依赖不符合固定契约。'
        }
        foreach ($framework in $requiredFrameworks) {
            if (@($manifest.requiredFrameworks | Where-Object {
                $_.name -ceq $framework.name -and $_.version -ceq $framework.version
            }).Count -ne 1) { throw '发布清单运行时依赖不符合固定契约。' }
        }
        if (-not $manifest.selfContained -and
            ($manifest.runtimeRollForward -cne 'LatestPatch' -or $manifest.appHostDotNetSearch -cne 'Global')) {
            throw '轻量发布清单必须使用 LatestPatch 与 Global 主机搜索。'
        }
    }

    $manifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.files)) {
        if ([string]::IsNullOrWhiteSpace($file.path) -or [System.IO.Path]::IsPathFullyQualified($file.path)) {
            throw '发布清单包含无效相对路径。'
        }

        $candidate = [System.IO.Path]::GetFullPath((Join-Path $resolvedPackageRoot $file.path))
        if (-not (Test-IsChildPath -Candidate $candidate -Parent $resolvedPackageRoot) -or
            (Test-IsSamePath -Left $candidate -Right $resolvedPackageRoot)) {
            throw '发布清单包含越界路径。'
        }

        if (-not $manifestPaths.Add($candidate)) {
            throw '发布清单包含重复路径。'
        }

        Assert-NormalFile -PathValue $candidate -Description '清单文件'
        $actualFile = Get-Item -LiteralPath $candidate
        $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualFile.Length -ne [long]$file.length -or $actualHash -ne $file.sha256) {
            throw '发布包文件长度或 SHA-256 与清单不一致。'
        }
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $resolvedPackageRoot -Recurse -File |
        Where-Object { -not (Test-IsSamePath -Left $_.FullName -Right $manifestPath) })
    if ($actualFiles.Count -ne $manifestPaths.Count -or
        @($actualFiles | Where-Object { -not $manifestPaths.Contains($_.FullName) }).Count -ne 0) {
        throw '发布包包含未登记文件或缺少清单文件。'
    }

    Assert-NormalFile -PathValue (Join-Path $resolvedPackageRoot 'web\DbBackupManager.Web.exe') -Description 'Web 可执行文件'
    Assert-NormalFile -PathValue (Join-Path $resolvedPackageRoot 'worker\DbBackupManager.Worker.exe') -Description 'Worker 可执行文件'
    Assert-PackageRuntimeConfiguration -PackageRoot $resolvedPackageRoot -SelfContained $manifest.selfContained

    $distributionTermsPath = $null
    $hasTerms = $manifestPaths.Contains((Join-Path $resolvedPackageRoot 'DISTRIBUTION-TERMS.md'))
    if ($hasTerms -and ($manifest.PSObject.Properties.Name -notcontains 'requiresDistributionTermsAcceptance' -or
        $manifest.requiresDistributionTermsAcceptance -cne $true)) { throw '分发条款文件缺少接受标记。' }
    if ($manifest.PSObject.Properties.Name -contains 'requiresDistributionTermsAcceptance') {
        if ($manifest.requiresDistributionTermsAcceptance -isnot [bool]) { throw '分发条款接受标记无效。' }
        if ($manifest.requiresDistributionTermsAcceptance) {
            if ($manifest.distributionTermsPath -cne 'DISTRIBUTION-TERMS.md') { throw '分发条款路径无效。' }
            $distributionTermsPath = Join-Path $resolvedPackageRoot 'DISTRIBUTION-TERMS.md'
            if (-not $manifestPaths.Contains($distributionTermsPath)) { throw '分发条款未登记到发布清单。' }
            Assert-NormalFile -PathValue $distributionTermsPath -Description '分发条款'
        }
        elseif ($manifest.distributionTermsPath) { throw '分发条款路径与接受标记不一致。' }
    }

    return [pscustomobject]@{
        Root = $resolvedPackageRoot
        Version = [string]$manifest.version
        RuntimeIdentifier = [string]$manifest.runtimeIdentifier
        SourceCommit = [string]$manifest.sourceCommit
        FileCount = $manifestPaths.Count
        SelfContained = $manifest.selfContained
        RequiredFrameworks = $requiredFrameworks
        DistributionTermsPath = $distributionTermsPath
    }
}

function Get-ServiceRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    return Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
}

function Get-ServiceExecutablePath {
    param(
        [Parameter(Mandatory)]
        [string]$ServicePathName
    )

    if ($ServicePathName.StartsWith('"', [System.StringComparison]::Ordinal)) {
        $endQuote = $ServicePathName.IndexOf('"', 1)
        if ($endQuote -lt 2) {
            throw '现有服务启动路径格式无效。'
        }

        return $ServicePathName.Substring(1, $endQuote - 1)
    }

    return $ServicePathName.Split(' ', 2)[0]
}

function Assert-ServiceOwnership {
    param(
        [Parameter(Mandatory)]
        $ServiceRecord,

        [Parameter(Mandatory)]
        [string]$ExpectedInstallRoot
    )

    $executablePath = Get-AbsolutePath `
        -PathValue (Get-ServiceExecutablePath -ServicePathName $ServiceRecord.PathName) `
        -Description '现有服务可执行文件路径'
    if (-not (Test-IsChildPath -Candidate $executablePath -Parent $ExpectedInstallRoot)) {
        throw '现有同名服务不属于指定程序根目录，拒绝操作。'
    }
}

function Assert-Administrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '写操作必须在提升权限的 PowerShell 中执行。'
    }
}

function Ensure-ServiceEventLogSources {
    foreach ($serviceName in $serviceNames) {
        if ([System.Diagnostics.EventLog]::SourceExists($serviceName)) {
            $logName = [System.Diagnostics.EventLog]::LogNameFromSourceName($serviceName, '.')
            if ($logName -cne 'Application') {
                throw "服务事件源已绑定到非 Application 日志：$serviceName。"
            }
            continue
        }

        $source = [System.Diagnostics.EventSourceCreationData]::new($serviceName, 'Application')
        [System.Diagnostics.EventLog]::CreateEventSource($source)
    }
}

function Invoke-Sc {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Windows 服务控制命令执行失败，退出码：$LASTEXITCODE。"
    }

    return $output
}

function Get-ServiceSid {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName
    )

    $account = [System.Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")
    return $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
}

function Invoke-Icacls {
    param(
        [Parameter(Mandatory)]
        [string]$PathValue,

        [Parameter(Mandatory)]
        [string[]]$Grants,

        [switch]$Recurse
    )

    $arguments = @($PathValue, '/inheritance:r', '/grant:r') + $Grants
    & icacls.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw '目录或文件 ACL 设置失败。'
    }

    if ($Recurse) {
        foreach ($child in Get-ChildItem -LiteralPath $PathValue -Force) {
            & icacls.exe $child.FullName /reset /T | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw '目录子项 ACL 继承设置失败。'
            }
        }
    }
}

function Set-DeploymentAcls {
    param(
        [Parameter(Mandatory)]
        [string]$VersionDirectory,

        [Parameter(Mandatory)]
        [string]$ResolvedDataRoot
    )

    $webSid = Get-ServiceSid -ServiceName $webServiceName
    $workerSid = Get-ServiceSid -ServiceName $workerServiceName
    $systemFull = '*S-1-5-18:(OI)(CI)F'
    $administratorsFull = '*S-1-5-32-544:(OI)(CI)F'

    Invoke-Icacls -PathValue $VersionDirectory -Recurse -Grants @(
        $systemFull,
        $administratorsFull,
        "*$webSid`:(OI)(CI)RX",
        "*$workerSid`:(OI)(CI)RX")

    Invoke-Icacls -PathValue $ResolvedDataRoot -Grants @(
        $systemFull,
        $administratorsFull,
        "*$webSid`:(OI)(CI)RX",
        "*$workerSid`:(OI)(CI)RX")

    $configDirectory = Join-Path $ResolvedDataRoot 'config'
    Invoke-Icacls -PathValue $configDirectory -Grants @(
        $systemFull,
        $administratorsFull,
        "*$webSid`:(OI)(CI)RX",
        "*$workerSid`:(OI)(CI)RX")
    Invoke-Icacls -PathValue (Join-Path $configDirectory 'web.json') -Grants @(
        '*S-1-5-18:F', '*S-1-5-32-544:F', "*$webSid`:R")
    Invoke-Icacls -PathValue (Join-Path $configDirectory 'worker.json') -Grants @(
        '*S-1-5-18:F', '*S-1-5-32-544:F', "*$workerSid`:R")

    Invoke-Icacls -PathValue (Join-Path $ResolvedDataRoot 'keys\cookie') -Recurse -Grants @(
        $systemFull, $administratorsFull, "*$webSid`:(OI)(CI)M")
    Invoke-Icacls -PathValue (Join-Path $ResolvedDataRoot 'keys\business') -Recurse -Grants @(
        $systemFull, $administratorsFull, "*$webSid`:(OI)(CI)M", "*$workerSid`:(OI)(CI)M")
    Invoke-Icacls -PathValue (Join-Path $ResolvedDataRoot 'logs\web') -Recurse -Grants @(
        $systemFull, $administratorsFull, "*$webSid`:(OI)(CI)M")
    Invoke-Icacls -PathValue (Join-Path $ResolvedDataRoot 'logs\worker') -Recurse -Grants @(
        $systemFull, $administratorsFull, "*$workerSid`:(OI)(CI)M")
}

function Copy-Package {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        throw '目标版本目录已存在；部署脚本不会覆盖现有版本。'
    }

    New-Item -ItemType Directory -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse
    }
}

function Get-ServiceCommandLine {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,

        [Parameter(Mandatory)]
        [string]$ConfigurationPath
    )

    return ('"{0}" --environment Production --DbBackupManager:ExternalConfigurationPath="{1}"' -f `
        $ExecutablePath, $ConfigurationPath)
}

function Stop-ServiceIfRunning {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $service = Get-Service -Name $Name
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $Name
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
}

if (-not $IsWindows) {
    throw 'Windows Service 部署脚本只能在 Windows 上运行。'
}

$resolvedInstallRoot = Get-AbsolutePath -PathValue $InstallRoot -Description '程序根目录'
$resolvedDataRoot = Get-AbsolutePath -PathValue $DataRoot -Description '数据根目录'
Assert-SafeRoot -PathValue $resolvedInstallRoot -Description '程序根目录'
Assert-SafeRoot -PathValue $resolvedDataRoot -Description '数据根目录'

if ((Test-IsSamePath -Left $resolvedInstallRoot -Right $resolvedDataRoot) -or
    (Test-IsChildPath -Candidate $resolvedInstallRoot -Parent $resolvedDataRoot) -or
    (Test-IsChildPath -Candidate $resolvedDataRoot -Parent $resolvedInstallRoot)) {
    throw '程序根目录与数据根目录必须彼此独立，不能相同或互相包含。'
}

$requiredDataPaths = @()
if ($Action -ne 'Uninstall') {
    $requiredDataPaths = @(
        (Join-Path $resolvedDataRoot 'config\web.json'),
        (Join-Path $resolvedDataRoot 'config\worker.json'),
        (Join-Path $resolvedDataRoot 'keys\cookie'),
        (Join-Path $resolvedDataRoot 'keys\business'),
        (Join-Path $resolvedDataRoot 'logs\web'),
        (Join-Path $resolvedDataRoot 'logs\worker')
    )
    foreach ($requiredPath in $requiredDataPaths) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw '数据根目录缺少固定配置、Key Ring 或日志路径。部署脚本不会代写秘密配置。'
        }
    }
    Assert-NormalFile -PathValue $requiredDataPaths[0] -Description 'Web 外部配置'
    Assert-NormalFile -PathValue $requiredDataPaths[1] -Description 'Worker 外部配置'
    Assert-NormalDirectoryTree -PathValue (Join-Path $resolvedDataRoot 'config') -Description '配置目录'
    Assert-NormalDirectoryTree -PathValue $requiredDataPaths[2] -Description 'Cookie Key Ring 目录'
    Assert-NormalDirectoryTree -PathValue $requiredDataPaths[3] -Description '业务 Key Ring 目录'
    Assert-NormalDirectoryTree -PathValue $requiredDataPaths[4] -Description 'Web 日志目录'
    Assert-NormalDirectoryTree -PathValue $requiredDataPaths[5] -Description 'Worker 日志目录'
}

$existingServices = @{}
foreach ($serviceName in $serviceNames) {
    $existingServices[$serviceName] = Get-ServiceRecord -Name $serviceName
}

$package = $null
$targetVersionDirectory = $null
$runtimePreflight = $null
if ($Action -ne 'Uninstall') {
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
        throw '安装或升级必须提供发布包根目录。'
    }

    $package = Get-VerifiedPackage -PathValue $PackageRoot
    $runtimePreflight = Get-PackageRuntimePreflight -SelfContained $package.SelfContained -RequiredFrameworks $package.RequiredFrameworks
    $targetVersionDirectory = Join-Path $resolvedInstallRoot "versions\DbBackupManager-$($package.Version)-$($package.RuntimeIdentifier)"
    if (-not $package.SelfContained) { $targetVersionDirectory += '-framework-dependent' }
    if (Test-Path -LiteralPath $targetVersionDirectory) {
        throw '目标版本目录已存在；部署脚本不会覆盖现有版本。'
    }
}

switch ($Action) {
    'Install' {
        if (@($existingServices.Values | Where-Object { $null -ne $_ }).Count -ne 0) {
            throw '安装要求两个固定服务名均不存在。'
        }
    }
    'Upgrade' {
        foreach ($serviceName in $serviceNames) {
            if ($null -eq $existingServices[$serviceName]) {
                throw '升级要求两个固定服务均已存在。'
            }
            Assert-ServiceOwnership -ServiceRecord $existingServices[$serviceName] -ExpectedInstallRoot $resolvedInstallRoot
        }
    }
    'Uninstall' {
        foreach ($serviceName in $serviceNames) {
            if ($null -eq $existingServices[$serviceName]) {
                throw '卸载要求两个固定服务均已存在。'
            }
            Assert-ServiceOwnership -ServiceRecord $existingServices[$serviceName] -ExpectedInstallRoot $resolvedInstallRoot
        }
    }
}

$plan = [ordered]@{
    schemaVersion = 1
    action = $Action
    apply = [bool]$Apply
    stage = [bool]$Stage
    serviceNames = $serviceNames
    installRoot = $resolvedInstallRoot
    dataRoot = $resolvedDataRoot
    packageRoot = if ($null -eq $package) { $null } else { $package.Root }
    packageVersion = if ($null -eq $package) { $null } else { $package.Version }
    packageSourceCommit = if ($null -eq $package) { $null } else { $package.SourceCommit }
    selfContained = if ($null -eq $package) { $null } else { $package.SelfContained }
    requiredFrameworks = @(if ($null -ne $package) { $package.RequiredFrameworks })
    runtimePreflight = $runtimePreflight
    targetVersionDirectory = $targetVersionDirectory
    preservesDataOnUninstall = $true
    eventLogSources = if ($Action -eq 'Uninstall') { @() } else { $serviceNames }
    preservesEventLogSourcesOnUninstall = $true
    requiredConfirmation = $expectedConfirmation
    distributionTermsPath = if ($null -eq $package) { $null } else { $package.DistributionTermsPath }
    distributionTermsAccepted = [bool]$AcceptDistributionTerms
}

if (-not $Apply) {
    $plan | ConvertTo-Json -Depth 5
    return
}

if ($Confirmation -cne $expectedConfirmation) {
    throw "写操作确认短语不匹配。必须精确传入：$expectedConfirmation"
}

if ($null -ne $package -and $package.DistributionTermsPath -and -not $AcceptDistributionTerms) {
    throw '请先阅读包内 DISTRIBUTION-TERMS.md 及其引用的完整原许可；安装或升级 Apply 必须显式传入 -AcceptDistributionTerms。'
}

Assert-Administrator

if ($Action -eq 'Install') {
    Copy-Package -Source $package.Root -Destination $targetVersionDirectory

    $webCommandLine = Get-ServiceCommandLine `
        -ExecutablePath (Join-Path $targetVersionDirectory 'web\DbBackupManager.Web.exe') `
        -ConfigurationPath (Join-Path $resolvedDataRoot 'config\web.json')
    $workerCommandLine = Get-ServiceCommandLine `
        -ExecutablePath (Join-Path $targetVersionDirectory 'worker\DbBackupManager.Worker.exe') `
        -ConfigurationPath (Join-Path $resolvedDataRoot 'config\worker.json')

    $createdServices = [System.Collections.Generic.List[string]]::new()
    try {
        $startMode = if ($Stage) { 'demand' } else { 'auto' }
        Invoke-Sc -Arguments @('create', $webServiceName, 'binPath=', $webCommandLine, 'start=', $startMode, 'obj=', "NT SERVICE\$webServiceName") | Out-Null
        $createdServices.Add($webServiceName)
        Invoke-Sc -Arguments @('create', $workerServiceName, 'binPath=', $workerCommandLine, 'start=', $startMode, 'obj=', "NT SERVICE\$workerServiceName") | Out-Null
        $createdServices.Add($workerServiceName)
        Set-DeploymentAcls -VersionDirectory $targetVersionDirectory -ResolvedDataRoot $resolvedDataRoot
        Ensure-ServiceEventLogSources
        if (-not $Stage) {
            Start-Service -Name $webServiceName
            Start-Service -Name $workerServiceName
        }
    }
    catch {
        foreach ($createdService in $createdServices) {
            try {
                Stop-ServiceIfRunning -Name $createdService
            }
            catch {
                Write-Warning "未能停止本轮创建的服务：$createdService"
            }
            try {
                Invoke-Sc -Arguments @('delete', $createdService) | Out-Null
            }
            catch {
                Write-Warning "未能回退本轮创建的服务记录：$createdService"
            }
        }
        Write-Warning '程序版本目录已保留，未执行递归删除。'
        throw
    }
}
elseif ($Action -eq 'Upgrade') {
    Copy-Package -Source $package.Root -Destination $targetVersionDirectory
    Set-DeploymentAcls -VersionDirectory $targetVersionDirectory -ResolvedDataRoot $resolvedDataRoot
    Ensure-ServiceEventLogSources

    $oldWebPath = $existingServices[$webServiceName].PathName
    $oldWorkerPath = $existingServices[$workerServiceName].PathName
    $newWebPath = Get-ServiceCommandLine `
        -ExecutablePath (Join-Path $targetVersionDirectory 'web\DbBackupManager.Web.exe') `
        -ConfigurationPath (Join-Path $resolvedDataRoot 'config\web.json')
    $newWorkerPath = Get-ServiceCommandLine `
        -ExecutablePath (Join-Path $targetVersionDirectory 'worker\DbBackupManager.Worker.exe') `
        -ConfigurationPath (Join-Path $resolvedDataRoot 'config\worker.json')

    Stop-ServiceIfRunning -Name $workerServiceName
    Stop-ServiceIfRunning -Name $webServiceName
    try {
        Invoke-Sc -Arguments @('config', $webServiceName, 'binPath=', $newWebPath) | Out-Null
        Invoke-Sc -Arguments @('config', $workerServiceName, 'binPath=', $newWorkerPath) | Out-Null
        Start-Service -Name $webServiceName
        Start-Service -Name $workerServiceName
    }
    catch {
        Invoke-Sc -Arguments @('config', $webServiceName, 'binPath=', $oldWebPath) | Out-Null
        Invoke-Sc -Arguments @('config', $workerServiceName, 'binPath=', $oldWorkerPath) | Out-Null
        Start-Service -Name $webServiceName
        Start-Service -Name $workerServiceName
        Write-Warning '服务启动路径已回退；新程序版本目录保留，数据库不会回退。'
        throw
    }
}
else {
    Stop-ServiceIfRunning -Name $workerServiceName
    Stop-ServiceIfRunning -Name $webServiceName
    Invoke-Sc -Arguments @('delete', $workerServiceName) | Out-Null
    Invoke-Sc -Arguments @('delete', $webServiceName) | Out-Null
}

[ordered]@{
    status = if ($Stage) { 'Staged' } else { 'Applied' }
    action = $Action
    serviceNames = $serviceNames
    activeVersionDirectory = if ($Action -eq 'Uninstall') { $null } else { $targetVersionDirectory }
    preservedInstallRoot = $true
    preservedDataRoot = $true
} | ConvertTo-Json -Depth 4
