#!/usr/bin/env bash
set -euo pipefail

bridge_root=""
job=""
execute=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/physical-win7/send-physical-win7-job.sh \
    --bridge-root <mounted-share-path> \
    --job env-report|smoke-pos|tasklist|collect-logs \
    [--execute]

Default mode is dry-run. Use --execute to create one .job file in inbox.

This script does not use SSH, UTM, RDP automation, or arbitrary commands.
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
        echo "--job requires a job name." >&2
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

if [[ -z "${bridge_root}" ]]; then
  echo "Missing required --bridge-root <mounted-share-path>." >&2
  usage
  exit 2
fi

if [[ -z "${job}" ]]; then
  echo "Missing required --job." >&2
  usage
  exit 2
fi

case "${job}" in
  env-report) job_file="env-report.job" ;;
  smoke-pos) job_file="smoke-pos.job" ;;
  tasklist) job_file="tasklist.job" ;;
  collect-logs) job_file="collect-logs.job" ;;
  *)
    echo "Unsupported job: ${job}" >&2
    echo "Allowed jobs: env-report, smoke-pos, tasklist, collect-logs" >&2
    exit 2
    ;;
esac

if [[ "${bridge_root}" != /* ]]; then
  parent_dir="$(dirname "${bridge_root}")"
  if [[ -d "${parent_dir}" ]]; then
    bridge_root="$(cd "${parent_dir}" && pwd)/$(basename "${bridge_root}")"
  else
    bridge_root="$(pwd)/${bridge_root}"
  fi
fi

inbox="${bridge_root}/inbox"
outbox="${bridge_root}/outbox"
job_path="${inbox}/${job_file}"

echo "Win7POS physical job sender"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "Bridge root: ${bridge_root}"
echo "Job: ${job_file}"
echo "Inbox: ${inbox}"
echo "Outbox: ${outbox}"

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would create:"
  echo "  ${job_path}"
  echo
  echo "Run with --execute to send the job."
  exit 0
fi

if [[ ! -d "${inbox}" ]]; then
  cat >&2 <<EOF
Inbox not found:
  ${inbox}

Start the Windows 7 bridge first, or point --bridge-root to the mounted
RDP/SMB share that contains the bridge folders.
EOF
  exit 1
fi

if [[ -e "${job_path}" ]]; then
  echo "Refusing to overwrite existing job: ${job_path}" >&2
  exit 1
fi

{
  echo "job=${job}"
  echo "created_at_utc=$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  echo "note=Bridge ignores file contents and uses the allowlisted filename only."
} > "${job_path}"

echo "Job created: ${job_path}"
echo "Read output from: ${outbox}"
