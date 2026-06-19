#!/usr/bin/env bash
set -euo pipefail

bridge_root=""
job=""
execute=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/vm/send-builder-job.sh --bridge-root <path> --job <name> [--execute]

Jobs:
  env-report
  build-dry-run
  build-release
  package-drop
  screenshot

Default mode is dry-run. With --execute, this script creates a simple .job file
in <bridge-root>/inbox. It does not require a VM CLI and does not start VMs.

In the Windows Builder VM, start the bridge first:
  powershell -ExecutionPolicy Bypass -File scripts\win7pos\windows\bridge\start-builder-bridge.ps1 -Watch
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bridge-root)
      if [[ $# -lt 2 ]]; then
        echo "--bridge-root requires a path." >&2
        exit 2
      fi
      bridge_root="${2:-}"
      shift 2
      ;;
    --job)
      if [[ $# -lt 2 ]]; then
        echo "--job requires a value." >&2
        exit 2
      fi
      job="${2:-}"
      shift 2
      ;;
    --execute)
      execute=1
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

if [[ -z "${bridge_root}" || -z "${job}" ]]; then
  echo "Missing --bridge-root or --job." >&2
  usage
  exit 2
fi

case "${job}" in
  env-report|build-dry-run|build-release|package-drop|screenshot) ;;
  *)
    echo "Unsupported job: ${job}" >&2
    usage
    exit 2
    ;;
esac

if [[ "${bridge_root}" != /* ]]; then
  bridge_root="$(pwd)/${bridge_root}"
fi

inbox="${bridge_root}/inbox"
job_path="${inbox}/${job}.job"

echo "Win7POS Builder job sender"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "Bridge root: ${bridge_root}"
echo "Job: ${job}"
echo "Job file: ${job_path}"

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would create:"
  echo "  ${inbox}"
  echo "  ${job_path}"
  echo
  echo "Start this in the Windows Builder VM:"
  echo "  powershell -ExecutionPolicy Bypass -File scripts\\win7pos\\windows\\bridge\\start-builder-bridge.ps1 -Watch"
  exit 0
fi

mkdir -p "${inbox}"

if [[ -e "${job_path}" ]]; then
  echo "Job already exists: ${job_path}" >&2
  echo "Wait for the Builder bridge to move it to outbox/done or outbox/failed, then retry." >&2
  exit 1
fi

{
  echo "job=${job}"
  echo "created_at=$(date '+%Y-%m-%d %H:%M:%S %z')"
  echo "created_by=send-builder-job.sh"
} > "${job_path}"

echo "Job queued: ${job_path}"
echo
echo "Watch Windows Builder output under:"
echo "  ${bridge_root}/outbox"
