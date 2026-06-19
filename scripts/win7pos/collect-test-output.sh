#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
vm_root="${repo_root}/.win7pos-vm"
logs_dir="${vm_root}/logs"
screenshots_dir="${vm_root}/screenshots"
reports_dir="${vm_root}/reports"
source_dir=""
execute=0

usage() {
  cat <<'EOF'
Usage:
  scripts/win7pos/collect-test-output.sh [--execute] [--from <local-dir>]

Default mode is dry-run. Use --execute to copy local files and write a report.

If --from is provided, it must be a local Mac path containing files manually exported
from the guest or a mounted shared folder. The script does not read guest files directly.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --execute)
      execute=1
      shift
      ;;
    --from)
      if [[ $# -lt 2 ]]; then
        echo "--from requires a local directory." >&2
        exit 2
      fi
      source_dir="${2:-}"
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

if [[ -n "${source_dir}" && "${source_dir}" != /* ]]; then
  source_dir="${repo_root}/${source_dir}"
fi

timestamp="$(date +%Y%m%d-%H%M%S)"
report_path="${reports_dir}/smoke-${timestamp}.md"

echo "Win7POS test output collection"
echo "Mode: $([[ "${execute}" -eq 1 ]] && echo execute || echo dry-run)"
echo "Logs: ${logs_dir}"
echo "Screenshots: ${screenshots_dir}"
echo "Reports: ${reports_dir}"
if [[ -n "${source_dir}" ]]; then
  echo "Local source: ${source_dir}"
fi

if [[ -n "${source_dir}" && ! -d "${source_dir}" ]]; then
  echo "Local source directory not found: ${source_dir}" >&2
  exit 1
fi

if [[ "${execute}" -ne 1 ]]; then
  echo
  echo "Dry-run only. Would create host output folders and report:"
  echo "  ${report_path}"
  if [[ -n "${source_dir}" ]]; then
    echo "Would copy *.log/*.txt to logs and common image files to screenshots."
  fi
  echo
  echo "Run with --execute to collect."
  exit 0
fi

mkdir -p "${logs_dir}" "${screenshots_dir}" "${reports_dir}"

if [[ -n "${source_dir}" ]]; then
  find "${source_dir}" -maxdepth 2 -type f \( -iname '*.log' -o -iname '*.txt' \) -exec cp {} "${logs_dir}/" \;
  find "${source_dir}" -maxdepth 2 -type f \( -iname '*.png' -o -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.bmp' \) -exec cp {} "${screenshots_dir}/" \;
fi

log_count="$(find "${logs_dir}" -type f | wc -l | tr -d ' ')"
screenshot_count="$(find "${screenshots_dir}" -type f | wc -l | tr -d ' ')"

{
  echo "# Win7POS smoke report ${timestamp}"
  echo
  echo "## Summary"
  echo
  echo "- Logs collected: ${log_count}"
  echo "- Screenshots collected: ${screenshot_count}"
  echo "- Source: $([[ -n "${source_dir}" ]] && echo "${source_dir}" || echo "existing .win7pos-vm folders")"
  echo
  echo "## Logs"
  echo
  find "${logs_dir}" -type f | sort | sed "s#^${repo_root}/#- #"
  echo
  echo "## Screenshots"
  echo
  find "${screenshots_dir}" -type f | sort | sed "s#^${repo_root}/#- #"
  echo
  echo "## Notes"
  echo
  echo "- Add manual anomalies here after the smoke run."
} > "${report_path}"

echo "Report written: ${report_path}"
