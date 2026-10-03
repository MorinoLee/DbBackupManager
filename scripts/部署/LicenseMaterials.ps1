#Requires -Version 7.0

# 离线汇集已还原包的许可材料；这份清单不是最终 SBOM 或分发授权。
function Get-LicenseChildPath {
    param([string]$Root, [string]$RelativePath)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if ([IO.Path]::IsPathFullyQualified($RelativePath) -or
        -not $path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '许可材料路径越出所属目录。'
    }
    return $path
}

function Get-SniDistributionReview {
    param([string]$RepositoryRoot, [string]$Component, [string]$DeclaredLicense)
    if ($Component -cne 'Microsoft.Data.SqlClient.SNI.runtime/6.0.3') { return $null }
    $termsPath = Join-Path $RepositoryRoot 'DISTRIBUTION-TERMS.md'
    $reviewPath = Join-Path $RepositoryRoot 'scripts/部署/SniDistributionReview.json'
    if (-not (Test-Path -LiteralPath $termsPath -PathType Leaf)) { throw 'SNI 分发条款材料缺失。' }
    $result = @{ termsPath = $termsPath; approved = $false }
    if (-not (Test-Path -LiteralPath $reviewPath -PathType Leaf)) { return $result }
    $review = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json -AsHashtable
    if ($review.schemaVersion -ne 1 -or $review.component -cne $Component -or
        $review.licenseSha256 -cne (Get-FileHash -LiteralPath $DeclaredLicense).Hash.ToLowerInvariant() -or
        $review.termsSha256 -cne (Get-FileHash -LiteralPath $termsPath).Hash.ToLowerInvariant()) { return $result }
    $result.approved = $review.approved -is [bool] -and $review.approved -and
        -not [string]::IsNullOrWhiteSpace($review.approvalReference)
    return $result
}

function Write-PackageLicenseMaterials {
    param(
        [string]$RepositoryRoot,
        [string]$PackageRoot,
        [string[]]$AssetsPaths,
        [bool]$SelfContained,
        [string]$RuntimeIdentifier
    )

    $projectLicense = Join-Path $RepositoryRoot 'LICENSE'
    $licenseText = Get-Content -LiteralPath $projectLicense -Raw
    $mitStart = $licenseText.IndexOf('Permission is hereby granted, free of charge', [StringComparison]::Ordinal)
    if ($mitStart -lt 0) { throw '项目 LICENSE 缺少标准 MIT 授权正文。' }
    $mitBody = $licenseText.Substring($mitStart).TrimEnd()
    $components = @{}
    $packageFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assetsPath in $AssetsPaths) {
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($folder in $assets.packageFolders.Keys) { [void]$packageFolders.Add($folder) }
        foreach ($library in $assets.libraries.GetEnumerator()) {
            if ($library.Value.type -eq 'package') { $components[$library.Key] = $library.Value.path }
        }
    }
    if ($SelfContained) {
        foreach ($hostName in @('Web', 'Worker')) {
            $runtime = Get-Content -LiteralPath (Join-Path $PackageRoot "$($hostName.ToLowerInvariant())/DbBackupManager.$hostName.runtimeconfig.json") -Raw |
                ConvertFrom-Json -AsHashtable
            foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
                $key = "$($framework.name).Runtime.$RuntimeIdentifier/$($framework.version)"
                $components[$key] = $key.ToLowerInvariant()
            }
        }
    }

    $licensesRoot = Join-Path $PackageRoot 'licenses'
    if (Test-Path -LiteralPath $licensesRoot) { throw '许可材料目录已存在，拒绝覆盖。' }
    New-Item -ItemType Directory -Path $licensesRoot | Out-Null
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $PackageRoot 'LICENSE')
    $entries = @()
    $blockers = @()
    $distributionTermsPath = $null
    $supplementalMaterials = @()
    $notice = [Collections.Generic.List[string]]::new()
    $notice.Add('DbBackupManager 第三方许可与声明')
    $notice.Add('来源为 Web/Worker 已还原包的并集（含构建依赖）及实际自包含运行时；不是最终二进制 SBOM。')
    $notice.Add('材料齐备不等于获得分发授权；独立软件条款须另行审查，已知阻塞见 licenses/index.json。')
    $notice.Add('MIT 标准正文配合包元数据中的署名；包内原许可和第三方 NOTICE 保持原字节。')
    foreach ($key in ($components.Keys | Sort-Object)) {
        $parts = $key.Split('/')
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[A-Za-z0-9_.-]+$' -or $parts[1] -notmatch '^[A-Za-z0-9.+-]+$') {
            throw '依赖标识不能用于许可目录。'
        }
        $componentRoot = $null
        foreach ($folder in $packageFolders) {
            $candidate = Get-LicenseChildPath $folder $components[$key]
            if (Test-Path -LiteralPath $candidate -PathType Container) { $componentRoot = $candidate; break }
        }
        if (-not $componentRoot) { throw "未找到已还原包的许可来源：$key" }
        $nuspecs = @(Get-ChildItem -LiteralPath $componentRoot -Filter '*.nuspec' -File)
        if ($nuspecs.Count -ne 1) { throw "包元数据不唯一：$key" }
        $xmlSettings = [Xml.XmlReaderSettings]::new()
        $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create($nuspecs[0].FullName, $xmlSettings)
        try {
            $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader)
        }
        finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $idNode = $metadata.SelectSingleNode('*[local-name()="id"]')
        $versionNode = $metadata.SelectSingleNode('*[local-name()="version"]')
        if ($idNode.InnerText -ine $parts[0] -or $versionNode.InnerText -ine $parts[1]) {
            throw "包标识与元数据不一致：$key"
        }
        $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
        $authorsNode = $metadata.SelectSingleNode('*[local-name()="authors"]')
        $copyrightNode = $metadata.SelectSingleNode('*[local-name()="copyright"]')
        $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { 'missing' }
        $licenseValue = if ($licenseNode) { $licenseNode.InnerText } else { '' }
        $copyright = if ($copyrightNode) { $copyrightNode.InnerText } else { '' }
        $authors = if ($authorsNode) { $authorsNode.InnerText } else { '' }
        $destination = Get-LicenseChildPath $licensesRoot $key
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $nuspecs[0].FullName -Destination (Join-Path $destination 'package.nuspec')
        $originals = @(Get-ChildItem -LiteralPath $componentRoot -Recurse -File | Where-Object {
            $_.Name -match '^(licen[sc]e|copying|notice|third[-_]?party)([._-]|$)'
        })
        foreach ($original in $originals) {
            if (($original.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw '原许可文件不能是重解析点。'
            }
            $relative = [IO.Path]::GetRelativePath($componentRoot, $original.FullName)
            $originalDestination = Get-LicenseChildPath $destination (Join-Path 'original' $relative)
            New-Item -ItemType Directory -Path (Split-Path $originalDestination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $original.FullName -Destination $originalDestination
        }
        $status = 'ReviewRequired'
        if ($licenseType -eq 'expression' -and $licenseValue -ceq 'MIT') {
            if ($copyright -or $originals.Count -gt 0) {
                $status = 'MaterialsReady'
                if ($copyright) {
                    "MIT License`n`n$copyright`n`n$mitBody`n" |
                        Set-Content -LiteralPath (Join-Path $destination 'MIT.txt') -Encoding utf8NoBOM -NoNewline
                }
            }
        }
        elseif ($licenseType -eq 'file') {
            $declaredLicense = Get-LicenseChildPath $componentRoot $licenseValue
            if (-not (Test-Path -LiteralPath $declaredLicense -PathType Leaf)) {
                throw "包内声明的许可文件缺失：$key"
            }
            Copy-Item -LiteralPath $declaredLicense -Destination (Join-Path $destination 'DECLARED-LICENSE.txt')
            $review = Get-SniDistributionReview $RepositoryRoot $key $declaredLicense
            if ($null -ne $review) {
                Copy-Item -LiteralPath $review.termsPath -Destination (Join-Path $PackageRoot 'DISTRIBUTION-TERMS.md')
                $distributionTermsPath = 'DISTRIBUTION-TERMS.md'
                if ($review.approved) { $status = 'MaterialsReady' }
            }
        }
        if ($parts[0] -ieq 'Microsoft.Identity.Client.NativeInterop') { $status = 'Blocked' }
        if ($status -ne 'MaterialsReady') { $blockers += [ordered]@{ component = $key; status = $status } }
        $materials = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
        $entries += [ordered]@{
            id = $parts[0]; version = $parts[1]; authors = $authors; copyright = $copyright
            licenseType = $licenseType; license = $licenseValue; status = $status
            source = "https://www.nuget.org/packages/$($parts[0])/$($parts[1])"; materials = $materials
        }
        $notice.Add('')
        $notice.Add("$key | $licenseType : $licenseValue | $status")
        $notice.Add("署名（包元数据）：$authors | $copyright")
        $notice.Add("许可材料：licenses/$key；来源：https://www.nuget.org/packages/$key")
    }
    if (@($components.Keys | Where-Object { $_ -like 'MudBlazor/*' -and $_ -cne 'MudBlazor/9.9.0' }).Count -gt 0) {
        throw 'MudBlazor 版本变化，须更新静态资源许可审查。'
    }
    if ($components.ContainsKey('MudBlazor/9.9.0')) {
        $sourceRoot = Join-Path $RepositoryRoot 'third-party/Google.MaterialDesignIcons'
        $sourcePath = Join-Path $sourceRoot 'source.json'
        $licensePath = Join-Path $sourceRoot 'LICENSE'
        $source = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json -AsHashtable
        if ($source.consumer -cne 'MudBlazor/9.9.0' -or $source.license -cne 'Apache-2.0' -or
            $source.licenseSha256 -cne (Get-FileHash -LiteralPath $licensePath).Hash.ToLowerInvariant()) {
            throw 'Google 图标许可来源或原文哈希不一致。'
        }
        $iconDestination = Join-Path $licensesRoot 'StaticAssets/Google.MaterialDesignIcons'
        New-Item -ItemType Directory -Path $iconDestination | Out-Null
        foreach ($name in @('LICENSE', 'NOTICE.md', 'source.json')) {
            $path = Join-Path $sourceRoot $name
            $target = Join-Path $iconDestination $name
            Copy-Item -LiteralPath $path -Destination $target
            $supplementalMaterials += [ordered]@{
                path = [IO.Path]::GetRelativePath($PackageRoot, $target).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant()
            }
        }
        $notice.Add('')
        $notice.Add('Google Material Design Icons | Apache-2.0 | 通过 MudBlazor SVG 常量使用')
        $notice.Add('原许可、图标来源与转换说明：licenses/StaticAssets/Google.MaterialDesignIcons')
    }
    $report = [ordered]@{
        schemaVersion = 1
        scope = 'Web/Worker restored package union including build-time packages; self-contained runtime packs included; not a final binary SBOM'
        status = if ($blockers.Count -gt 0) { 'Blocked' } else { 'MaterialsReady' }
        distributionTermsPath = $distributionTermsPath
        supplementalMaterials = @($supplementalMaterials)
        blockers = @($blockers); components = @($entries)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licensesRoot 'index.json') -Encoding utf8NoBOM
    $notice | Set-Content -LiteralPath (Join-Path $PackageRoot 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    return $report
}

function Assert-DistributionLicenseMaterials {
    param($Report)
    if ($Report.status -cne 'MaterialsReady') {
        throw '分发许可门禁未通过；只可用于隔离验收，详情见 licenses/index.json。'
    }
}
