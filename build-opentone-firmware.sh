#!/usr/bin/env bash
# Builds a custom firmware image: imports every preset in ./presets/lt25/ into its firmware slot, replaces the
# startup splash logo with ./assets/splash-red.bmp and installs the USB debug hook (./hooks/debug.c).
# Linux version of Build-OpenTone-Firmware.ps1 (needs bash 4+; macOS ships bash 3.2, so install a newer bash there).
#
# Preset files are named "NN NAME.json", where NN is the slot (01-60). Slots 01-30 are imported into their
# firmware slot. The firmware has only one template for slots 31-60, so files for 31-60 must be identical;
# one of them is imported as the EMPTY template. "empty NAME.json" sets the template directly.
# presets/custom/make_custom_presets.py writes files in this form.
#
# Hooks (default: the debug hook on field 12, used by "opentone get mem", "set mem" and "call") are compiled
# with arm-none-eabi-gcc, which must be on PATH. --hook replaces the default (repeatable); --no-hooks builds
# without any.
#
# Everything is applied in one "opentone patch" run, so the build either succeeds as a whole or writes nothing.
# The splash must be a 24- or 32-bit BMP of exactly 128x43 pixels. Export the current one with:
#     opentone patch ./fw/lt25-2.1.4.bin --export-image splash=logo.bmp
#
# After flashing, the presets only reach the amp's preset memory through a factory restore
# (Settings > Factory Restore), which replaces all 60 slots.
#
# Usage:
#   ./build-opentone-firmware.sh [--firmware PATH] [--output PATH] [--presets DIR] [--splash BMP]
#                                [--hook FIELD=FILE]... [--no-hooks] [--opentone PATH] [--dry-run]
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
firmware="./fw/lt25-2.1.4.bin"
output="./fw/lt25-2.1.4-opentone.bin"
presets="./presets/lt25"
splash="./assets/splash-red.bmp"
hooks=("12=./hooks/debug.c")
custom_hooks=0
opentone=""
dry_run=0

usage() { awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --firmware) firmware="$2"; shift 2 ;;
        --output)   output="$2";   shift 2 ;;
        --presets)  presets="$2";  shift 2 ;;
        --splash)   splash="$2";   shift 2 ;;
        --hook)     (( custom_hooks )) || hooks=(); custom_hooks=1; hooks+=("$2"); shift 2 ;;
        --no-hooks) hooks=(); custom_hooks=1; shift ;;
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

abs() { echo "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; }

for required in "$firmware" "$presets" "$splash"; do
    [[ -e "$required" ]] || die "Not found: $required"
done

# ---- Presets: "NN NAME.json" -> slot NN (01-30); 31-60 and "empty NAME.json" -> the EMPTY template ----

declare -A by_slot=()
template=()         # files for slots 31-60
shopt -s nullglob
files=("$presets"/*.json)
shopt -u nullglob
(( ${#files[@]} > 0 )) || die "No preset files (*.json) in $presets."

while IFS= read -r file; do
    name="$(basename "$file")"
    if [[ "$name" =~ ^([0-9]{1,2})[[:space:]] ]]; then
        slot=$((10#${BASH_REMATCH[1]}))
        (( slot >= 1 && slot <= 60 )) || die "$name: slot $slot is outside 1-60."
        if (( slot > 30 )); then template+=("$file"); continue; fi
        key="$slot"
    elif [[ "$name" =~ ^[Ee][Mm][Pp][Tt][Yy]([^[:alnum:]]|$) ]]; then
        key="empty"
    else
        die "$name: name must start with the slot (e.g. '05 MY TONE.json') or 'empty'."
    fi

    [[ -z "${by_slot[$key]:-}" ]] || die "Slot $key has two presets: $(basename "${by_slot[$key]}") and $name."
    by_slot[$key]="$file"
done < <(printf '%s\n' "${files[@]}" | sort)

# Slots 31-60 all come from one template in the firmware, so their files must be identical.
if (( ${#template[@]} > 0 )); then
    for file in "${template[@]}"; do
        cmp -s "$file" "${template[0]}" || die "Slots 31-60 share one template in the firmware, but $(basename "$file") differs from $(basename "${template[0]}"). Make them identical or use a single 'empty NAME.json'."
    done
    [[ -n "${by_slot[empty]:-}" ]] || by_slot[empty]="${template[0]}"
fi

import_args=()
for key in "${!by_slot[@]}"; do
    import_args+=(--import "$key=$(abs "${by_slot[$key]}")")
done

# ---- Hooks ---------------------------------------------------------------------------------------------

hook_args=()
for spec in "${hooks[@]}"; do
    source="${spec#*=}"
    [[ "$spec" == *=* && -f "$source" ]] || die "Hook source not found: $spec"
    hook_args+=(--hook "${spec%%=*}=$(abs "$source")")
done
if (( ${#hooks[@]} > 0 )) && ! command -v arm-none-eabi-gcc >/dev/null; then
    die "Hooks need arm-none-eabi-gcc on PATH (install the Arm GNU toolchain), or build with --no-hooks."
fi

# ---- One patch run: all presets, the splash logo and the hooks -----------------------------------------

command=(patch "$(abs "$firmware")")
(( dry_run )) || command+=("$output")
command+=("${import_args[@]}" --image "splash=$(abs "$splash")")
(( ${#hook_args[@]} > 0 )) && command+=("${hook_args[@]}")
(( dry_run )) && command+=(--dry-run)

hook_text="no hooks"
(( ${#hooks[@]} > 0 )) && hook_text="hooks ${hooks[*]}"
echo "Building from $firmware with ${#by_slot[@]} presets from $presets, splash $splash and $hook_text"
if ! run_opentone "${command[@]}"; then
    die "opentone patch failed; no image was written."
fi

if (( ! dry_run )); then
    echo "Flash it with:  opentone flash $output -d lt-dfu"
    echo "Then run Settings > Factory Restore on the amp to load the presets (replaces all 60 slots)."
    if (( ${#hooks[@]} > 0 )); then echo "With the debug hook: opentone get mem 0x08020200 16 -d mustang-lt25"; fi
fi
