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
$ConfigDir = Join-Path $env:USERPROFILE ".revitcli"

Write-Host "RevitCli Uninstaller" -ForegroundColor Cyan
Write-Host ""

# ── Read install metadata ───────────────────────────────────────
$revitYear = "2026"
if (Test-Path $MetadataPath) {
    $meta = Get-Content $MetadataPath -Raw | ConvertFrom-Json
    $revitYear = $meta.revitYear
    Write-Host "Found installation: v$($meta.version), Revit $revitYear"
} else {
    Write-Host "No install metadata found, using defaults." -ForegroundColor Yellow
}

$BinDir = Join-Path $InstallRoot "bin"
$ManifestPath = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$revitYear\RevitCli.addin"

# ── Warn if Revit is running ───────────────────────────────────
$revitProcess = Get-Process Revit -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Host "WARNING: Revit is running. Close Revit first for a clean uninstall." -ForegroundColor Yellow
}

# ── Remove Revit manifest ──────────────────────────────────────
if (Test-Path $ManifestPath) {
    Remove-Item $ManifestPath -Force
    Write-Host "Removed Revit manifest: $ManifestPath" -ForegroundColor Green
} else {
    Write-Host "No Revit manifest found." -ForegroundColor DarkGray
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
} else {
    Write-Host "No installation directory found." -ForegroundColor DarkGray
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
