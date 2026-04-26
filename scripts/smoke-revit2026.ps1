#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the internal Revit 2026 real-usability smoke slice.
.DESCRIPTION
    Validates the intended baseline chain:
    doctor -> status -> query --id -> query <category> --filter ->
    set --dry-run -> set -> query confirm -> restore.

    The filter must match exactly one element and should not depend on the
    parameter being written, so the restore command can target the same element.
.EXAMPLE
    .\scripts\smoke-revit2026.ps1 `
      -ElementId 12345 `
      -RevitInstallDir 'D:\revit2026\Revit 2026' `
      -Category walls `
      -Filter 'Mark = W-01' `
      -Param Comments `
      -Value 'revitcli smoke' `
      -Apply
#>
param(
    [Parameter(Mandatory = $true)]
    [long]$ElementId,

    [string]$Category = "walls",

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [string]$Param = "Comments",

    [string]$Value = "revitcli-smoke",

    [string]$RevitCli = "revitcli",

    [string]$RevitInstallDir = "",

    [switch]$Apply,

    [switch]$FixDryRun,

    [switch]$FixApply,

    [string]$FixCheckName = "default",

    [string]$FixProfile = "",

    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$SemVerPattern = '^(?:v)?(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

function Resolve-Revit2026InstallDir {
    param([string]$OverridePath)
    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) { return $OverridePath }
    if ($env:REVITCLI_REVIT2026_INSTALL_DIR) { return $env:REVITCLI_REVIT2026_INSTALL_DIR }
    if ($env:Revit2026InstallDir) { return $env:Revit2026InstallDir }
    return (Join-Path $env:ProgramFiles "Autodesk\Revit 2026")
}

function Assert-FileExists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label missing: $Path"
    }
}

function Get-AssemblyVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    try {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if ($versionInfo.ProductVersion) {
            return $versionInfo.ProductVersion.Trim()
        }
    } catch {
        return ""
    }

    return ""
}

function Resolve-ManifestAssemblyPath {
    param([string]$ManifestPath)

    [xml]$manifestXml = Get-Content -Raw -LiteralPath $ManifestPath
    $assembly = @($manifestXml.RevitAddIns.AddIn) |
        ForEach-Object { $_.Assembly } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($assembly)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($assembly)) {
        return $assembly
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ManifestPath) $assembly))
}

function Get-CliVersionMetadata {
    param([string]$Command)

    try {
        $output = & $Command --version 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    } catch {
        return [pscustomobject]@{
            Version = ""
            Error = "Failed to run '$Command --version': $($_.Exception.Message)"
        }
    }

    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        return [pscustomobject]@{
            Version = ""
            Error = "'$Command --version' exited $exitCode`: $text"
        }
    }

    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^revitcli\s+(.+)$') {
            $version = $Matches[1].Trim()
            if ($version -notmatch $SemVerPattern) {
                return [pscustomobject]@{
                    Version = ""
                    Error = "'$Command --version' returned non-SemVer version: $version"
                }
            }

            return [pscustomobject]@{
                Version = $version
                Error = ""
            }
        }
    }

    return [pscustomobject]@{
        Version = ""
        Error = "'$Command --version' did not return a 'revitcli <version>' line: $text"
    }
}

function Invoke-RevitCliSmoke {
    param(
        [string[]]$CommandArgs,
        [int]$ExpectedExitCode = 0
    )

    $output = & $RevitCli @CommandArgs 2>&1
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $entry = [ordered]@{
        command = "$RevitCli $($CommandArgs -join ' ')"
        exitCode = $exitCode
        output = $text
    }
    $script:Steps.Add($entry) | Out-Null

    if ($exitCode -ne $ExpectedExitCode) {
        throw "Command failed with exit code ${exitCode}: $($entry.command)`n$text"
    }

    return $text
}

function Convert-JsonArray {
    param([string]$Json, [string]$Label)
    try {
        $value = $Json | ConvertFrom-Json
    } catch {
        throw "$Label did not return valid JSON: $($_.Exception.Message)`n$Json"
    }

    if ($null -eq $value) {
        throw "$Label returned empty JSON."
    }

    if ($value -is [array]) { return @($value) }
    return @($value)
}

function Get-ElementParameterProperty {
    param([object]$Element, [string]$ParameterName, [string]$Context)

    if ($null -eq $Element.parameters) {
        throw "$Context returned no parameters object for element $($Element.id)."
    }

    $property = $Element.parameters.PSObject.Properties |
        Where-Object { $_.Name -eq $ParameterName } |
        Select-Object -First 1

    if ($null -eq $property) {
        throw "$Context did not expose parameter '$ParameterName' for element $($Element.id)."
    }

    return $property
}

function Assert-DryRunPreview {
    param([string]$Text, [long]$ElementId, [string]$OldValue, [string]$NewValue)

    $idNeedle = "[$ElementId]"
    $transitionNeedle = '"' + $OldValue + '" -> "' + $NewValue + '"'
    if (-not $Text.Contains($idNeedle)) {
        throw "set --dry-run preview did not include target element id $ElementId.`n$Text"
    }
    if (-not $Text.Contains($transitionNeedle)) {
        throw "set --dry-run preview did not include expected value transition $transitionNeedle.`n$Text"
    }
}

function Get-LatestFixBaseline {
    param([string]$RootPath)

    $fixDirectory = Join-Path $RootPath ".revitcli"
    if (-not (Test-Path -LiteralPath $fixDirectory)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $fixDirectory -Filter "fix-baseline-*.json" -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-FixJournalPath {
    param([string]$BaselinePath)

    if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
        return ""
    }

    $fullPath = [System.IO.Path]::GetFullPath($BaselinePath)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
    return Join-Path $directory "$name.fixjournal.json"
}

$Steps = [System.Collections.Generic.List[object]]::new()
$installDir = Resolve-Revit2026InstallDir -OverridePath $RevitInstallDir
$manifestPath = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026\RevitCli.addin"
$serverInfoPath = Join-Path $env:USERPROFILE ".revitcli\server.json"

Assert-FileExists (Join-Path $installDir "RevitAPI.dll") "Revit 2026 API DLL"
Assert-FileExists (Join-Path $installDir "RevitAPIUI.dll") "Revit 2026 API UI DLL"
Assert-FileExists $manifestPath "RevitCli 2026 add-in manifest"
Assert-FileExists $serverInfoPath "RevitCli server.json"

$manifestAssemblyPath = Resolve-ManifestAssemblyPath $manifestPath
Assert-FileExists $manifestAssemblyPath "RevitCli manifest assembly"
$installedAddinVersion = Get-AssemblyVersion $manifestAssemblyPath
$cliVersionMetadata = Get-CliVersionMetadata -Command $RevitCli
$cliVersion = $cliVersionMetadata.Version
$cliVersionError = $cliVersionMetadata.Error
if (-not [string]::IsNullOrWhiteSpace($cliVersionError) -or [string]::IsNullOrWhiteSpace($cliVersion)) {
    throw "CLI version metadata unavailable: $cliVersionError"
}

$serverInfo = Get-Content -Raw -LiteralPath $serverInfoPath | ConvertFrom-Json
try {
    $proc = Get-Process -Id ([int]$serverInfo.pid) -ErrorAction Stop
} catch {
    throw "server.json is stale; pid $($serverInfo.pid) is not running. Restart Revit 2026."
}
if ($proc.ProcessName -notlike "*Revit*") {
    throw "server.json pid $($serverInfo.pid) belongs to '$($proc.ProcessName)', not Revit."
}

Invoke-RevitCliSmoke @("doctor") | Out-Null
$statusText = Invoke-RevitCliSmoke @("status")
$liveAddinVersion = ""
foreach ($line in ($statusText -split "`r?`n")) {
    if ($line -match '^Add-in:\s+v?(.+)$') {
        $liveAddinVersion = $Matches[1].Trim()
        break
    }
}

$idJson = Invoke-RevitCliSmoke @("query", "--id", $ElementId.ToString(), "--output", "json")
$idElements = Convert-JsonArray $idJson "query --id"
if ($idElements.Count -ne 1) {
    throw "query --id returned $($idElements.Count) elements; expected exactly 1."
}

$oldValue = $null
$paramProperty = Get-ElementParameterProperty -Element $idElements[0] -ParameterName $Param -Context "query --id"
if ($Apply -and $null -eq $paramProperty.Value) {
    throw "Element $ElementId parameter '$Param' is null. Pick a writable text parameter with an existing value so the smoke test can restore it exactly."
}
$oldValue = if ($null -eq $paramProperty.Value) { $null } else { [string]$paramProperty.Value }
if ($Apply -and [string]::IsNullOrEmpty($Value)) {
    throw "-Apply requires a non-empty -Value so query confirmation cannot hide a missing parameter."
}

$filterJson = Invoke-RevitCliSmoke @("query", $Category, "--filter", $Filter, "--output", "json")
$filtered = Convert-JsonArray $filterJson "query filter"
if ($filtered.Count -ne 1) {
    throw "query $Category --filter '$Filter' returned $($filtered.Count) elements; expected exactly 1."
}
if ([long]$filtered[0].id -ne $ElementId) {
    throw "Filter matched element $($filtered[0].id), expected $ElementId."
}

$dryRunText = Invoke-RevitCliSmoke @(
    "set", $Category,
    "--filter", $Filter,
    "--param", $Param,
    "--value", $Value,
    "--dry-run"
)
Assert-DryRunPreview -Text $dryRunText -ElementId $ElementId -OldValue $oldValue -NewValue $Value

if (-not $Apply) {
    Write-Host "Dry-run smoke completed. Re-run with -Apply to perform the write/confirm/restore steps."
} else {
    $restoreNeeded = $false
    $applyFailure = $null
    $restoreFailure = $null

    try {
        Invoke-RevitCliSmoke @(
            "set", $Category,
            "--filter", $Filter,
            "--param", $Param,
            "--value", $Value
        ) | Out-Null
        $restoreNeeded = $true

        $confirmJson = Invoke-RevitCliSmoke @("query", "--id", $ElementId.ToString(), "--output", "json")
        $confirmed = Convert-JsonArray $confirmJson "query confirm"
        $newParam = Get-ElementParameterProperty -Element $confirmed[0] -ParameterName $Param -Context "query confirm"
        if ([string]$newParam.Value -ne $Value) {
            throw "Write verification failed for '$Param': expected '$Value', got '$($newParam.Value)'."
        }
    } catch {
        $applyFailure = $_
    } finally {
        if ($restoreNeeded) {
            try {
                Invoke-RevitCliSmoke @(
                    "set", "--id", $ElementId.ToString(),
                    "--param", $Param,
                    "--value", $oldValue
                ) | Out-Null

                $restoreJson = Invoke-RevitCliSmoke @("query", "--id", $ElementId.ToString(), "--output", "json")
                $restored = Convert-JsonArray $restoreJson "query restore"
                $restoredParam = Get-ElementParameterProperty -Element $restored[0] -ParameterName $Param -Context "query restore"
                if ([string]$restoredParam.Value -ne $oldValue) {
                    throw "Restore verification failed for '$Param': expected '$oldValue', got '$($restoredParam.Value)'."
                }
            } catch {
                $restoreFailure = $_
            }
        }
    }

    if ($applyFailure -and $restoreFailure) {
        throw "Apply/confirm failed after write, and restore also failed. Apply error: $($applyFailure.Exception.Message). Restore error: $($restoreFailure.Exception.Message)"
    }
    if ($restoreFailure) {
        throw "Restore failed after smoke write: $($restoreFailure.Exception.Message)"
    }
    if ($applyFailure) {
        throw $applyFailure
    }
}

$fixBaselinePath = ""
$fixJournalPath = ""
if ($FixDryRun -or $FixApply) {
    if ([string]::IsNullOrWhiteSpace($FixProfile)) {
        throw "-FixDryRun and -FixApply require a non-empty -FixProfile."
    }
}

if ($FixDryRun) {
    Invoke-RevitCliSmoke @("fix", $FixCheckName, "--dry-run", "--profile", $FixProfile) | Out-Null
}

if ($FixApply) {
    Invoke-RevitCliSmoke @("fix", $FixCheckName, "--apply", "--yes", "--profile", $FixProfile) | Out-Null

    $baseline = Get-LatestFixBaseline -RootPath (Get-Location).Path
    if ($null -eq $baseline) {
        throw "fix apply did not create a fix baseline under .revitcli"
    }

    $fixBaselinePath = $baseline.FullName
    $candidateJournalPath = Get-FixJournalPath -BaselinePath $fixBaselinePath
    if (Test-Path -LiteralPath $candidateJournalPath) {
        $fixJournalPath = (Get-Item -LiteralPath $candidateJournalPath).FullName
    }

    Invoke-RevitCliSmoke @("rollback", $fixBaselinePath, "--yes") | Out-Null
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path (Get-Location) "revitcli-smoke-2026-$stamp.json"
}

$report = [ordered]@{
    timestamp = (Get-Date).ToString("o")
    revitInstallDir = $installDir
    manifestPath = $manifestPath
    manifestAssemblyPath = $manifestAssemblyPath
    serverInfoPath = $serverInfoPath
    cliVersion = $cliVersion
    cliVersionError = $cliVersionError
    installedAddinVersion = $installedAddinVersion
    liveAddinVersion = $liveAddinVersion
    elementId = $ElementId
    category = $Category
    filter = $Filter
    parameter = $Param
    oldValue = $oldValue
    testValue = $Value
    applied = [bool]$Apply
    fixDryRun = [bool]$FixDryRun
    fixApply = [bool]$FixApply
    fixCheckName = $FixCheckName
    fixProfile = $FixProfile
    fixBaselinePath = $fixBaselinePath
    fixJournalPath = $fixJournalPath
    steps = $Steps
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Smoke report written to $OutputPath"
