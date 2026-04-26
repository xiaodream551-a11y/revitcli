#Requires -Version 5.1
<#
.SYNOPSIS
    Install RevitCli (CLI + Revit Add-in) for the current user.
.DESCRIPTION
    Copies CLI binaries to %LocalAppData%\RevitCli\bin,
    add-in binaries per Revit year, generates manifests,
    and adds the CLI to PATH.
.PARAMETER RevitYears
    Comma-separated Revit years to install for (e.g. "2024,2025,2026").
    Default: 2026.
.PARAMETER Configuration
    Build configuration to publish from source-tree mode.
.PARAMETER RevitInstallDir
    Optional Revit install directory override for Revit 2026 source-tree builds.
.PARAMETER SkipBuild
    In source-tree mode, use existing .artifacts/install outputs instead of publishing.
.PARAMETER Force
    Overwrite existing installation without prompting.
#>
param(
    [string]$RevitYears = "2026",
    [string]$Configuration = "Release",
    [string]$RevitInstallDir = "",
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$SupportedYears = @("2024", "2025", "2026")

# ── Paths ────────────────────────────────────────────────────────
$InstallRoot  = Join-Path $env:LOCALAPPDATA "RevitCli"
$BinDir       = Join-Path $InstallRoot "bin"
$MetadataPath = Join-Path $InstallRoot "install.json"

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $ScriptDir
$ArtifactsRoot = Join-Path $RepoRoot ".artifacts\install"
$SourceTreeMode = Test-Path (Join-Path $RepoRoot "revitcli.sln")
$SrcBin       = if ($SourceTreeMode) { Join-Path $ArtifactsRoot "bin" } else { Join-Path $ScriptDir "bin" }
$SemVerPattern = '^(?:v)?(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-AddinTargetFramework {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Year
    )

    if ($Year -eq "2024") {
        return "net48"
    }

    return "net8.0-windows"
}

function Get-SourceAddinDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Year
    )

    if ($SourceTreeMode) {
        return (Join-Path $ArtifactsRoot "addin\$Year")
    }

    return (Join-Path $ScriptDir "addin\$Year")
}

function Test-RevitCliVersion {
    param([string]$Version)
    return ($Version -match $SemVerPattern)
}

function Get-RevitCliVersion {
    param([string]$ExePath)

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "RevitCli executable not found: $ExePath"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $versionOutput = & $ExePath --version 2>&1
        $versionExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $text = ($versionOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($versionExitCode -ne 0) {
        throw "'$ExePath --version' exited $versionExitCode`: $text"
    }

    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^revitcli\s+(.+)$') {
            $version = $Matches[1].Trim()
            if (Test-RevitCliVersion -Version $version) {
                return $version
            }
            throw "RevitCli version is not valid SemVer: $version"
        }
    }

    throw "'$ExePath --version' did not return a 'revitcli <version>' line: $text"
}

function Publish-SourceTreePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Years
    )

    if (-not $SourceTreeMode) {
        return
    }

    if ($SkipBuild) {
        Write-Host "Source-tree mode: skipping build; using $ArtifactsRoot" -ForegroundColor DarkGray
        return
    }

    $resolvedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
    $resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
    if (-not $resolvedArtifactsRoot.StartsWith($resolvedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear artifacts outside repository root: $ArtifactsRoot"
    }

    if (Test-Path -LiteralPath $ArtifactsRoot) {
        Remove-Item -LiteralPath $ArtifactsRoot -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($ArtifactsRoot) | Out-Null

    Write-Host "Publishing CLI from source tree to $SrcBin ..." -ForegroundColor Green
    Invoke-DotNet -Arguments @(
        "publish",
        (Join-Path $RepoRoot "src\RevitCli\RevitCli.csproj"),
        "-c", $Configuration,
        "-o", $SrcBin
    )

    foreach ($year in $Years) {
        $srcAddinYear = Get-SourceAddinDir -Year $year
        $framework = Get-AddinTargetFramework -Year $year
        $publishArgs = @(
            "publish",
            (Join-Path $RepoRoot "src\RevitCli.Addin\RevitCli.Addin.csproj"),
            "-c", $Configuration,
            "-f", $framework,
            "-o", $srcAddinYear,
            "-p:RevitYear=$year"
        )

        if (($year -eq "2026") -and ($RevitInstallDir -ne "")) {
            $publishArgs += "-p:RevitInstallDir=$RevitInstallDir"
        }

        Write-Host "Publishing Add-in for Revit $year from source tree to $srcAddinYear ..." -ForegroundColor Green
        Invoke-DotNet -Arguments $publishArgs
    }
}

# ── Pre-checks ──────────────────────────────────────────────────
Write-Host "RevitCli Installer" -ForegroundColor Cyan
Write-Host ""

# Determine which Revit years to install
if ($PSBoundParameters.ContainsKey("RevitYears")) {
    if ([string]::IsNullOrWhiteSpace($RevitYears)) {
        Write-Host "ERROR: No Revit years specified." -ForegroundColor Red
        Write-Host "Supported years: $($SupportedYears -join ', ')"
        exit 1
    }

    $targetYears = @($RevitYears -split "," | ForEach-Object { $_.Trim() })
    $emptyYearTokens = @($targetYears | Where-Object { $_ -eq "" })
    if ($emptyYearTokens.Count -gt 0) {
        Write-Host "ERROR: Empty Revit year token in -RevitYears '$RevitYears'." -ForegroundColor Red
        Write-Host "Use a comma-separated list like: 2026 or 2024,2025,2026"
        exit 1
    }
} elseif ($SourceTreeMode) {
    $targetYears = @("2026")
} else {
    $targetYears = @()
    foreach ($year in $SupportedYears) {
        $srcAddinYear = Get-SourceAddinDir -Year $year
        if (Test-Path $srcAddinYear) {
            $targetYears += $year
        }
    }
    if ($targetYears.Count -eq 0) {
        Write-Host "ERROR: No addin/<year> directories found in the package." -ForegroundColor Red
        exit 1
    }
    Write-Host "Detected add-in packages for: $($targetYears -join ', ')" -ForegroundColor DarkGray
}

$unsupportedYears = @($targetYears | Where-Object { $SupportedYears -notcontains $_ })
if ($unsupportedYears.Count -gt 0) {
    Write-Host "ERROR: Unsupported Revit year(s): $($unsupportedYears -join ', ')" -ForegroundColor Red
    Write-Host "Supported years: $($SupportedYears -join ', ')"
    exit 1
}

Publish-SourceTreePackage -Years $targetYears

# Check source directories
if (-not (Test-Path -LiteralPath $SrcBin)) {
    Write-Host "ERROR: bin/ directory not found in install package." -ForegroundColor Red
    if ($SourceTreeMode) {
        Write-Host "Run without -SkipBuild, or build source-tree artifacts under $ArtifactsRoot."
    } else {
        Write-Host "Make sure you extracted the full ZIP archive."
    }
    exit 1
}

# Validate source add-in directories exist
foreach ($year in $targetYears) {
    $srcAddinYear = Get-SourceAddinDir -Year $year
    if (-not (Test-Path -LiteralPath $srcAddinYear)) {
        Write-Host "ERROR: addin/$year/ directory not found in install package." -ForegroundColor Red
        if ($SourceTreeMode) {
            Write-Host "Run without -SkipBuild, or build source-tree artifacts under $ArtifactsRoot."
        }
        exit 1
    }
}

$sourceCliExe = Join-Path $SrcBin "RevitCli.exe"
try {
    $installedVersion = Get-RevitCliVersion -ExePath $sourceCliExe
} catch {
    Write-Host "ERROR: Failed to validate source CLI before modifying installation." -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

# Warn if Revit is running
$revitProcess = Get-Process Revit -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Host "WARNING: Revit is running. Close Revit before installing to avoid locked files." -ForegroundColor Yellow
    if (-not $Force) {
        $answer = Read-Host "Continue anyway? (y/N)"
        if ($answer -ne "y") {
            Write-Host "Installation cancelled."
            exit 0
        }
    }
}

# Check for existing installation
if ((Test-Path $BinDir) -and -not $Force) {
    Write-Host "Existing installation found at $InstallRoot" -ForegroundColor Yellow
    $answer = Read-Host "Overwrite? (y/N)"
    if ($answer -ne "y") {
        Write-Host "Installation cancelled."
        exit 0
    }
}

# ── Install CLI ─────────────────────────────────────────────────
Write-Host "Installing CLI to $BinDir ..." -ForegroundColor Green
New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
Copy-Item -Path "$SrcBin\*" -Destination $BinDir -Recurse -Force

$installedCliExe = Join-Path $BinDir "RevitCli.exe"
if (-not (Test-Path -LiteralPath $installedCliExe)) {
    Write-Host "ERROR: Installed CLI executable not found at $installedCliExe" -ForegroundColor Red
    exit 1
}

# ── Install Add-in per year ─────────────────────────────────────
$installedYears = @()

foreach ($year in $targetYears) {
    $srcAddinYear = Get-SourceAddinDir -Year $year
    $addinDir     = Join-Path $InstallRoot "addin\$year"
    $revitAddins  = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $manifestPath = Join-Path $revitAddins "RevitCli.addin"
    $assemblyPath = Join-Path $addinDir "RevitCli.Addin.dll"

    Write-Host "Installing Add-in for Revit $year ..." -ForegroundColor Green

    # Copy add-in binaries
    New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
    Copy-Item -Path "$srcAddinYear\*" -Destination $addinDir -Recurse -Force

    # Generate Revit manifest
    New-Item -ItemType Directory -Path $revitAddins -Force | Out-Null
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitCli</Name>
    <Assembly>$assemblyPath</Assembly>
    <FullClassName>RevitCli.Addin.RevitCliApp</FullClassName>
    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>
    <VendorId>RevitCli</VendorId>
    <VendorDescription>https://github.com/xiaodream551-a11y/revitcli</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
    $installedYears += $year
}

# ── Add to PATH ─────────────────────────────────────────────────
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($userPath -notlike "*$BinDir*") {
    Write-Host "Adding $BinDir to user PATH ..." -ForegroundColor Green
    [Environment]::SetEnvironmentVariable("PATH", "$userPath;$BinDir", "User")
    $env:PATH = "$env:PATH;$BinDir"
} else {
    Write-Host "PATH already contains $BinDir" -ForegroundColor DarkGray
}

# ── Write install metadata ──────────────────────────────────────
$metadata = @{
    version      = $installedVersion
    revitYears   = $installedYears
    binDir       = $BinDir
    installRoot  = $InstallRoot
    timestamp    = (Get-Date -Format "o")
} | ConvertTo-Json
Set-Content -Path $MetadataPath -Value $metadata -Encoding UTF8

# ── Done ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host "  Revit years: $($installedYears -join ', ')"
Write-Host "  CLI:         $BinDir"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start (or restart) Revit"
Write-Host "  2. Open a project"
Write-Host "  3. Open a NEW terminal and run:"
Write-Host "       revitcli doctor" -ForegroundColor White
Write-Host "       revitcli status" -ForegroundColor White
