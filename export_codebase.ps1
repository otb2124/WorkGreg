param (
    [switch]$CleanOld,
    [int]$MaxFileKB = 200,          # skip any single file larger than this
    [switch]$SkipDesignerFiles      # skip WinForms *.Designer.cs if they are just noise to you
)

# Run this from the solution root (the folder with the .sln), e.g.:
#   powershell -ExecutionPolicy Bypass -File .\export_codebase.ps1
#   powershell -ExecutionPolicy Bypass -File .\export_codebase.ps1 -CleanOld

# 1. Determine project name (.sln first, then .csproj, then folder name)
$RootPath    = (Get-Item -Path ".").FullName
$ProjectName = (Get-Item -Path ".").Name
$ExcludeDirs = '[\\/](bin|obj|\.git|\.vs|\.idea|exports|packages|node_modules|TestResults|__pycache__|\.venv|venv)[\\/]'

$SlnFile = Get-ChildItem -Path $RootPath -Filter "*.sln" -File | Select-Object -First 1
if ($SlnFile) {
    $ProjectName = [System.IO.Path]::GetFileNameWithoutExtension($SlnFile.Name)
} else {
    $CsprojFile = Get-ChildItem -Path $RootPath -Filter "*.csproj" -Recurse -File |
        Where-Object { $_.FullName -notmatch $ExcludeDirs } |
        Select-Object -First 1
    if ($CsprojFile) {
        $ProjectName = [System.IO.Path]::GetFileNameWithoutExtension($CsprojFile.Name)
    }
}
$CleanProjectName = $ProjectName -replace '[\\/:*?"<>|]', '-'

# 2. Exports folder + timestamped filename
$ExportsFolder = Join-Path -Path $RootPath -ChildPath "exports"
if (-not (Test-Path $ExportsFolder)) {
    New-Item -ItemType Directory -Path $ExportsFolder | Out-Null
}

if ($CleanOld) {
    Write-Host "Cleaning up previous export files in $ExportsFolder..." -ForegroundColor Yellow
    Get-ChildItem -Path $ExportsFolder -Filter "*.xml" | Remove-Item -Force
}

$Timestamp      = Get-Date -Format "yyyyMMdd-HHmmss"
$OutputFileName = "$CleanProjectName-codebase-$Timestamp.xml"
$OutputFilePath = Join-Path -Path $ExportsFolder -ChildPath $OutputFileName

Write-Host "Exporting .NET desktop codebase to $OutputFilePath..." -ForegroundColor Green

$StringBuilder = [System.Text.StringBuilder]::new()

# 3. Header for the LLM
[void]$StringBuilder.AppendLine("<codebase_context>")
[void]$StringBuilder.AppendLine("<overview>")
[void]$StringBuilder.AppendLine("This document contains the source code, directory structure, and configuration files for a .NET (Visual Studio) desktop application that tracks the user's hands via camera and uses the tracking to move the mouse cursor.")
[void]$StringBuilder.AppendLine("Use this context to analyze, refactor, debug, or extend the code: hand/landmark tracking, camera capture, gesture detection, cursor/input control (Win32 interop), smoothing/calibration, and the UI layer.")
[void]$StringBuilder.AppendLine("</overview>")
[void]$StringBuilder.AppendLine()

# 4. Collect all non-excluded items once
$AllItems = Get-ChildItem -Path $RootPath -Recurse |
    Where-Object { $_.FullName -notmatch $ExcludeDirs }

# 5. Directory tree
[void]$StringBuilder.AppendLine("<directory_structure>")
foreach ($item in $AllItems) {
    $relativePath = $item.FullName.Substring($RootPath.Length + 1).Replace("\", "/")
    if ($item.PSIsContainer) { $relativePath += "/" }
    [void]$StringBuilder.AppendLine($relativePath)
}
[void]$StringBuilder.AppendLine("</directory_structure>")
[void]$StringBuilder.AppendLine()

# 6. File contents
$IncludeExt = @(
    ".cs", ".xaml", ".razor", ".cshtml",
    ".csproj", ".sln", ".props", ".targets",
    ".json", ".config", ".xml", ".yaml", ".yml", ".ini",
    ".py", ".md", ".txt", ".ps1"
)

$SkipNames = @(
    '\.g\.cs$', '\.g\.i\.cs$', '\.AssemblyInfo\.cs$', '^GlobalUsings\.g\.cs$',
    '^package-lock\.json$', '^project\.assets\.json$', '\.nuget\.dgspec\.json$'
)
if ($SkipDesignerFiles) { $SkipNames += '\.Designer\.cs$' }

[void]$StringBuilder.AppendLine("<files>")

$fileCount = 0
$skipped   = 0

foreach ($file in ($AllItems | Where-Object { -not $_.PSIsContainer })) {

    if ($IncludeExt -notcontains $file.Extension.ToLower()) { continue }

    $skip = $false
    foreach ($pattern in $SkipNames) {
        if ($file.Name -match $pattern) { $skip = $true; break }
    }
    if ($skip) { continue }

    # Don't re-export previous exports or this script itself
    if ($file.Name -eq "export_codebase.ps1") { continue }

    $relativePath = $file.FullName.Substring($RootPath.Length + 1).Replace("\", "/")

    if ($file.Length -gt ($MaxFileKB * 1KB)) {
        Write-Host "Skipping (too large, $([math]::Round($file.Length / 1KB)) KB): $relativePath" -ForegroundColor DarkYellow
        [void]$StringBuilder.AppendLine("  <file path=""$relativePath"" skipped=""too large"" />")
        $skipped++
        continue
    }

    Write-Host "Processing: $relativePath" -ForegroundColor Gray

    [void]$StringBuilder.AppendLine("  <file path=""$relativePath"">")
    $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8

    if ($content) {
        # Escape closing tag if it appears inside a file
        $safeContent = $content.Replace("</file>", "<\_file>")
        [void]$StringBuilder.AppendLine($safeContent)
    }

    [void]$StringBuilder.AppendLine("  </file>")
    [void]$StringBuilder.AppendLine()
    $fileCount++
}

[void]$StringBuilder.AppendLine("</files>")
[void]$StringBuilder.AppendLine("</codebase_context>")

# 7. Save (UTF-8 without BOM)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputFilePath, $StringBuilder.ToString(), $Utf8NoBom)

$sizeKB = [math]::Round((Get-Item $OutputFilePath).Length / 1KB)
Write-Host "Export completed: $fileCount files ($skipped skipped), $sizeKB KB." -ForegroundColor Green
Write-Host "Saved to $OutputFilePath" -ForegroundColor Green