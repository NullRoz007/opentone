<#
.SYNOPSIS
    Builds a custom firmware image: imports every preset in ./presets/lt25/ into its firmware slot, replaces
    the startup splash logo with ./assets/splash-red.bmp and installs the USB debug hook (./hooks/debug.c).

.DESCRIPTION
    Preset files are named "NN NAME.json", where NN is the slot (01-60). Slots 01-30 are imported into their
    firmware slot. The firmware has only one template for slots 31-60, so files for 31-60 must all be
    identical; one of them is imported as the EMPTY template. "empty NAME.json" sets the template directly.
    presets/custom/make_custom_presets.py writes files in this form.

    Hooks (default: the debug hook on field 12, used by "opentone get mem", "set mem" and "call") are compiled
    with arm-none-eabi-gcc, which must be on PATH. Use -NoHooks to build without them.

    Everything is applied in one "opentone patch" run, so the build either succeeds as a whole (every preset,
    the logo and the hooks pass the patcher's checks and the image is re-validated) or writes nothing.

    The splash must be a 24- or 32-bit BMP of exactly 128x43 pixels. Export the current one to edit with:
        opentone patch ./fw/lt25-2.1.4.bin --export-image splash=logo.bmp

    After flashing, the presets only reach the amp's preset memory through a factory restore
    (Settings > Factory Restore), which replaces all 60 slots.

.EXAMPLE
    ./Build-OpenTone-Firmware.ps1
    ./Build-OpenTone-Firmware.ps1 -Output ./fw/my-build.bin
    ./Build-OpenTone-Firmware.ps1 -Splash ./static/logo.bmp -DryRun
    ./Build-OpenTone-Firmware.ps1 -Presets ./presets/custom -NoHooks
#>
param(
    [string] $Firmware = "./fw/lt25-2.1.4.bin",
    [string] $Output   = "./fw/lt25-2.1.4-opentone.bin",
    [string] $Presets  = "./presets/lt25",
    [string] $Splash   = "./assets/splash-red.bmp",
    # Hooks as FIELD=FILE (see hooks/opentone.h; "opentone patch <image> --info" lists the free fields).
    [string[]] $Hook   = @("12=./hooks/debug.c"),
    # Build without any hooks.
    [switch] $NoHooks,
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

# ---- Presets: "NN NAME.json" -> slot NN (01-30); 31-60 and "empty NAME.json" -> the EMPTY template ----

$imports = [ordered]@{}
$template = @()     # files for slots 31-60
foreach ($file in Get-ChildItem -LiteralPath $Presets -Filter *.json | Sort-Object Name) {
    if ($file.Name -match '^(\d{1,2})\s') {
        $slot = [int]$Matches[1]
        if ($slot -lt 1 -or $slot -gt 60) { throw "$($file.Name): slot $slot is outside 1-60." }
        if ($slot -gt 30) { $template += $file; continue }
        $key = "$slot"
    }
    elseif ($file.Name -match '^empty\b') { $key = "empty" }
    else { throw "$($file.Name): name must start with the slot (e.g. '05 MY TONE.json') or 'empty'." }

    if ($imports.Contains($key)) { throw "Slot $key has two presets: $(Split-Path $imports[$key] -Leaf) and $($file.Name)." }
    $imports[$key] = $file.FullName
}

# Slots 31-60 all come from one template in the firmware, so their files must agree.
if ($template.Count -gt 0) {
    $distinct = $template |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 64 -Compress } |
        Select-Object -Unique
    if (@($distinct).Count -gt 1) {
        throw "Slots 31-60 share one template in the firmware, but the $($template.Count) files for them differ. Make them identical or use a single 'empty NAME.json'."
    }
    if (-not $imports.Contains("empty")) { $imports["empty"] = $template[0].FullName }
}
if ($imports.Count -eq 0) { throw "No preset files (*.json) in $Presets." }

# ---- Hooks ---------------------------------------------------------------------------------------------

$hooks = if ($NoHooks) { @() } else { @($Hook) }
foreach ($spec in $hooks) {
    $source = ($spec -split '=', 2)[1]
    if (-not $source -or -not (Test-Path -LiteralPath $source)) { throw "Hook source not found: $spec" }
}
if ($hooks.Count -gt 0 -and -not (Get-Command arm-none-eabi-gcc -ErrorAction SilentlyContinue)) {
    throw "Hooks need arm-none-eabi-gcc on PATH (e.g. 'scoop bucket add extras; scoop install gcc-arm-none-eabi'), or build with -NoHooks."
}

# ---- One patch run: all presets, the splash logo and the hooks -----------------------------------------

$command = @("patch", (Resolve-Path -LiteralPath $Firmware).Path)
if (-not $DryRun) { $command += $Output }
foreach ($slot in $imports.Keys) { $command += @("--import", "$slot=$($imports[$slot])") }
$command += @("--image", "splash=$((Resolve-Path -LiteralPath $Splash).Path)")
foreach ($spec in $hooks) { $command += @("--hook", $spec) }
if ($DryRun) { $command += "--dry-run" }

$hookText = if ($hooks.Count -gt 0) { "hooks $($hooks -join ', ')" } else { "no hooks" }
Write-Host "Building from $Firmware with $($imports.Count) presets from $Presets, splash $Splash and $hookText"
Invoke-OpenTone $command
if ($LASTEXITCODE -ne 0) { throw "opentone patch failed (exit $LASTEXITCODE); no image was written." }

if (-not $DryRun) {
    Write-Host "Flash it with:  opentone flash $Output -d lt-dfu"
    Write-Host "Then run Settings > Factory Restore on the amp to load the presets (replaces all 60 slots)."
    if ($hooks.Count -gt 0) { Write-Host "With the debug hook: opentone get mem 0x08020200 16 -d mustang-lt25" }
}
