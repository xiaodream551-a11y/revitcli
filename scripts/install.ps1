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
    Default: auto-detect installed Revit versions.
.PARAMETER Force
    Overwrite existing installation without prompting.
#>
param(
    [string]$RevitYears = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$SupportedYears = @("2024", "2025", "2026")

# ── Paths ────────────────────────────────────────────────────────
$InstallRoot  = Join-Path $env:LOCALAPPDATA "RevitCli"
$BinDir       = Join-Path $InstallRoot "bin"
$MetadataPath = Join-Path $InstallRoot "install.json"

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcBin       = Join-Path $ScriptDir "bin"

# ── Pre-checks ──────────────────────────────────────────────────
Write-Host "RevitCli Installer" -ForegroundColor Cyan
Write-Host ""

# Check source directories
if (-not (Test-Path $SrcBin)) {
    Write-Host "ERROR: bin/ directory not found next to install.ps1" -ForegroundColor Red
    Write-Host "Make sure you extracted the full ZIP archive."
    exit 1
}

# Determine which Revit years to install
if ($RevitYears -ne "") {
    $targetYears = $RevitYears -split "," | ForEach-Object { $_.Trim() }
} else {
    # Auto-detect: check which years have add-in packages AND Revit installed
    $targetYears = @()
    foreach ($year in $SupportedYears) {
        $srcAddinYear = Join-Path $ScriptDir "addin\$year"
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

# Validate source add-in directories exist
foreach ($year in $targetYears) {
    $srcAddinYear = Join-Path $ScriptDir "addin\$year"
    if (-not (Test-Path $srcAddinYear)) {
        Write-Host "ERROR: addin/$year/ directory not found in package." -ForegroundColor Red
        exit 1
    }
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

# ── Install Add-in per year ─────────────────────────────────────
$installedYears = @()

foreach ($year in $targetYears) {
    $srcAddinYear = Join-Path $ScriptDir "addin\$year"
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
    version      = "0.1.0"
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
