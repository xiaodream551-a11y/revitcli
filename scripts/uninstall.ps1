#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstall RevitCli for the current user.
.PARAMETER Purge
    Also remove user configuration (~/.revitcli/).
#>
param(
    [switch]$Purge
)

$ErrorActionPreference = "Stop"

$InstallRoot = Join-Path $env:LOCALAPPDATA "RevitCli"
$MetadataPath = Join-Path $InstallRoot "install.json"
$BinDir = Join-Path $InstallRoot "bin"
$ConfigDir = Join-Path $env:USERPROFILE ".revitcli"

Write-Host "RevitCli Uninstaller" -ForegroundColor Cyan
Write-Host ""

# ── Read install metadata ───────────────────────────────────────
$revitYears = @("2024", "2025", "2026")
if (Test-Path $MetadataPath) {
    $meta = Get-Content $MetadataPath -Raw | ConvertFrom-Json
    if ($meta.revitYears) {
        $revitYears = @($meta.revitYears)
    } elseif ($meta.revitYear) {
        $revitYears = @($meta.revitYear)
    }
    Write-Host "Found installation: v$($meta.version), Revit $($revitYears -join ', ')"
} else {
    Write-Host "No install metadata found, cleaning up all known years." -ForegroundColor Yellow
}

# ── Warn if Revit is running ───────────────────────────────────
$revitProcess = Get-Process Revit -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Host "WARNING: Revit is running. Close Revit first for a clean uninstall." -ForegroundColor Yellow
}

# ── Remove Revit manifests ─────────────────────────────────────
foreach ($year in $revitYears) {
    $manifestPath = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\RevitCli.addin"
    if (Test-Path $manifestPath) {
        Remove-Item $manifestPath -Force
        Write-Host "Removed Revit $year manifest" -ForegroundColor Green
    }
}

# ── Remove from PATH ───────────────────────────────────────────
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($userPath -like "*$BinDir*") {
    $newPath = ($userPath -split ";" | Where-Object { $_ -ne $BinDir }) -join ";"
    [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
    Write-Host "Removed $BinDir from user PATH" -ForegroundColor Green
}

# ── Remove installed files ──────────────────────────────────────
if (Test-Path $InstallRoot) {
    Remove-Item $InstallRoot -Recurse -Force
    Write-Host "Removed installation: $InstallRoot" -ForegroundColor Green
}

# ── Purge config ────────────────────────────────────────────────
if ($Purge) {
    if (Test-Path $ConfigDir) {
        Remove-Item $ConfigDir -Recurse -Force
        Write-Host "Removed configuration: $ConfigDir" -ForegroundColor Green
    }
} else {
    if (Test-Path $ConfigDir) {
        Write-Host "Configuration kept at $ConfigDir (use -Purge to remove)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
