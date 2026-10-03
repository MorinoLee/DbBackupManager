<#
.SYNOPSIS
    校验仓库内所有被 Git 跟踪的 Markdown 文档。

.DESCRIPTION
    轻量文档门禁，供 docs-ci 工作流在纯文档变更时运行，避免触发完整构建测试。
    检查项：
      1. 严格 UTF-8 解码，并拒绝 UTF-8 BOM。
      2. 相对 Markdown 链接（文件和图片）目标存在。
    忽略外部链接、纯锚点，以及围栏代码块和行内代码中的示例内容。
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepositoryRoot) {
    $RepositoryRoot = (& git rev-parse --show-toplevel).Trim()
}

$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$failures = [System.Collections.Generic.List[string]]::new()
$linkCount = 0
$fileCount = 0

$ignoredSegments = '[\\/](\.git|\.vs|\.idea|bin|obj|artifacts|node_modules|TestResults)[\\/]'
$markdownFiles = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.md' |
    Where-Object { $_.FullName -notmatch $ignoredSegments }

foreach ($file in $markdownFiles) {
    $fullPath = $file.FullName
    $relativePath = [System.IO.Path]::GetRelativePath($RepositoryRoot, $fullPath) -replace '\\', '/'
    $fileCount++
    $bytes = [System.IO.File]::ReadAllBytes($fullPath)

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $failures.Add("${relativePath}: 包含 UTF-8 BOM，仓库约定为无 BOM UTF-8。")
        continue
    }

    $text = $null
    try {
        $text = $utf8Strict.GetString($bytes)
    }
    catch [System.Text.DecoderFallbackException] {
        $failures.Add("${relativePath}: 无法按严格 UTF-8 解码。")
        continue
    }

    $baseDirectory = Split-Path -Parent $fullPath
    $lines = $text -split "`n"
    $insideFence = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index].TrimEnd("`r")

        if ($line -match '^\s*(```|~~~)') {
            $insideFence = -not $insideFence
            continue
        }
        if ($insideFence) { continue }

        $scannable = [System.Text.RegularExpressions.Regex]::Replace($line, '`[^`]*`', '')
        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($scannable, '\]\(\s*([^)]+?)\s*\)')) {
            $target = $match.Groups[1].Value.Trim()
            if (-not $target) { continue }
            if ($target -match '^\s*[a-zA-Z][a-zA-Z0-9+.\-]*:') { continue }
            if ($target.StartsWith('#')) { continue }

            $path = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            try { $path = [System.Uri]::UnescapeDataString($path) } catch { }
            $path = $path.Trim()
            if ([string]::IsNullOrWhiteSpace($path)) { continue }

            $candidate = if ($path.StartsWith('/')) {
                Join-Path $RepositoryRoot ($path.TrimStart('/'))
            }
            else {
                Join-Path $baseDirectory ($path -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            }

            # 指向仓库之外的引用（如 README 中说明的同级参考项目）在 CI 中不存在，按外部引用跳过。
            $candidateFull = [System.IO.Path]::GetFullPath($candidate)
            if ([System.IO.Path]::GetRelativePath($RepositoryRoot, $candidateFull).StartsWith('..')) {
                continue
            }

            $linkCount++
            if (-not (Test-Path -LiteralPath $candidateFull)) {
                $failures.Add("${relativePath}:$($index + 1): 相对链接目标不存在 -> $target")
            }
        }
    }
}

Write-Host "已检查 $fileCount 个 Markdown 文件、$linkCount 个相对链接。"
if ($failures.Count -gt 0) {
    Write-Host "发现 $($failures.Count) 个问题："
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}

Write-Host '文档检查通过。'
