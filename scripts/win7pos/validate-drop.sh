#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source_dir=""
expect_config=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/validate-drop.sh --source <dir> [--expect-config]

Validates a Win7POS drop produced on Windows. This script is read-only:
it does not copy, delete, build, install, or modify files.

Required:
  --source <dir>     Folder expected to contain Win7POS.Wpf.exe.

Optional:
  --expect-config    Fail if Win7POS.Wpf.exe.config is missing.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source)
      if [[ $# -lt 2 ]]; then
        echo "--source requires a directory." >&2
        exit 2
      fi
      source_dir="${2:-}"
      shift 2
      ;;
    --expect-config)
      expect_config=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

if [[ -z "${source_dir}" ]]; then
  echo "Missing required --source <dir>." >&2
  usage
  exit 2
fi

if [[ "${source_dir}" != /* ]]; then
  source_dir="${repo_root}/${source_dir}"
fi

echo "Win7POS drop validation"
echo "Source: ${source_dir}"

if [[ ! -d "${source_dir}" ]]; then
  cat >&2 <<EOF
ERROR: source folder not found.

Provide a drop produced on Windows, for example:
  src/Win7POS.Wpf/bin/Release/net48
  dist/Win7POS
EOF
  exit 1
fi

exe_path="${source_dir}/Win7POS.Wpf.exe"
if [[ ! -f "${exe_path}" ]]; then
  cat >&2 <<EOF
ERROR: Win7POS.Wpf.exe not found.

Expected:
  ${exe_path}

Build the WPF project on Windows 10/11 Builder, then validate the produced drop.
EOF
  exit 1
fi

echo "OK: found Win7POS.Wpf.exe"

config_path="${source_dir}/Win7POS.Wpf.exe.config"
if [[ -f "${config_path}" ]]; then
  echo "OK: found Win7POS.Wpf.exe.config"
else
  if [[ "${expect_config}" -eq 1 ]]; then
    echo "ERROR: --expect-config was set but Win7POS.Wpf.exe.config is missing." >&2
    exit 1
  fi
  echo "INFO: Win7POS.Wpf.exe.config not found; this repo does not currently define an app.config requirement."
fi

echo
echo "Project DLLs:"
for dll in Win7POS.Core.dll Win7POS.Data.dll; do
  if [[ -f "${source_dir}/${dll}" ]]; then
    echo "  OK: ${dll}"
  else
    echo "  WARN: ${dll} not found"
  fi
done

echo
echo "Known dependency DLLs present:"
found_dependency=0
while IFS= read -r dll_path; do
  found_dependency=1
  printf '  %s\n' "${dll_path#${source_dir}/}"
done < <(
  find "${source_dir}" -maxdepth 3 -type f \( \
    -iname 'Dapper*.dll' -o \
    -iname 'Microsoft.Data.Sqlite*.dll' -o \
    -iname 'SQLitePCLRaw*.dll' -o \
    -iname 'PDFsharp*.dll' -o \
    -iname 'ZXing*.dll' -o \
    -iname 'ClosedXML*.dll' -o \
    -iname 'ExcelDataReader*.dll' \
  \) | sort
)
if [[ "${found_dependency}" -eq 0 ]]; then
  echo "  WARN: no known dependency DLLs found in the first 3 levels."
fi

echo
echo "Native SQLite candidates:"
found_native=0
while IFS= read -r native_path; do
  found_native=1
  printf '  %s\n' "${native_path#${source_dir}/}"
done < <(
  find "${source_dir}" -maxdepth 5 -type f \( \
    -iname 'e_sqlite3.dll' -o \
    -iname 'SQLite.Interop.dll' \
  \) | sort
)
if [[ "${found_native}" -eq 0 ]]; then
  echo "  WARN: no e_sqlite3.dll or SQLite.Interop.dll found. Verify the Windows build copied native SQLite assets if Win7 smoke fails at DB startup."
fi

echo
echo "Asset check:"
if [[ -f "${source_dir}/Assets/sii_qrcode.png" ]]; then
  echo "  OK: Assets/sii_qrcode.png"
else
  echo "  WARN: Assets/sii_qrcode.png not found"
fi

echo
echo "Next step:"
echo "  scripts/win7pos/prepare-test-drop.sh --execute --source \"${source_dir}\""
