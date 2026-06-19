#!/usr/bin/env bash
set -euo pipefail

home_dir="${HOME:-}"

print_section() {
  printf '\n== %s ==\n' "$1"
}

collect_apps() {
  {
    ls -la /Applications 2>/dev/null | egrep -i 'UTM|Parallels|VMware|VirtualBox' || true
    ls -la "${home_dir}/Applications" 2>/dev/null | egrep -i 'UTM|Parallels|VMware|VirtualBox' || true
  } | sed '/^[[:space:]]*$/d'
}

collect_cli() {
  for cli in utmctl prlctl vmrun VBoxManage; do
    if command -v "${cli}" >/dev/null 2>&1; then
      printf '%s: %s\n' "${cli}" "$(command -v "${cli}")"
    fi
  done
}

collect_processes() {
  ps -axo pid=,comm= | awk '
    BEGIN { IGNORECASE = 1 }
    /UTM\.app|\/UTM$|utmctl|qemu|Parallels|prl_|vmware|VMware|VirtualBox|VBoxManage|VBoxHeadless|VBoxSVC/ { print }
  ' || true
}

collect_vm_files() {
  if [[ -z "${home_dir}" || ! -d "${home_dir}" ]]; then
    return 0
  fi
  find "${home_dir}" -maxdepth 5 \( \
    -name '*.utm' -o \
    -name '*.pvm' -o \
    -name '*.vmwarevm' -o \
    -name '*.vbox' \
  \) 2>/dev/null | head -50
}

apps="$(collect_apps)"
cli_paths="$(collect_cli)"
processes="$(collect_processes)"
vm_files="$(collect_vm_files)"

print_section "Win7POS VM host discovery"
echo "This script is read-only. It does not start, stop, install, delete, or modify VMs."

print_section "Apps found"
if [[ -n "${apps}" ]]; then
  echo "${apps}"
else
  echo "None found in /Applications or ~/Applications."
fi

print_section "CLI found"
if [[ -n "${cli_paths}" ]]; then
  echo "${cli_paths}"
else
  echo "None found in PATH."
fi

print_section "Active VM processes"
if [[ -n "${processes}" ]]; then
  echo "${processes}"
else
  echo "No active VM processes found."
fi

print_section "VM files found"
if [[ -n "${vm_files}" ]]; then
  echo "${vm_files}"
else
  echo "No *.utm, *.pvm, *.vmwarevm, or *.vbox files found in first 5 levels of HOME."
fi

print_section "Verdict"
if [[ -n "${apps}" || -n "${processes}" ]]; then
  echo "VM_HOST_FOUND"
else
  echo "VM_HOST_NOT_FOUND"
fi

if [[ -n "${cli_paths}" ]]; then
  echo "VM_CLI_FOUND"
else
  echo "VM_CLI_NOT_FOUND"
fi

if [[ -n "${vm_files}" ]]; then
  echo "VM_FILES_FOUND"
else
  echo "VM_FILES_NOT_FOUND"
fi

print_section "Next command"
if [[ -n "${cli_paths}" ]]; then
  echo "Run the matching non-destructive list command, for example:"
  echo "  utmctl list"
  echo "  prlctl list --all"
  echo "  vmrun list"
  echo "  VBoxManage list vms"
else
  echo "Install/configure a VM app manually, then rerun:"
  echo "  scripts/win7pos/vm/discover-vm-host.sh"
fi
