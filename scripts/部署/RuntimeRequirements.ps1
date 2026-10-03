#Requires -Version 7.0

# 发布与部署共用的固定运行时契约；升级目标框架时必须同步更新并复验。
function Get-RequiredFrameworks {
    param([bool]$SelfContained)
    if (-not $SelfContained) {
        [ordered]@{ name = 'Microsoft.NETCore.App'; version = '10.0.0' }
        [ordered]@{ name = 'Microsoft.AspNetCore.App'; version = '10.0.0' }
    }
}

function Assert-PackageRuntimeConfiguration {
    param([string]$PackageRoot, [bool]$SelfContained)

    foreach ($hostName in @('Web', 'Worker')) {
        $configPath = Join-Path $PackageRoot "$($hostName.ToLowerInvariant())/DbBackupManager.$hostName.runtimeconfig.json"
        $options = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable).runtimeOptions
        if ($options.tfm -ne 'net10.0') { throw '发布包目标框架必须为 net10.0。' }
        if ($SelfContained) {
            if ($options.ContainsKey('framework') -or $options.ContainsKey('frameworks') -or
                -not $options.ContainsKey('includedFrameworks')) {
                throw '自包含清单与包内运行时配置不一致。'
            }
            continue
        }

        if ($options.ContainsKey('includedFrameworks') -or $options.rollForward -cne 'LatestPatch' -or
            $options.ContainsKey('applyPatches') -or $options.ContainsKey('rollForwardOnNoCandidateFx')) {
            throw '轻量包必须使用 LatestPatch 运行时策略。'
        }
        $actual = @(if ($options.ContainsKey('frameworks')) { $options.frameworks } else { $options.framework })
        $expected = @(Get-RequiredFrameworks -SelfContained $false | Where-Object {
            $hostName -eq 'Web' -or $_.name -eq 'Microsoft.NETCore.App'
        })
        if ($actual.Count -ne $expected.Count) { throw '包内运行时依赖与发布契约不一致。' }
        foreach ($framework in $expected) {
            if (@($actual | Where-Object { $_.name -ceq $framework.name -and $_.version -ceq $framework.version }).Count -ne 1) {
                throw '包内运行时依赖与发布契约不一致。'
            }
        }
    }
}

function Get-MachineDotNetHost {
    # apphost 的 Global 搜索使用 32 位注册表视图，即使目标是 x64。
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry32')
    $key = $null
    try {
        $key = $registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        $location = if ($null -ne $key) { [string]$key.GetValue('InstallLocation') } else { '' }
    }
    finally {
        if ($null -ne $key) { $key.Dispose() }
        $registry.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($location)) {
        $programFiles = [Environment]::GetEnvironmentVariable('ProgramW6432')
        if ([string]::IsNullOrWhiteSpace($programFiles)) { $programFiles = [Environment]::GetFolderPath('ProgramFiles') }
        $location = Join-Path $programFiles 'dotnet'
    }
    $hostPath = Join-Path $location 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
        throw '轻量包需要机器级 x64 .NET 10 / ASP.NET Core 10 运行时；未找到 dotnet.exe。'
    }
    Assert-X64DotNetHost -HostPath $hostPath
    return $hostPath
}

function Assert-X64DotNetHost {
    param([string]$HostPath)
    # 防止误将 x86/ARM64 主机当成 x64。PE COFF Machine 与操作系统语言无关。
    $stream = [IO.File]::OpenRead($HostPath)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw '机器级 dotnet.exe 不是 x64 PE 主机。'
        }
    }
    finally { $reader.Dispose() }
}

function Get-InstalledFrameworkLines {
    param([string]$DotNetHost)
    $lines = @(& $DotNetHost --list-runtimes)
    if ($LASTEXITCODE -ne 0) { throw '无法查询机器级 x64 .NET 运行时。' }
    return $lines
}

function Get-CompatibleFrameworks {
    param([object[]]$RequiredFrameworks, [AllowEmptyCollection()][string[]]$InstalledLines)
    $selected = @(foreach ($required in $RequiredFrameworks) {
        $minimum = [version]$required.version
        $compatible = @($InstalledLines | ForEach-Object {
            if ($_ -cmatch '^(\S+) (\d+\.\d+\.\d+) \[.+\]$' -and $Matches[1] -ceq $required.name) {
                $candidate = [version]$Matches[2]
                if ($candidate.Major -eq $minimum.Major -and $candidate.Minor -eq $minimum.Minor -and $candidate -ge $minimum) {
                    $candidate
                }
            }
        } | Sort-Object -Descending)
        if ($compatible.Count -eq 0) {
            throw "缺少兼容 x64 运行时：$($required.name) $($required.version)（LatestPatch，不接受其他主/次版本或预览版）。"
        }
        [ordered]@{ name = $required.name; version = $compatible[0].ToString() }
    })
    $core = @($selected | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
    $asp = @($selected | Where-Object { $_.name -eq 'Microsoft.AspNetCore.App' })
    # ASP.NET Core 的共享框架还依赖同补丁的 NETCore；只有两个名称并不足以启动 Web。
    if ($asp.Count -gt 0 -and ($core.Count -eq 0 -or [version]$core[0].version -lt [version]$asp[0].version)) {
        throw 'NETCore 补丁版本不能低于选中的 ASP.NET Core 补丁版本；请补齐配套 x64 运行时。'
    }
    return $selected
}

function Get-PackageRuntimePreflight {
    param([bool]$SelfContained, [object[]]$RequiredFrameworks)
    if ($SelfContained) {
        return [ordered]@{ status = 'NotRequired'; dotNetHost = $null; frameworks = @() }
    }
    $hostPath = Get-MachineDotNetHost
    $lines = @(Get-InstalledFrameworkLines -DotNetHost $hostPath)
    $compatible = @(Get-CompatibleFrameworks -RequiredFrameworks $RequiredFrameworks -InstalledLines $lines)
    return [ordered]@{ status = 'Passed'; dotNetHost = $hostPath; frameworks = $compatible }
}
