#!/usr/bin/env bash
set -euo pipefail

preflight() {
    case "$(uname -s)" in
        CYGWIN*) ;;
        *) echo 'This Windows build profile requires Cygwin. Git Bash/MSYS is not a substitute.' >&2; return 1 ;;
    esac
    local missing=0 tool
    for tool in git make perl autoconf aclocal pkg-config python3 zip unzip bison flex gperf tar curl; do
        if ! command -v "$tool" >/dev/null 2>&1; then
            echo "Missing build tool: $tool" >&2
            missing=1
        fi
    done
    [[ "$missing" == 0 ]] || return 1
    echo 'Build-shell prerequisites found; upstream configure performs the remaining compiler/SDK checks.'
}

if [[ "${1:-}" == --preflight ]]; then preflight; exit; fi
[[ "$#" == 3 ]] || { echo 'Usage: build-engine.sh <checkout> <configure-args-file> <jobs>' >&2; exit 2; }
preflight
source_dir=$(cygpath -au "$1")
args_file=$(cygpath -au "$2")
jobs=$3
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid job count' >&2; exit 2; }
[[ "$source_dir" != *' '* ]] || { echo 'The source path must not contain spaces.' >&2; exit 2; }
cd "$source_dir"
[[ -f autogen.sh && -d .git ]] || { echo 'Not a prepared LibreOffice checkout' >&2; exit 2; }
configure_args=()
while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    [[ -z "$line" || "$line" == \#* ]] && continue
    [[ "$line" == --* ]] || { echo 'Configure arguments must be one option per line.' >&2; exit 2; }
    configure_args+=("$line")
done < "$args_file"
./autogen.sh "${configure_args[@]}"
# Dependency URLs and checksums come from the pinned upstream download.lst.
make fetch
make -j"$jobs" build
make -j"$jobs" check
for required in program/soffice.exe program/mergedlo.dll program/version.ini license.txt NOTICE; do
    [[ -s "instdir/$required" ]] || { echo "Missing source-build output: $required" >&2; exit 1; }
done
echo 'LibreOffice source build and upstream checks completed.'
