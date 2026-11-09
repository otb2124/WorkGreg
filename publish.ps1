<#
.SYNOPSIS
    Publishes WorkGreg into a clean folder and (optionally) zips it for upload.

.EXAMPLES
    .\publish.ps1                          # uses the defaults below
    .\publish.ps1 -Platform win            # build for running on this PC
    .\publish.ps1 -Platform pi             # Raspberry Pi (64-bit OS)
    .\publish.ps1 -NoZip                   # skip the zip
    .\publish.ps1 -Platform linux -Configuration Debug
#>
param(
    # linux = Linux VM (Google Cloud, VPS) | win = Windows | pi = Raspberry Pi 64-bit | mac = Apple Silicon
    [ValidateSet('linux', 'win', 'pi', 'mac')]
    [string]$Platform = 'linux',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Output folder and zip name (relative to this script's folder)
    [string]$OutputDir = 'publish',
    [string]$ZipName = 'publish.zip',

    # Include the .NET runtime so the target needs nothing installed
    [bool]$SelfContained = $true,

    # One executable instead of many dlls
    [bool]$SingleFile = $true,

    # Create the zip after publishing
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# ---- Settings you may want to change -------------------------------------------------------------

$Project = 'src/WorkGreg.Host/WorkGreg.Host.csproj'

$RuntimeByPlatform = @{
    linux = 'linux-x64'
    win   = 'win-x64'
    pi    = 'linux-arm64'
    mac   = 'osx-arm64'
}

# Files that must exist in the output, or the app will not start.
$RequiredFiles = @('appsettings.json', 'providers.json')

# Files that must never end up in the output (secrets for development, local databases, debug symbols).
$ForbiddenPatterns = @('appsettings.Development.json', 'appsettings.Production.json', 'secrets.json', 'WorkGreg.db*', '*.pdb')

# --------------------------------------------------------------------------------------------------

$Runtime = $RuntimeByPlatform[$Platform]
$Exe = if ($Platform -eq 'win') { 'WorkGreg.exe' } else { 'WorkGreg' }

Write-Host "Platform:      $Platform ($Runtime)"
Write-Host "Configuration: $Configuration"
Write-Host "Self-contained: $SelfContained, single file: $SingleFile"
Write-Host "Output:        $OutputDir"
Write-Host ''

# Clean start, so files from older builds can never sneak in.
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
if (Test-Path $ZipName) { Remove-Item $ZipName -Force }

$args = @(
    'publish', $Project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', $SelfContained.ToString().ToLower(),
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLower())",
    '-o', $OutputDir
)
if ($SingleFile) { $args += '-p:IncludeNativeLibrariesForSelfExtract=true' }

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

# Remove anything that should not be shipped.
foreach ($pattern in $ForbiddenPatterns) {
    Get-ChildItem -Path $OutputDir -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

# Check what the app needs.
$missing = $RequiredFiles | Where-Object { -not (Test-Path (Join-Path $OutputDir $_)) }
if (-not (Test-Path (Join-Path $OutputDir $Exe))) { $missing += $Exe }
if ($missing) { throw "Missing from the output: $($missing -join ', ')" }

# Secrets are never shipped. Create secrets.json once on the target, next to the executable.
Write-Host 'Reminder: secrets.json is not included. It must already exist on the target (Bot:Token and Api:ApiKey).'

Write-Host ''
Write-Host 'Output contents:'
Get-ChildItem $OutputDir -Recurse -File |
    Select-Object @{ n = 'File'; e = { $_.FullName.Substring((Resolve-Path $OutputDir).Path.Length + 1) } },
                  @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $ZipName -Force
    Write-Host "Created $ZipName ($([math]::Round((Get-Item $ZipName).Length / 1MB, 1)) MB)"
    if ($Platform -ne 'win') {
        Write-Host "On the server, run: chmod +x /opt/workgreg/$Exe after unzipping."
    }
}

Write-Host ''
Write-Host 'Done.'