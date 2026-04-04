#Requires -Version 5.1
<#
.SYNOPSIS
    Install RevitCli (CLI + Revit Add-in) for the current user.
.DESCRIPTION
    Copies CLI binaries to %LocalAppData%\RevitCli\bin,
    add-in binaries to %LocalAppData%\RevitCli\addin\2026,
    generates the Revit manifest, and adds the CLI to PATH.
.PARAMETER RevitYear
    Target Revit version year. Default: 2026.
.PARAMETER Force
    Overwrite existing installation without prompting.
#>
param(
    [string]$RevitYear = "2026",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────
$InstallRoot  = Join-Path $env:LOCALAPPDATA "RevitCli"
$BinDir       = Join-Path $InstallRoot "bin"
$AddinDir     = Join-Path $InstallRoot "addin\$RevitYear"
$RevitAddins  = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
$ManifestPath = Join-Path $RevitAddins "RevitCli.addin"
$MetadataPath = Join-Path $InstallRoot "install.json"

# Source directories (relative to this script)
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcBin       = Join-Path $ScriptDir "bin"
$SrcAddin     = Join-Path $ScriptDir "addin"

# ── Pre-checks ──────────────────────────────────────────────────
Write-Host "RevitCli Installer" -ForegroundColor Cyan
Write-Host ""

# Check source directories exist
if (-not (Test-Path $SrcBin)) {
    Write-Host "ERROR: bin/ directory not found next to install.ps1" -ForegroundColor Red
    Write-Host "Make sure you extracted the full ZIP archive."
    exit 1
}
if (-not (Test-Path $SrcAddin)) {
    Write-Host "ERROR: addin/ directory not found next to install.ps1" -ForegroundColor Red
    Write-Host "Make sure you extracted the full ZIP archive."
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

# ── Install Add-in ──────────────────────────────────────────────
Write-Host "Installing Add-in to $AddinDir ..." -ForegroundColor Green
New-Item -ItemType Directory -Path $AddinDir -Force | Out-Null
Copy-Item -Path "$SrcAddin\*" -Destination $AddinDir -Recurse -Force

# ── Generate Revit manifest ────────────────────────────────────
Write-Host "Writing Revit manifest to $ManifestPath ..." -ForegroundColor Green
New-Item -ItemType Directory -Path $RevitAddins -Force | Out-Null

$assemblyPath = Join-Path $AddinDir "RevitCli.Addin.dll"
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
Set-Content -Path $ManifestPath -Value $manifest -Encoding UTF8

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
    version   = "0.1.0"
    revitYear = $RevitYear
    binDir    = $BinDir
    addinDir  = $AddinDir
    manifest  = $ManifestPath
    timestamp = (Get-Date -Format "o")
} | ConvertTo-Json
Set-Content -Path $MetadataPath -Value $metadata -Encoding UTF8

# ── Done ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start (or restart) Revit $RevitYear"
Write-Host "  2. Open a project"
Write-Host "  3. Open a NEW terminal and run:"
Write-Host "       revitcli doctor" -ForegroundColor White
Write-Host "       revitcli status" -ForegroundColor White
Write-Host ""
Write-Host "Installed to: $InstallRoot"
