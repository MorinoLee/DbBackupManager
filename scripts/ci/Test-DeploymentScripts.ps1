#Requires -Version 7.0
[CmdletBinding()]
param([switch]$PublishSmoke, [switch]$EventLogSmoke)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw '部署脚本回归需要 Windows。' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$deploymentRoot = Join-Path $repositoryRoot 'scripts/部署'
. (Join-Path $deploymentRoot 'RuntimeRequirements.ps1')
. (Join-Path $deploymentRoot 'LicenseMaterials.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "DbBackupManagerDeploymentTests_$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Assert-Throws {
    param([scriptblock]$Body, [string]$Pattern)
    try { & $Body | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Pattern*") { throw }
        return
    }
    throw "预期拒绝：$Pattern"
}
function Test-Case {
    param([string]$Name, [scriptblock]$Action)
    & $Action
    $script:passed++
    Write-Host "通过：$Name"
}
function New-FixturePackage {
    param([string]$Name, [bool]$SelfContained, [int]$SchemaVersion = 2)
    $root = Join-Path $fixtureRoot $Name
    foreach ($hostName in @('Web', 'Worker')) {
        $hostRoot = Join-Path $root $hostName.ToLowerInvariant()
        New-Item -ItemType Directory -Path $hostRoot -Force | Out-Null
        'synthetic executable, never run' | Set-Content (Join-Path $hostRoot "DbBackupManager.$hostName.exe")
        $frameworks = @(Get-RequiredFrameworks -SelfContained $false | Where-Object {
            $hostName -eq 'Web' -or $_.name -eq 'Microsoft.NETCore.App'
        })
        $options = [ordered]@{ tfm = 'net10.0' }
        if ($SelfContained) { $options.includedFrameworks = $frameworks }
        else {
            $options.frameworks = $frameworks
            $options.rollForward = 'LatestPatch'
        }
        @{ runtimeOptions = $options } | ConvertTo-Json -Depth 6 |
            Set-Content (Join-Path $hostRoot "DbBackupManager.$hostName.runtimeconfig.json")
    }
    $manifest = [ordered]@{
        schemaVersion = $SchemaVersion; product = 'DbBackupManager'; version = '1.0.0'
        runtimeIdentifier = 'win-x64'; selfContained = $SelfContained; sourceCommit = ('a' * 40)
        requiredFrameworks = @(Get-RequiredFrameworks -SelfContained $SelfContained)
        runtimeRollForward = if ($SelfContained) { $null } else { 'LatestPatch' }
        appHostDotNetSearch = if ($SelfContained) { $null } else { 'Global' }
        files = @(Get-ChildItem $root -Recurse -File | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($root, $_.FullName); length = $_.Length
                sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    }
    if ($SchemaVersion -eq 1) {
        $manifest.Remove('requiredFrameworks'); $manifest.Remove('runtimeRollForward'); $manifest.Remove('appHostDotNetSearch')
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'manifest.json')
    return $root
}

function New-LicenseFixture {
    param([string]$Name, [string]$Id = 'Synthetic.Package', [string]$LicenseType = 'expression', [string]$LicenseValue = 'MIT', [string]$Version = '1.0.0')
    $root = Join-Path $fixtureRoot $Name
    $cache = Join-Path $root 'cache'
    $key = "$Id/$Version"
    $libraryPath = $key.ToLowerInvariant()
    $package = Join-Path $cache $libraryPath
    $output = Join-Path $root 'output'
    New-Item -ItemType Directory -Path $package, $output -Force | Out-Null
    Copy-Item (Join-Path $repositoryRoot 'LICENSE') (Join-Path $root 'LICENSE')
    "<package><metadata><id>$Id</id><version>$Version</version><authors>Synthetic Contributors</authors><copyright>Copyright Synthetic Contributors</copyright><license type=`"$LicenseType`">$LicenseValue</license></metadata></package>" |
        Set-Content (Join-Path $package 'synthetic.nuspec')
    $assets = @{ packageFolders = @{ $cache = @{} }; libraries = @{ $key = @{ type = 'package'; path = $libraryPath } } }
    $assetsPath = Join-Path $root 'project.assets.json'
    $assets | ConvertTo-Json -Depth 5 | Set-Content $assetsPath
    return @{ root = $root; package = $package; output = $output; assets = $assetsPath }
}

try {
    # 仅加载部署函数，不执行服务部署主流程。
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $deploymentRoot 'Invoke-DbBackupManagerServiceDeployment.ps1'), [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) '部署脚本语法错误。'
    foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $sc = New-FixturePackage 'sc' $true
    $legacy = New-FixturePackage 'legacy' $true 1
    $fdd = New-FixturePackage 'fdd' $false
    $requirements = @(Get-RequiredFrameworks $false)
    Test-Case '许可材料并集去重、原文保留且报告不包含本机路径' {
        $f = New-LicenseFixture 'licenses-mit'
        $original = Join-Path $f.package 'THIRD-PARTY-NOTICES.txt'
        [IO.File]::WriteAllBytes($original, [byte[]](65, 13, 10, 66, 10))
        $report = Write-PackageLicenseMaterials $f.root $f.output @($f.assets, $f.assets) $false 'win-x64'
        Assert-True ($report.status -eq 'MaterialsReady' -and $report.components.Count -eq 1) '许可并集错误。'
        $copy = Join-Path $f.output 'licenses/Synthetic.Package/1.0.0/original/THIRD-PARTY-NOTICES.txt'
        Assert-True ((Get-FileHash $copy).Hash -ceq (Get-FileHash $original).Hash) '改写了原 NOTICE。'
        Assert-True (Test-Path (Join-Path $f.output 'LICENSE')) '缺少项目 LICENSE。'
        Assert-True ((Get-Content (Join-Path $f.output 'licenses/Synthetic.Package/1.0.0/MIT.txt') -Raw).Contains('Copyright Synthetic Contributors')) '丢失署名。'
        Assert-True (-not (Get-Content (Join-Path $f.output 'licenses/index.json') -Raw).Contains($fixtureRoot)) '报告包含机器路径。'
        Assert-DistributionLicenseMaterials $report
    }
    Test-Case 'NativeInterop 与独立软件条款拒绝分发且不改标 MIT' {
        foreach ($id in @('Microsoft.Identity.Client.NativeInterop', 'Synthetic.Proprietary')) {
            $f = New-LicenseFixture "licenses-$id" $id 'file' 'terms.txt'
            'synthetic proprietary terms' | Set-Content (Join-Path $f.package 'terms.txt')
            $report = Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64'
            Assert-True ($report.status -eq 'Blocked' -and $report.components[0].licenseType -eq 'file') '误报分发许可。'
            Assert-True (-not (Test-Path (Join-Path $f.output "licenses/$id/1.0.0/MIT.txt"))) '独立条款被改标 MIT。'
            Assert-Throws { Assert-DistributionLicenseMaterials $report } '分发许可门禁未通过*'
        }
    }
    Test-Case '未知表达式保持待审且路径越界或缺失声明许可被拒绝' {
        $f = New-LicenseFixture 'licenses-unknown' 'Synthetic.Package' 'expression' 'Apache-2.0'
        $report = Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64'
        Assert-Throws { Assert-DistributionLicenseMaterials $report } '分发许可门禁未通过*'
        foreach ($case in @(@('traversal', '../../outside.txt', '*路径越出*'), @('missing', 'missing.txt', '*许可文件缺失*'))) {
            $f = New-LicenseFixture "licenses-$($case[0])" 'Synthetic.Package' 'file' $case[1]
            Assert-Throws { Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64' } $case[2]
        }
    }
    Test-Case 'SNI 审查绑定原文和条款哈希，未批准或内容变化保持待审' {
        $cases = @('pending', 'approved', 'license-changed', 'terms-changed', 'version-changed', 'no-reference', 'string-approval')
        foreach ($case in $cases) {
            $version = if ($case -eq 'version-changed') { '6.0.4' } else { '6.0.3' }
            $f = New-LicenseFixture "sni-$case" 'Microsoft.Data.SqlClient.SNI.runtime' 'file' 'terms.txt' $version
            'synthetic original license' | Set-Content (Join-Path $f.package 'terms.txt')
            'synthetic downstream terms' | Set-Content (Join-Path $f.root 'DISTRIBUTION-TERMS.md')
            New-Item -ItemType Directory -Path (Join-Path $f.root 'scripts/部署') -Force | Out-Null
            $review = @{ schemaVersion = 1; component = 'Microsoft.Data.SqlClient.SNI.runtime/6.0.3'
                licenseSha256 = (Get-FileHash (Join-Path $f.package 'terms.txt')).Hash.ToLowerInvariant()
                termsSha256 = (Get-FileHash (Join-Path $f.root 'DISTRIBUTION-TERMS.md')).Hash.ToLowerInvariant()
                approved = $case -ne 'pending'; approvalReference = 'synthetic approval, not a product decision' }
            if ($case -eq 'no-reference') { $review.approvalReference = '' }
            if ($case -eq 'string-approval') { $review.approved = 'true' }
            $review | ConvertTo-Json | Set-Content (Join-Path $f.root 'scripts/部署/SniDistributionReview.json')
            if ($case -eq 'license-changed') { Add-Content (Join-Path $f.package 'terms.txt') 'changed' }
            if ($case -eq 'terms-changed') { Add-Content (Join-Path $f.root 'DISTRIBUTION-TERMS.md') 'changed' }
            $report = Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64'
            if ($case -eq 'approved') { Assert-DistributionLicenseMaterials $report }
            else { Assert-Throws { Assert-DistributionLicenseMaterials $report } '分发许可门禁未通过*' }
            Assert-True (-not (Test-Path (Join-Path $f.output "licenses/Microsoft.Data.SqlClient.SNI.runtime/$version/MIT.txt"))) 'SNI 被改标 MIT。'
            if ($case -ne 'version-changed') {
                Assert-True ($report.distributionTermsPath -ceq 'DISTRIBUTION-TERMS.md') '未附带下游条款。'
            }
        }
    }
    Test-Case 'Google 图标许可材料保持原文、登记哈希且内容变化拒绝' {
        foreach ($case in @('ready', 'changed', 'new-version')) {
            $version = if ($case -eq 'new-version') { '9.10.0' } else { '9.9.0' }
            $f = New-LicenseFixture "icons-$case" 'MudBlazor' 'expression' 'MIT' $version
            $materials = Join-Path $f.root 'third-party/Google.MaterialDesignIcons'
            New-Item -ItemType Directory -Path $materials -Force | Out-Null
            foreach ($name in @('LICENSE', 'NOTICE.md', 'source.json')) {
                Copy-Item -LiteralPath (Join-Path $repositoryRoot "third-party/Google.MaterialDesignIcons/$name") -Destination (Join-Path $materials $name)
            }
            if ($case -eq 'changed') { Add-Content (Join-Path $materials 'LICENSE') 'changed' }
            if ($case -eq 'changed') {
                Assert-Throws { Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64' } '图标许可来源或原文哈希不一致'
            }
            elseif ($case -eq 'new-version') {
                Assert-Throws { Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64' } '版本变化'
            }
            else {
                $report = Write-PackageLicenseMaterials $f.root $f.output @($f.assets) $false 'win-x64'
                Assert-True ($report.supplementalMaterials.Count -eq 3) '未登记图标材料。'
                foreach ($item in $report.supplementalMaterials) {
                    Assert-True ((Get-FileHash (Join-Path $f.output $item.path)).Hash.ToLowerInvariant() -ceq $item.sha256) '图标材料哈希错误。'
                }
            }
        }
    }
    Test-Case '默认发布计划自包含且无写入' {
        $before = @(Get-ChildItem $fixtureRoot -Recurse).Count
        $plan = & (Join-Path $deploymentRoot 'Publish-DbBackupManager.ps1') -Version 1.0.0 -OutputRoot $fixtureRoot | ConvertFrom-Json
        Assert-True ($plan.selfContained -and $plan.requiredFrameworks.Count -eq 0) '默认模式改变。'
        Assert-True (-not $plan.forDistribution -and $plan.licenseMaterials.Count -eq 3) '计划缺少许可边界。'
        $distributionPlan = & (Join-Path $deploymentRoot 'Publish-DbBackupManager.ps1') -Version 1.0.0 -OutputRoot $fixtureRoot -ForDistribution | ConvertFrom-Json
        Assert-True $distributionPlan.forDistribution '显式分发计划丢失门禁意图。'
        Assert-True (@(Get-ChildItem $fixtureRoot -Recurse).Count -eq $before) 'Plan 写入了文件。'
    }
    Test-Case '轻量计划区分目录并声明运行时策略' {
        $plan = & (Join-Path $deploymentRoot 'Publish-DbBackupManager.ps1') -Version 1.0.0 -OutputRoot $fixtureRoot -DeploymentMode FrameworkDependent | ConvertFrom-Json
        Assert-True (-not $plan.selfContained -and $plan.requiredFrameworks.Count -eq 2 -and
            $plan.targetDirectory.EndsWith('-framework-dependent') -and $plan.appHostDotNetSearch -eq 'Global') '轻量计划不完整。'
    }
    Test-Case '两种清单与旧自包含清单可验证' {
        foreach ($root in @($sc, $legacy, $fdd)) { Get-VerifiedPackage $root | Out-Null }
    }
    Test-Case '自包含无需机器运行时' {
        Assert-True ((Get-PackageRuntimePreflight $true @()).status -eq 'NotRequired') '自包含不应依赖服务器运行时。'
    }
    Test-Case 'x86 和 ARM64 主机拒绝，x64 接受' {
        $path = Join-Path $fixtureRoot 'synthetic-host.exe'
        foreach ($machine in @(0x14c, 0xaa64, 0x8664)) {
            $bytes = [byte[]]::new(128)
            [BitConverter]::GetBytes([int]64).CopyTo($bytes, 0x3c)
            [BitConverter]::GetBytes([int]0x00004550).CopyTo($bytes, 64)
            [BitConverter]::GetBytes([uint16]$machine).CopyTo($bytes, 68)
            [IO.File]::WriteAllBytes($path, $bytes)
            if ($machine -eq 0x8664) { Assert-X64DotNetHost $path }
            else { Assert-Throws { Assert-X64DotNetHost $path } '不是 x64 PE' }
        }
    }
    Test-Case 'LatestPatch 接受补丁并选择最高稳定版本' {
        $selected = @(Get-CompatibleFrameworks $requirements @(
            'Microsoft.NETCore.App 10.0.1 [C:\synthetic]', 'Microsoft.NETCore.App 10.0.12 [C:\synthetic]',
            'Microsoft.AspNetCore.App 10.0.12 [C:\synthetic]'))
        Assert-True ($selected.Count -eq 2 -and $selected[0].version -eq '10.0.12') '补丁选择错误。'
    }
    Test-Case '缺少 ASP.NET Core 或只有其他主次版本、预览版均拒绝' {
        foreach ($lines in @(
            ,@('Microsoft.NETCore.App 10.0.12 [C:\synthetic]'),
            ,@('Microsoft.NETCore.App 11.0.0 [C:\synthetic]'),
            ,@('Microsoft.NETCore.App 10.1.0 [C:\synthetic]'),
            ,@('Microsoft.NETCore.App 10.0.1-preview.1 [C:\synthetic]'),
            ,@()
        )) { Assert-Throws { Get-CompatibleFrameworks $requirements $lines } '缺少兼容 x64 运行时' }
    }
    Test-Case 'ASP.NET Core 较新而 NETCore 补丁过旧被拒绝' {
        Assert-Throws { Get-CompatibleFrameworks $requirements @(
            'Microsoft.NETCore.App 10.0.1 [C:\synthetic]', 'Microsoft.AspNetCore.App 10.0.12 [C:\synthetic]'
        ) } 'NETCore 补丁版本不能低于'
    }
    Test-Case '包内容篡改被哈希拦截' {
        $tampered = New-FixturePackage 'tampered' $false
        Add-Content (Join-Path $tampered 'web/DbBackupManager.Web.exe') 'changed'
        Assert-Throws { Get-VerifiedPackage $tampered } 'SHA-256'
    }
    Test-Case '清单省略框架与伪装旧版轻量包被拒绝' {
        $invalid = New-FixturePackage 'invalid' $false
        $path = Join-Path $invalid 'manifest.json'
        $manifest = Get-Content $path -Raw | ConvertFrom-Json
        $manifest.requiredFrameworks = @()
        $manifest | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Throws { Get-VerifiedPackage $invalid } '运行时依赖'
        $manifest.schemaVersion = 1
        $manifest | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Throws { Get-VerifiedPackage $invalid } '固定契约'
    }
    Test-Case '清单模式与 runtimeconfig 不一致被拒绝' {
        $invalid = New-FixturePackage 'mismatched' $false
        $path = Join-Path $invalid 'manifest.json'
        $manifest = Get-Content $path -Raw | ConvertFrom-Json
        $manifest.selfContained = $true; $manifest.requiredFrameworks = @()
        $manifest | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Throws { Get-VerifiedPackage $invalid } '自包含清单'
    }
    Test-Case '过旧补丁与非 LatestPatch 配置被拒绝' {
        Assert-Throws { Get-CompatibleFrameworks @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.12' }) @('Microsoft.NETCore.App 10.0.1 [C:\synthetic]') } '缺少兼容'
        $path = Join-Path $fdd 'web/DbBackupManager.Web.runtimeconfig.json'
        $config = Get-Content $path -Raw
        $config.Replace('LatestPatch', 'LatestMajor') | Set-Content $path
        Assert-Throws { Assert-PackageRuntimeConfiguration $fdd $false } 'LatestPatch'
        $config | Set-Content $path
    }
    Test-Case '安装升级缺少运行时均在 Apply 确认与目录写入前退出' {
        $copy = Join-Path $fixtureRoot 'scripts'
        New-Item -ItemType Directory -Path $copy | Out-Null
        Copy-Item (Join-Path $deploymentRoot '*.ps1') $copy
        # 仅在隔离副本中模拟无运行时；永不安装服务，错误确认短语再提供一道写入保护。
        Add-Content (Join-Path $copy 'RuntimeRequirements.ps1') "`nfunction Get-InstalledFrameworkLines { return @() }"
        $data = Join-Path $fixtureRoot 'data'
        foreach ($relative in @('config', 'keys/cookie', 'keys/business', 'logs/web', 'logs/worker')) {
            New-Item -ItemType Directory -Path (Join-Path $data $relative) -Force | Out-Null
        }
        '{}' | Set-Content (Join-Path $data 'config/web.json')
        '{}' | Set-Content (Join-Path $data 'config/worker.json')
        function Get-CimInstance { return $null }
        $install = Join-Path $fixtureRoot 'install'
        $package = New-FixturePackage 'preflight' $false
        foreach ($action in @('Install', 'Upgrade')) {
            Assert-Throws {
                & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -Action $action -PackageRoot $package -InstallRoot $install -DataRoot $data -Apply -Confirmation 'never authorize writes'
            } '缺少兼容 x64 运行时'
        }
        Assert-True (-not (Test-Path $install)) '前置检查失败却创建了程序目录。'
        Add-Content (Join-Path $copy 'RuntimeRequirements.ps1') "`nfunction Get-InstalledFrameworkLines { return @('Microsoft.NETCore.App 10.0.12 [C:\synthetic]', 'Microsoft.AspNetCore.App 10.0.12 [C:\synthetic]') }"
        foreach ($root in @($sc, $package)) {
            $plan = & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -PackageRoot $root -InstallRoot $install -DataRoot $data | ConvertFrom-Json
            Assert-True (-not $plan.apply) '默认部署意外执行 Apply。'
            if ($root -eq $sc) {
                Assert-True ($plan.runtimePreflight.status -eq 'NotRequired' -and $plan.requiredFrameworks.Count -eq 0) '自包含计划错误。'
            }
            else {
                Assert-True ($plan.runtimePreflight.status -eq 'Passed' -and $plan.targetVersionDirectory.EndsWith('-framework-dependent')) '轻量部署计划错误。'
                Assert-Throws {
                    & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -PackageRoot $root -InstallRoot $install -DataRoot $data -Apply -Confirmation 'never authorize writes'
                } '确认短语不匹配'
            }
        }
        Assert-True (-not (Test-Path $install)) '默认部署计划或错误确认写入了程序目录。'
        $termsPackage = New-FixturePackage 'terms-package' $true
        $termsPath = Join-Path $termsPackage 'DISTRIBUTION-TERMS.md'
        'synthetic distribution terms' | Set-Content $termsPath
        $manifestPath = Join-Path $termsPackage 'manifest.json'
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        $manifest.requiresDistributionTermsAcceptance = $true
        $manifest.distributionTermsPath = 'DISTRIBUTION-TERMS.md'
        $manifest.files += @{ path = 'DISTRIBUTION-TERMS.md'; length = (Get-Item $termsPath).Length
            sha256 = (Get-FileHash $termsPath).Hash.ToLowerInvariant() }
        $manifest | ConvertTo-Json -Depth 6 | Set-Content $manifestPath
        $plan = & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -PackageRoot $termsPackage -InstallRoot $install -DataRoot $data | ConvertFrom-Json
        Assert-True ($plan.distributionTermsPath -eq $termsPath -and -not $plan.distributionTermsAccepted) 'Plan 未呈现条款且默认接受。'
        Assert-Throws {
            & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -PackageRoot $termsPackage -InstallRoot $install -DataRoot $data -Apply -Confirmation 'Install DbBackupManager.Web DbBackupManager.Worker'
        } '必须显式传入 -AcceptDistributionTerms'
        $plan = & (Join-Path $copy 'Invoke-DbBackupManagerServiceDeployment.ps1') -PackageRoot $termsPackage -InstallRoot $install -DataRoot $data -AcceptDistributionTerms | ConvertFrom-Json
        Assert-True $plan.distributionTermsAccepted '显式接受未呈现。'
        $manifest.requiresDistributionTermsAcceptance = $false
        $manifest | ConvertTo-Json -Depth 6 | Set-Content $manifestPath
        Assert-Throws { Get-VerifiedPackage $termsPackage } '缺少接受标记'
        Assert-True (-not (Test-Path $install)) '条款拒绝或 Plan 创建了程序目录。'
    }
    if ($EventLogSmoke) {
        Test-Case 'PowerShell 7 首次注册事件源、重复注册及错误日志绑定拒绝' {
            Assert-Administrator
            $suffix = [guid]::NewGuid().ToString('N')
            $applicationSources = @("DbBackupManager.DeploymentTest.Web.$suffix", "DbBackupManager.DeploymentTest.Worker.$suffix")
            $conflictingSource = "DbBackupManager.DeploymentTest.Conflict.$suffix"
            $ownedSources = @($applicationSources) + @($conflictingSource)
            foreach ($name in $ownedSources) {
                Assert-True (-not [Diagnostics.EventLog]::SourceExists($name)) '临时事件源已经存在，拒绝使用。'
            }
            try {
                $script:serviceNames = $applicationSources
                Ensure-ServiceEventLogSources
                Ensure-ServiceEventLogSources
                foreach ($name in $applicationSources) {
                    Assert-True ([Diagnostics.EventLog]::LogNameFromSourceName($name, '.') -ceq 'Application') '首次注册未绑定 Application。'
                }
                [Diagnostics.EventLog]::CreateEventSource([Diagnostics.EventSourceCreationData]::new($conflictingSource, 'System'))
                $script:serviceNames = @($conflictingSource)
                Assert-Throws { Ensure-ServiceEventLogSources } '非 Application 日志'
                Assert-True ([Diagnostics.EventLog]::LogNameFromSourceName($conflictingSource, '.') -ceq 'System') '拒绝时修改了既有绑定。'
            }
            finally {
                foreach ($name in $ownedSources) {
                    if ([Diagnostics.EventLog]::SourceExists($name)) { [Diagnostics.EventLog]::DeleteEventSource($name) }
                }
            }
            foreach ($name in $ownedSources) {
                Assert-True (-not [Diagnostics.EventLog]::SourceExists($name)) '临时事件源没有清理。'
            }
        }
    }
    if ($PublishSmoke) {
        Test-Case '实际发布的两种 apphost 均可启动（最小宿主，无业务 I/O）' {
            $source = Join-Path $fixtureRoot 'smoke-source'
            $output = Join-Path $fixtureRoot 'smoke-packages'
            New-Item -ItemType Directory -Path (Join-Path $source 'scripts/部署'), $output -Force | Out-Null
            Copy-Item (Join-Path $deploymentRoot '*.ps1') (Join-Path $source 'scripts/部署')
            Copy-Item (Join-Path $repositoryRoot 'LICENSE') (Join-Path $source 'LICENSE')
            foreach ($hostName in @('Web', 'Worker')) {
                $projectRoot = Join-Path $source "src/DbBackupManager.$hostName"
                New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
                $sdk = if ($hostName -eq 'Web') { 'Microsoft.NET.Sdk.Web' } else { 'Microsoft.NET.Sdk' }
                "<Project Sdk=`"$sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>" |
                    Set-Content (Join-Path $projectRoot "DbBackupManager.$hostName.csproj")
                'System.Console.WriteLine("deployment-apphost-smoke-ok");' | Set-Content (Join-Path $projectRoot 'Program.cs')
            }
            & git -C $source init --quiet
            if ($LASTEXITCODE -ne 0) { throw 'Smoke Git init 失败。' }
            & git -C $source add --all
            if ($LASTEXITCODE -ne 0) { throw 'Smoke Git add 失败。' }
            & git -C $source -c user.name=DeploymentSmoke -c user.email=smoke@example.invalid commit --quiet -m 'test: isolated apphost smoke'
            if ($LASTEXITCODE -ne 0) { throw 'Smoke Git commit 失败。' }
            $savedLicense = [IO.File]::ReadAllBytes((Join-Path $source 'LICENSE'))
            try {
                'synthetic invalid license' | Set-Content (Join-Path $source 'LICENSE')
                Assert-Throws {
                    & (Join-Path $source 'scripts/部署/Publish-DbBackupManager.ps1') -Command Publish -Version 0.0.0-rejected -OutputRoot $output -DeploymentMode FrameworkDependent -ErrorAction SilentlyContinue
                } '*项目 LICENSE 缺少标准 MIT*'
                Assert-True (-not (Test-Path (Join-Path $output 'DbBackupManager-0.0.0-rejected-win-x64-framework-dependent'))) '失败发布生成了最终目录。'
            }
            finally { [IO.File]::WriteAllBytes((Join-Path $source 'LICENSE'), $savedLicense) }
            foreach ($mode in @('SelfContained', 'FrameworkDependent')) {
                & (Join-Path $source 'scripts/部署/Publish-DbBackupManager.ps1') -Command Publish -Version 0.0.0-smoke -OutputRoot $output -DeploymentMode $mode | Out-Null
                $name = 'DbBackupManager-0.0.0-smoke-win-x64'
                if ($mode -eq 'FrameworkDependent') { $name += '-framework-dependent' }
                $package = Join-Path $output $name
                $manifest = Get-Content (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
                foreach ($material in @('LICENSE', 'THIRD-PARTY-NOTICES.txt', 'licenses/index.json')) {
                    Assert-True (@($manifest.files | Where-Object path -CEQ $material).Count -eq 1) '许可材料未纳入清单。'
                }
                $licenses = Get-Content (Join-Path $package 'licenses/index.json') -Raw | ConvertFrom-Json -AsHashtable
                $runtimePacks = @($licenses.components | Where-Object {$_.id -match '\.App\.Runtime\.win-x64$'})
                Assert-True ($runtimePacks.Count -eq $(if ($mode -eq 'SelfContained') { 2 } else { 0 })) '运行时许可范围错误。'
                foreach ($runtimePack in $runtimePacks) {
                    Assert-True (@($runtimePack.materials | Where-Object {$_.path -match 'original/THIRD-PARTY-NOTICES.TXT$'}).Count -eq 1) '缺少自包含运行时 NOTICE。'
                }
                foreach ($file in $manifest.files) {
                    Assert-True ((Get-FileHash (Join-Path $package $file.path)).Hash.ToLowerInvariant() -ceq $file.sha256) '许可或程序清单哈希错误。'
                }
                foreach ($hostName in @('Web', 'Worker')) {
                    $exe = Join-Path $output "$name/$($hostName.ToLowerInvariant())/DbBackupManager.$hostName.exe"
                    $marker = & $exe
                    Assert-True ($LASTEXITCODE -eq 0 -and $marker -ceq 'deployment-apphost-smoke-ok') "$mode $hostName apphost 无法启动。"
                }
            }
        }
    }
    Write-Host "部署脚本回归：$passed 项通过（无服务写入；EventLogSmoke 仅登记并清理唯一临时事件源）。"
}
finally {
    # 只清理本脚本创建的唯一临时根，先核对解析后的路径及父目录。
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::GetTempPath().TrimEnd('\', '/') -or
        (Split-Path $resolved -Leaf) -notlike 'DbBackupManagerDeploymentTests_*') { throw '临时目录清理边界不匹配。' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
