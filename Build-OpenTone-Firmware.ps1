<#
.SYNOPSIS
    Builds a custom firmware image: imports every preset in ./presets/custom/ into its firmware slot and
    replaces the startup splash logo with ./assets/logo.bmp.

.DESCRIPTION
    Preset files are named "NN NAME.json", where NN is the firmware slot (01-30); "empty NAME.json" sets the
    EMPTY template used for slots 31-60. presets/custom/make_custom_presets.py writes them in this form.

    Everything is applied in one "opentone patch" run, so the build either succeeds as a whole (every preset
    and the logo pass the patcher's checks and the image is re-validated) or writes nothing.

    The splash must be a 24- or 32-bit BMP of exactly 128x43 pixels. Export the current one to edit with:
        opentone patch ./firmware/lt25-2.1.4.bin --export-image splash=logo.bmp

    After flashing, the presets only reach the amp's preset memory through a factory restore
    (Settings > Factory Restore), which replaces all 60 slots.

.EXAMPLE
    ./Build-OpenTone-Firmware.ps1
    ./Build-OpenTone-Firmware.ps1 -Output ./firmware/my-build.bin
    ./Build-OpenTone-Firmware.ps1 -Splash ./static/logo.bmp -DryRun
#>
param(
    [string] $Firmware = "./firmware/lt25-2.1.4.bin",
    [string] $Output   = "./firmware/lt25-2.1.4-opentone.bin",
    [string] $Presets  = "./presets/custom",
    [string] $Splash   = "./assets/logo.bmp",
    # Validate everything without writing the output image.
    [switch] $DryRun,
    # Path to the built OpenTone executable; falls back to "dotnet run" if it doesn't exist.
    [string] $OpenTone = "$PSScriptRoot/bin/Debug/net10.0/OpenTone.exe"
)

$ErrorActionPreference = "Stop"

function Invoke-OpenTone([string[]] $arguments) {
    if (Test-Path $OpenTone) { & $OpenTone @arguments }
    else { & dotnet run --project $PSScriptRoot --no-build -- @arguments }
}

foreach ($required in @($Firmware, $Presets, $Splash)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Not found: $required" }
}

# ---- Presets: "NN NAME.json" -> slot NN, "empty NAME.json" -> the EMPTY template ---------------------

$imports = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $Presets -Filter *.json | Sort-Object Name) {
    if ($file.Name -match '^(\d{1,2})\s') {
        $slot = [int]$Matches[1]
        if ($slot -lt 1 -or $slot -gt 30) { throw "$($file.Name): slot $slot is outside 1-30." }
        $key = "$slot"
    }
    elseif ($file.Name -match '^empty\b') { $key = "empty" }
    else { throw "$($file.Name): name must start with the firmware slot (e.g. '05 MY TONE.json') or 'empty'." }

    if ($imports.Contains($key)) { throw "Slot $key has two presets: $(Split-Path $imports[$key] -Leaf) and $($file.Name)." }
    $imports[$key] = $file.FullName
}
if ($imports.Count -eq 0) { throw "No preset files (*.json) in $Presets." }

# ---- One patch run: all presets plus the splash logo -------------------------------------------------

$command = @("patch", (Resolve-Path -LiteralPath $Firmware).Path)
if (-not $DryRun) { $command += $Output }
foreach ($slot in $imports.Keys) { $command += @("--import", "$slot=$($imports[$slot])") }
$command += @("--image", "splash=$((Resolve-Path -LiteralPath $Splash).Path)")
if ($DryRun) { $command += "--dry-run" }

Write-Host "Building from $Firmware with $($imports.Count) presets from $Presets and splash $Splash"
Invoke-OpenTone $command
if ($LASTEXITCODE -ne 0) { throw "opentone patch failed (exit $LASTEXITCODE); no image was written." }

if (-not $DryRun) {
    Write-Host "Flash it with:  opentone flash $Output -d lt-dfu"
    Write-Host "Then run Settings > Factory Restore on the amp to load the presets (replaces all 60 slots)."
}
