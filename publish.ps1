<#
.SYNOPSIS
    Publishes WorkGreg (the core/API and the Telegram bot) into clean folders and (optionally) zips them for upload.

.EXAMPLES
    .\publish.ps1                          # core + bot for linux, defaults below
    .\publish.ps1 -Target host             # only the core
    .\publish.ps1 -Target bot              # only the bot
    .\publish.ps1 -Platform win            # build for running on this PC
    .\publish.ps1 -Platform pi             # Raspberry Pi (64-bit OS)
    .\publish.ps1 -NoZip                   # skip the zips
    .\publish.ps1 -Platform linux -Configuration Debug
#>
param(
    # all = core and bot | host = core (REST API + polling) | bot = Telegram bot
    [ValidateSet('all', 'host', 'bot')]
    [string]$Target = 'all',

    # linux = Linux VM (Google Cloud, VPS) | win = Windows | pi = Raspberry Pi 64-bit | mac = Apple Silicon
    [ValidateSet('linux', 'win', 'pi', 'mac')]
    [string]$Platform = 'linux',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Output root (relative to this script's folder). Each component goes into its own subfolder and its own zip:
    # publish/host + publish-host.zip, publish/bot + publish-bot.zip.
    [string]$OutputDir = 'publish',
    [string]$ZipPrefix = 'publish',

    # Include the .NET runtime so the target needs nothing installed
    [bool]$SelfContained = $true,

    # One executable instead of many dlls
    [bool]$SingleFile = $true,

    # Create the zips after publishing
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# ---- Settings you may want to change -------------------------------------------------------------

# The two programs. They have different appsettings.json files, so each one gets its own folder.
# Exe is the file name without extension (the assembly name).
$Components = @(
    @{
        Name     = 'host'
        Project  = 'src/WorkGreg.Host/WorkGreg.Host.csproj'
        Exe      = 'WorkGreg'
        Required = @('appsettings.json', 'providers.json')
        Secrets  = 'Bot:Token and Api:ApiKey'
    },
    @{
        Name     = 'bot'
        Project  = 'src/WorkGreg.Bot/WorkGreg.Bot.csproj'
        Exe      = 'WorkGreg.Bot'
        Required = @('appsettings.json')
        Secrets  = 'Bot:Token, Core:ApiKey (same value as the core''s Api:ApiKey), and Core:BaseUrl if the core is not on localhost:5080'
    }
)

$RuntimeByPlatform = @{
    linux = 'linux-x64'
    win   = 'win-x64'
    pi    = 'linux-arm64'
    mac   = 'osx-arm64'
}

# Files that must never end up in the output (secrets for development, local databases, debug symbols).
$ForbiddenPatterns = @('appsettings.Development.json', 'appsettings.Production.json', 'secrets.json', 'WorkGreg.db*', '*.pdb')

# --------------------------------------------------------------------------------------------------

$Runtime = $RuntimeByPlatform[$Platform]
$Selected = $Components | Where-Object { $Target -eq 'all' -or $_.Name -eq $Target }

Write-Host "Target:        $Target"
Write-Host "Platform:      $Platform ($Runtime)"
Write-Host "Configuration: $Configuration"
Write-Host "Self-contained: $SelfContained, single file: $SingleFile"
Write-Host "Output:        $OutputDir"
Write-Host ''

# Clean start, so files from older builds can never sneak in. A partial publish (-Target host) leaves the other
# component's folder alone; a full one wipes everything.
if ($Target -eq 'all') {
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($c in $Selected) {
    $Out = Join-Path $OutputDir $c.Name
    $Zip = "$ZipPrefix-$($c.Name).zip"
    $Exe = if ($Platform -eq 'win') { "$($c.Exe).exe" } else { $c.Exe }

    Write-Host "==== $($c.Name) ===="

    if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
    if (Test-Path $Zip) { Remove-Item $Zip -Force }

    $publishArgs = @(
        'publish', $c.Project,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $SelfContained.ToString().ToLower(),
        "-p:PublishSingleFile=$($SingleFile.ToString().ToLower())",
        '-o', $Out
    )
    if ($SingleFile) { $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($c.Name) (exit code $LASTEXITCODE)." }

    # Remove anything that should not be shipped.
    foreach ($pattern in $ForbiddenPatterns) {
        Get-ChildItem -Path $Out -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    # Check what the app needs.
    $missing = @($c.Required | Where-Object { -not (Test-Path (Join-Path $Out $_)) })
    if (-not (Test-Path (Join-Path $Out $Exe))) { $missing += $Exe }
    if ($missing.Count -gt 0) { throw "Missing from the $($c.Name) output: $($missing -join ', ')" }

    Write-Host ''
    Write-Host "Output contents ($($c.Name)):"
    $root = (Resolve-Path $Out).Path
    Get-ChildItem $Out -Recurse -File |
        Select-Object @{ n = 'File'; e = { $_.FullName.Substring($root.Length + 1) } },
                      @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize

    # Secrets are never shipped. Create secrets.json once on the target, next to the executable.
    Write-Host "Reminder: secrets.json is not included. It must already exist on the target ($($c.Secrets))."

    if (-not $NoZip) {
        Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $Zip -Force
        Write-Host "Created $Zip ($([math]::Round((Get-Item $Zip).Length / 1MB, 1)) MB)"
        if ($Platform -ne 'win') {
            Write-Host "On the server, run: chmod +x $Exe after unzipping."
        }
    }
    Write-Host ''
}

Write-Host 'Done.'