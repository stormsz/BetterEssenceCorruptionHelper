<#
.SYNOPSIS
    Deploys BetterEssenceCorruptionHelper into the newest ExileApi-Compiled-<version> HUD.

.DESCRIPTION
    The newest HUD is found by scanning -HudRoot for ExileApi-Compiled-<version> folders that
    contain ExileCore.dll, comparing versions numerically (so 329.19 beats 329.9).

      .\deploy.ps1           Build Release against that HUD's ExileCore.dll and copy the plugin DLL
                             to <HUD>\Plugins\Compiled\BetterEssenceCorruptionHelper - the same thing
                             players install from the release zip.

      .\deploy.ps1 -Source   Copy the project source to <HUD>\Plugins\Source\BetterEssenceCorruptionHelper
                             and compile it there, the same way the HUD does, to check it builds on
                             that ExileApi version.

    Target folders created by this script carry a marker file and are simply replaced on the next
    deploy. A target folder this script did not create is never deleted: the deploy stops, or with
    -Force it is moved to <HUD>\deploy-backups first (outside Plugins, so the HUD won't load it).

.EXAMPLE
    .\deploy.ps1 -DryRun
    Shows which HUD would be used and what would be copied, without building or copying.
#>
[CmdletBinding()]
param(
    # Copy the source into Plugins\Source (and compile-check it) instead of deploying the DLL.
    [switch]$Source,

    # Folder holding the ExileApi-Compiled-<version> folders.
    [string]$HudRoot = 'D:\bin\POE\ExileApi',

    # Use this HUD folder directly instead of detecting the newest one.
    [string]$HudPath,

    # Move a pre-existing target folder this script didn't create to deploy-backups and continue.
    [switch]$Force,

    # Print what would happen without building or copying anything.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
trap {
    Write-Host "Deploy failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$PluginName = 'BetterEssenceCorruptionHelper'
$ProjectDir = $PSScriptRoot
$MarkerName = '.deployed-by-deploy-ps1'

function Find-LatestHud([string]$root) {
    if (-not (Test-Path $root)) {
        throw "HUD root '$root' does not exist. Pass -HudRoot or -HudPath."
    }

    $found = @(Get-ChildItem $root -Directory | ForEach-Object {
        if ($_.Name -match '^ExileApi-Compiled-(\d+(?:\.\d+){0,3})$' -and
            (Test-Path (Join-Path $_.FullName 'ExileCore.dll'))) {
            $v = $Matches[1]
            if ($v -notmatch '\.') { $v += '.0' }  # [version] needs at least major.minor
            [pscustomobject]@{ Version = [version]$v; Path = $_.FullName }
        }
    } | Sort-Object Version -Descending)

    if ($found.Count -eq 0) {
        throw "No ExileApi-Compiled-<version> folder containing ExileCore.dll found under '$root'."
    }

    Write-Host "HUD versions found: $(($found | ForEach-Object { $_.Version }) -join ', ')"
    return $found[0].Path
}

# Clears the way for a fresh deploy at $target. Only ever deletes folders carrying our marker.
function Initialize-Target([string]$target, [string]$hud, [string]$kind) {
    if (Test-Path $target) {
        if (Test-Path (Join-Path $target $MarkerName)) {
            Write-Host "Replacing previous deploy at $target"
            if (-not $DryRun) { Remove-Item $target -Recurse -Force }
        }
        elseif ($Force) {
            $backupDir = Join-Path $hud 'deploy-backups'
            $backup = Join-Path $backupDir "$kind-$PluginName-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
            Write-Host "Moving existing $target (not created by this script) to $backup"
            if (-not $DryRun) {
                New-Item -ItemType Directory -Force $backupDir | Out-Null
                Move-Item $target $backup
            }
        }
        else {
            throw "$target already exists and was not created by this script. Re-run with -Force to move it to $hud\deploy-backups first."
        }
    }

    if (-not $DryRun) {
        New-Item -ItemType Directory $target | Out-Null
        Set-Content (Join-Path $target $MarkerName) "Deployed from $ProjectDir at $(Get-Date -Format s)"
    }
}

# The HUD may load (or prefer) one copy over the other when both exist, which makes it easy to
# end up testing the wrong build. Flag it rather than deleting anything.
function Show-OtherCopyWarning([string]$hud) {
    $other = if ($Source) { 'Compiled' } else { 'Source' }
    $otherPath = Join-Path $hud "Plugins\$other\$PluginName"
    if (Test-Path $otherPath) {
        Write-Warning "There is also a copy in Plugins\$other ($otherPath). Remove it if you only want the $(if ($Source) { 'Source' } else { 'Compiled' }) one loaded."
    }
}

$hud = if ($HudPath) { (Resolve-Path $HudPath).Path } else { Find-LatestHud $HudRoot }
if (-not (Test-Path (Join-Path $hud 'ExileCore.dll'))) {
    throw "'$hud' does not contain ExileCore.dll."
}
Write-Host "Using HUD: $hud" -ForegroundColor Cyan

if ($Source) {
    $target = Join-Path $hud "Plugins\Source\$PluginName"

    # Tracked + untracked-but-not-ignored files: never bin/, obj/, .vs/ or the .git folder.
    $files = @(git -C $ProjectDir ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw "git ls-files failed in $ProjectDir." }

    Initialize-Target $target $hud 'Source'
    Write-Host "Copying $($files.Count) source files to $target"

    if (-not $DryRun) {
        foreach ($rel in $files) {
            $from = Join-Path $ProjectDir $rel
            if (-not (Test-Path $from -PathType Leaf)) { continue }  # deleted but not yet staged
            $to = Join-Path $target $rel
            New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
            Copy-Item $from $to
        }

        # Build the deployed copy in place: DepsDir falls back to ..\..\.. there, so this resolves
        # ExileCore.dll exactly like the HUD's own compile does.
        Write-Host "Compile check against $(Split-Path $hud -Leaf)..."
        & dotnet build (Join-Path $target "$PluginName.csproj") -c Release -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Compile check FAILED against $(Split-Path $hud -Leaf)." -ForegroundColor Red
            exit 1
        }
        Write-Host "Compile check passed - the HUD will compile it on next load." -ForegroundColor Green
    }
}
else {
    $target = Join-Path $hud "Plugins\Compiled\$PluginName"
    $outDir = Join-Path $ProjectDir 'bin\deploy'

    Write-Host "Building Release against $(Split-Path $hud -Leaf)..."
    if (-not $DryRun) {
        if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
        & dotnet build (Join-Path $ProjectDir "$PluginName.csproj") -c Release -nologo -v q "-p:CI_LIBS_PATH=$hud" -o $outDir | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build FAILED against $(Split-Path $hud -Leaf)." -ForegroundColor Red
            exit 1
        }
    }

    Initialize-Target $target $hud 'Compiled'

    # Only the plugin's own files, like the release zip. ExileCore etc. come from the HUD itself.
    $artifacts = @("$PluginName.dll", "$PluginName.pdb")
    Write-Host "Copying $($artifacts -join ', ') to $target"
    if (-not $DryRun) {
        foreach ($name in $artifacts) {
            $from = Join-Path $outDir $name
            if (-not (Test-Path $from)) { continue }  # no .pdb if DebugType is none
            try {
                Copy-Item $from $target
            }
            catch {
                throw "Could not copy $name to $target - close the HUD if it has the plugin loaded. ($($_.Exception.Message))"
            }
        }
    }
}

Show-OtherCopyWarning $hud
if ($DryRun) { Write-Host "Dry run - nothing was built or copied." -ForegroundColor Yellow }
else { Write-Host "Deployed to $target" -ForegroundColor Green }
