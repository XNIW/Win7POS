#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
vm_root="${repo_root}/.win7pos-vm"
drop_app="${vm_root}/drop/Win7POS/Win7POS.Wpf.exe"
vm_name="Win7POS-Test"
execute=0
skip_start=0
utmctl_timeout_secs="${UTMCTL_TIMEOUT_SECS:-15}"

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/run-utm-smoke.sh [--execute] [--vm <name>] [--skip-start]

Default mode is dry-run. Use --execute to start the VM with utmctl.

This script does not restore snapshots, stop VMs, delete files, or execute guest commands.

Environment:
  UTMCTL_EXE             Optional path to utmctl.
  UTMCTL_TIMEOUT_SECS    Timeout for utmctl list in seconds. Default: 15.
EOF
}

find_utmctl() {
  if [[ -n "${UTMCTL_EXE:-}" && -x "${UTMCTL_EXE}" ]]; then
    printf '%s\n' "${UTMCTL_EXE}"
    return 0
  fi

  if command -v utmctl >/dev/null 2>&1; then
    command -v utmctl
    return 0
  fi

  if [[ -x "/Applications/UTM.app/Contents/MacOS/utmctl" ]]; then
    printf '%s\n' "/Applications/UTM.app/Contents/MacOS/utmctl"
    return 0
  fi

  return 1
}

utmctl_list_with_timeout() {
  local utmctl_bin="$1"
  local timeout_secs="$2"
  local tmp_file
  local pid
  local elapsed=0
  local status=0

  tmp_file="$(mktemp -t win7pos-utmctl-list.XXXXXX)"
  "${utmctl_bin}" list >"${tmp_file}" 2>&1 &
  pid="$!"

  while kill -0 "${pid}" >/dev/null 2>&1; do
    if [[ "${elapsed}" -ge "${timeout_secs}" ]]; then
      kill "${pid}" >/dev/null 2>&1 || true
      set +e
      wait "${pid}" >/dev/null 2>&1
      set -e
      cat "${tmp_file}"
      rm -f "${tmp_file}"
      echo "utmctl list timed out after ${timeout_secs}s. Open UTM once and verify permissions/automation access." >&2
      return 124
    fi
    sleep 1
    elapsed=$((elapsed + 1))
  done

  set +e
  wait "${pid}"
  status="$?"
  set -e
  cat "${tmp_file}"
  rm -f "${tmp_file}"
  return "${status}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --execute)
      execute=1
      shift
      ;;
    --vm)
      if [[ $# -lt 2 ]]; then
        echo "--vm requires a VM name." >&2
        exit 2
      fi
      vm_name="${2:-}"
      shift 2
      ;;
    --skip-start)
      skip_start=1
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

if [[ -z "${vm_name}" ]]; then
  echo "VM name cannot be empty." >&2
  exit 2
fi

echo "Win7POS UTM smoke helper"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "VM: ${vm_name}"
echo "Host folders:"
echo "  ${vm_root}/drop"
echo "  ${vm_root}/logs"
echo "  ${vm_root}/screenshots"
echo "  ${vm_root}/reports"
echo "Expected drop executable:"
echo "  ${drop_app}"

if [[ ! -f "${drop_app}" ]]; then
  cat >&2 <<EOF
Drop executable not found.

Prepare the Windows build output first, then run:
  scripts/win7pos/prepare-test-drop.sh --execute --source <prepared-app-folder>

Accepted source folders documented by the repo:
  src/Win7POS.Wpf/bin/Release/net48
  dist/Win7POS
EOF
  if [[ "${execute}" -eq 1 ]]; then
    exit 1
  fi
  echo
  echo "Dry-run only. Continuing with utmctl availability checks."
fi

utmctl_bin="$(find_utmctl || true)"
if [[ -z "${utmctl_bin}" ]]; then
  cat >&2 <<'EOF'
utmctl not found.

Install or expose UTM CLI first. Common path:
  /Applications/UTM.app/Contents/MacOS/utmctl

You can symlink it or add that directory to PATH. Then verify:
  utmctl list
EOF
  if [[ "${execute}" -eq 1 ]]; then
    exit 1
  fi
  echo
  echo "Dry-run only. Would create host output folders and check VM availability after utmctl is configured."
  exit 0
fi

echo
echo "utmctl: ${utmctl_bin}"
echo "Available UTM VMs:"
if ! utm_list="$(utmctl_list_with_timeout "${utmctl_bin}" "${utmctl_timeout_secs}")"; then
  echo "${utm_list}"
  if [[ "${execute}" -eq 1 ]]; then
    exit 1
  fi
  echo
  echo "Dry-run only. VM list is not available yet."
  exit 0
fi

echo "${utm_list}"

if ! grep -Fq "${vm_name}" <<<"${utm_list}"; then
  cat >&2 <<EOF
VM not found in utmctl list: ${vm_name}

Create or rename the Windows 7 VM to:
  ${vm_name}

Or rerun with:
  scripts/win7pos/run-utm-smoke.sh --vm <actual-name>
EOF
  exit 1
fi

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would start VM unless --skip-start is used:"
  echo "  mkdir -p \"${vm_root}/drop\" \"${vm_root}/logs\" \"${vm_root}/screenshots\" \"${vm_root}/reports\""
  echo "  utmctl start \"${vm_name}\""
  echo
  echo "Manual smoke steps remain inside Windows 7."
  exit 0
fi

mkdir -p "${vm_root}/drop" "${vm_root}/logs" "${vm_root}/screenshots" "${vm_root}/reports"

if [[ "${skip_start}" -eq 1 ]]; then
  echo "Skipping VM start by request."
else
  echo
  echo "Starting VM: ${vm_name}"
  "${utmctl_bin}" start "${vm_name}"
fi

cat <<EOF

Next manual guest steps:
  1. Restore baseline snapshot manually before this script if needed.
  2. Open C:\\Win7POSTest\\drop\\Win7POS\\Win7POS.Wpf.exe.
  3. Use a test data dir, for example:
       set WIN7POS_DATA_DIR=C:\\Win7POSTest\\data
  4. Run the smoke checklist from docs/dev/win7pos-mac-utm-testing.md.
  5. Copy app.log and screenshots to the shared folders.
EOF
