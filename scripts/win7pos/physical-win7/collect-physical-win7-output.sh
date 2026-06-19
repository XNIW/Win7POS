#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
bridge_root=""
reports_root="${repo_root}/.win7pos-physical/reports"
execute=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/physical-win7/collect-physical-win7-output.sh \
    --bridge-root <mounted-share-path> \
    [--target <reports-dir>] \
    [--execute]

Default mode is dry-run. Use --execute to copy outbox, logs and screenshots
from the mounted share into .win7pos-physical/reports or the target path.

The script never deletes files.
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
    --target|--reports-dir)
      if [[ $# -lt 2 ]]; then
        echo "$1 requires a path." >&2
        exit 2
      fi
      reports_root="${2:-}"
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

if [[ "${bridge_root}" != /* ]]; then
  parent_dir="$(dirname "${bridge_root}")"
  if [[ -d "${parent_dir}" ]]; then
    bridge_root="$(cd "${parent_dir}" && pwd)/$(basename "${bridge_root}")"
  else
    bridge_root="$(pwd)/${bridge_root}"
  fi
fi

if [[ "${reports_root}" != /* ]]; then
  reports_root="${repo_root}/${reports_root}"
fi

timestamp="$(date -u '+%Y%m%d-%H%M%S')"
run_dir="${reports_root}/physical-win7-${timestamp}"

echo "Win7POS physical output collector"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "Bridge root: ${bridge_root}"
echo "Reports root: ${reports_root}"
echo "Run dir: ${run_dir}"

for source_dir in outbox logs screenshots; do
  if [[ ! -d "${bridge_root}/${source_dir}" ]]; then
    echo "WARN: missing ${bridge_root}/${source_dir}"
  fi
done

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would create:"
  echo "  ${run_dir}/outbox"
  echo "  ${run_dir}/logs"
  echo "  ${run_dir}/screenshots"
  echo "  ${run_dir}/report.md"
  echo
  echo "Run with --execute to collect."
  exit 0
fi

mkdir -p "${run_dir}/outbox" "${run_dir}/logs" "${run_dir}/screenshots"

copy_files() {
  local source="$1"
  local target="$2"
  if [[ -d "${source}" ]]; then
    find "${source}" -maxdepth 1 -type f -exec cp -p {} "${target}/" \;
  fi
}

copy_files "${bridge_root}/outbox" "${run_dir}/outbox"
copy_files "${bridge_root}/logs" "${run_dir}/logs"
copy_files "${bridge_root}/screenshots" "${run_dir}/screenshots"

outbox_count="$(find "${run_dir}/outbox" -type f | wc -l | tr -d ' ')"
logs_count="$(find "${run_dir}/logs" -type f | wc -l | tr -d ' ')"
screenshots_count="$(find "${run_dir}/screenshots" -type f | wc -l | tr -d ' ')"

{
  echo "# Win7POS physical runner collection ${timestamp}"
  echo
  echo "## Summary"
  echo
  echo "- Bridge root: ${bridge_root}"
  echo "- Outbox files: ${outbox_count}"
  echo "- Log files: ${logs_count}"
  echo "- Screenshot files: ${screenshots_count}"
  echo
  echo "## Outbox"
  find "${run_dir}/outbox" -type f | sort | sed "s#^${repo_root}/#- #"
  echo
  echo "## Logs"
  find "${run_dir}/logs" -type f | sort | sed "s#^${repo_root}/#- #"
  echo
  echo "## Screenshots"
  find "${run_dir}/screenshots" -type f | sort | sed "s#^${repo_root}/#- #"
  echo
  echo "## Notes"
  echo
  echo "- Add manual Windows App UI observations here after review."
} > "${run_dir}/report.md"

echo "Collected output: ${run_dir}"
echo "Report: ${run_dir}/report.md"
