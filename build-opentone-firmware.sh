#!/usr/bin/env bash
# Builds a custom firmware image: imports every preset in ./presets/custom/ into its firmware slot and
# replaces the startup splash logo with ./assets/logo.bmp. Linux version of Build-OpenTone-Firmware.ps1
# (needs bash 4+; macOS ships bash 3.2, so install a newer bash there).
#
# Preset files are named "NN NAME.json", where NN is the firmware slot (01-30); "empty NAME.json" sets the
# EMPTY template used for slots 31-60. presets/custom/make_custom_presets.py writes them in this form.
#
# Everything is applied in one "opentone patch" run, so the build either succeeds as a whole or writes nothing.
# The splash must be a 24- or 32-bit BMP of exactly 128x43 pixels. Export the current one with:
#     opentone patch ./firmware/lt25-2.1.4.bin --export-image splash=logo.bmp
#
# After flashing, the presets only reach the amp's preset memory through a factory restore
# (Settings > Factory Restore), which replaces all 60 slots.
#
# Usage:
#   ./build-opentone-firmware.sh [--firmware PATH] [--output PATH] [--presets DIR] [--splash BMP]
#                                [--opentone PATH] [--dry-run]
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
firmware="./firmware/lt25-2.1.4.bin"
output="./firmware/lt25-2.1.4-opentone.bin"
presets="./presets/custom"
splash="./assets/logo.bmp"
opentone=""
dry_run=0

usage() { awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --firmware) firmware="$2"; shift 2 ;;
        --output)   output="$2";   shift 2 ;;
        --presets)  presets="$2";  shift 2 ;;
        --splash)   splash="$2";   shift 2 ;;
        --opentone) opentone="$2"; shift 2 ;;
        --dry-run)  dry_run=1;     shift ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

die() { echo "error: $*" >&2; exit 1; }

# The built executable (OpenTone on Linux/macOS, OpenTone.exe under Git Bash/WSL on a Windows build);
# otherwise "dotnet run".
run_opentone() {
    if [[ -n "$opentone" ]]; then "$opentone" "$@"; return; fi
    for exe in "$script_dir/bin/Debug/net10.0/OpenTone" "$script_dir/bin/Debug/net10.0/OpenTone.exe"; do
        if [[ -x "$exe" ]]; then "$exe" "$@"; return; fi
    done
    dotnet run --project "$script_dir" --no-build -- "$@"
}

for required in "$firmware" "$presets" "$splash"; do
    [[ -e "$required" ]] || die "Not found: $required"
done

# ---- Presets: "NN NAME.json" -> slot NN, "empty NAME.json" -> the EMPTY template ---------------------

declare -A by_slot=()
import_args=()
shopt -s nullglob
files=("$presets"/*.json)
shopt -u nullglob
(( ${#files[@]} > 0 )) || die "No preset files (*.json) in $presets."

while IFS= read -r file; do
    name="$(basename "$file")"
    if [[ "$name" =~ ^([0-9]{1,2})[[:space:]] ]]; then
        slot=$((10#${BASH_REMATCH[1]}))
        (( slot >= 1 && slot <= 30 )) || die "$name: slot $slot is outside 1-30."
        key="$slot"
    elif [[ "$name" =~ ^empty([^[:alnum:]]|$) ]]; then
        key="empty"
    else
        die "$name: name must start with the firmware slot (e.g. '05 MY TONE.json') or 'empty'."
    fi

    [[ -z "${by_slot[$key]:-}" ]] || die "Slot $key has two presets: $(basename "${by_slot[$key]}") and $name."
    by_slot[$key]="$file"
    import_args+=(--import "$key=$(cd "$(dirname "$file")" && pwd)/$name")
done < <(printf '%s\n' "${files[@]}" | sort)

# ---- One patch run: all presets plus the splash logo -------------------------------------------------

abs() { echo "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; }

command=(patch "$(abs "$firmware")")
(( dry_run )) || command+=("$output")
command+=("${import_args[@]}" --image "splash=$(abs "$splash")")
(( dry_run )) && command+=(--dry-run)

echo "Building from $firmware with ${#by_slot[@]} presets from $presets and splash $splash"
if ! run_opentone "${command[@]}"; then
    die "opentone patch failed; no image was written."
fi

if (( ! dry_run )); then
    echo "Flash it with:  opentone flash $output -d lt-dfu"
    echo "Then run Settings > Factory Restore on the amp to load the presets (replaces all 60 slots)."
fi
