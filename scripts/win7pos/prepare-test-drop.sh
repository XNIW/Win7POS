#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
vm_root="${repo_root}/.win7pos-vm"
drop_root="${vm_root}/drop"
default_dist="${repo_root}/dist/Win7POS"
default_wpf="${repo_root}/src/Win7POS.Wpf/bin/Release/net48"
target_dir="${drop_root}/Win7POS"
source_dir=""
execute=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/prepare-test-drop.sh [--execute] [--source <dir>] [--target <dir>]

Default mode is dry-run. Use --execute to create directories and copy files.

Defaults:
  source: dist/Win7POS if present, otherwise src/Win7POS.Wpf/bin/Release/net48
  target: .win7pos-vm/drop/Win7POS

This script never builds the WPF app and never deletes files outside .win7pos-vm.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --execute)
      execute=1
      shift
      ;;
    --source)
      if [[ $# -lt 2 ]]; then
        echo "--source requires a directory." >&2
        exit 2
      fi
      source_dir="${2:-}"
      shift 2
      ;;
    --target)
      if [[ $# -lt 2 ]]; then
        echo "--target requires a directory." >&2
        exit 2
      fi
      target_dir="${2:-}"
      shift 2
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
  if [[ -d "${default_dist}" ]]; then
    source_dir="${default_dist}"
  else
    source_dir="${default_wpf}"
  fi
fi

if [[ "${source_dir}" != /* ]]; then
  source_dir="${repo_root}/${source_dir}"
fi

if [[ "${target_dir}" != /* ]]; then
  target_dir="${repo_root}/${target_dir}"
fi

case "${target_dir}" in
  *"/.."|*"/../"*)
    echo "Refusing target with parent-directory traversal: ${target_dir}" >&2
    exit 1
    ;;
esac

case "${target_dir}" in
  "${vm_root}"/*) ;;
  *)
    echo "Refusing target outside .win7pos-vm: ${target_dir}" >&2
    exit 1
    ;;
esac

echo "Win7POS test drop preparation"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "Source: ${source_dir}"
echo "Target: ${target_dir}"

if [[ ! -d "${source_dir}" ]]; then
  cat >&2 <<EOF
Source directory not found.

Build/package the WPF app on Windows first, then rerun with:
  scripts/win7pos/prepare-test-drop.sh --execute --source <prepared-app-folder>

Expected executable:
  ${source_dir}/Win7POS.Wpf.exe
EOF
  if [[ "${execute}" -eq 1 ]]; then
    exit 1
  fi
  exit 0
fi

if [[ ! -f "${source_dir}/Win7POS.Wpf.exe" ]]; then
  cat >&2 <<EOF
Win7POS.Wpf.exe not found in source.

Use the complete WPF Release output folder or dist/Win7POS from the Windows release pack workflow.
EOF
  if [[ "${execute}" -eq 1 ]]; then
    exit 1
  fi
  exit 0
fi

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would create:"
  echo "  ${drop_root}"
  echo "  ${target_dir}"
  echo "Would copy files from source to target without deleting existing files."
  echo
  echo "Run with --execute to perform the copy."
  exit 0
fi

mkdir -p "${target_dir}"

if command -v rsync >/dev/null 2>&1; then
  rsync -a "${source_dir}/" "${target_dir}/"
else
  cp -R "${source_dir}/." "${target_dir}/"
fi

echo "Drop ready: ${target_dir}"
echo "Guest target suggestion: C:\\Win7POSTest\\drop\\Win7POS"
