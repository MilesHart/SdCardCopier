#!/usr/bin/env bash
set -euo pipefail

ACTION="build"
PROJECT="importer"
RUNTIME="linux-arm64"
CONFIGURATION="Release"
SELF_CONTAINED="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --action)
      ACTION="${2:-}"
      shift 2
      ;;
    --project)
      PROJECT="${2:-}"
      shift 2
      ;;
    --runtime)
      RUNTIME="${2:-}"
      shift 2
      ;;
    --configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    --self-contained)
      SELF_CONTAINED="true"
      shift
      ;;
    -h|--help)
      cat <<'EOF'
Usage:
  ./deploy/build-release.sh [options]

Options:
  --action <build|publish>                 Default: build
  --project <importer|desktop|solution>    Default: importer
  --runtime <win-x64|linux-x64|linux-arm|linux-arm64>  Default: linux-arm64
  --configuration <Release|Debug>          Default: Release
  --self-contained                          Publish as self-contained

Examples:
  ./deploy/build-release.sh
  ./deploy/build-release.sh --action publish --runtime linux-arm64
  ./deploy/build-release.sh --action publish --runtime linux-arm --self-contained
EOF
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 1
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

case "$PROJECT" in
  importer) TARGET="$PROJECT_ROOT/SDCardImporter.csproj" ;;
  desktop) TARGET="$PROJECT_ROOT/SDCardImporter.Desktop/SDCardImporter.Desktop.csproj" ;;
  solution) TARGET="$PROJECT_ROOT/SDCardImporter.sln" ;;
  *) echo "Unsupported project: $PROJECT" >&2; exit 1 ;;
esac

if [[ ! -f "$TARGET" ]]; then
  echo "Target not found: $TARGET" >&2
  exit 1
fi

if [[ "$ACTION" == "build" ]]; then
  echo "Running: dotnet build $TARGET -c $CONFIGURATION"
  dotnet build "$TARGET" -c "$CONFIGURATION"
  exit $?
fi

if [[ "$ACTION" == "publish" ]]; then
  if [[ "$PROJECT" == "solution" ]]; then
    echo "Publish does not support --project solution. Use importer or desktop." >&2
    exit 1
  fi

  SUFFIX="fd"
  if [[ "$SELF_CONTAINED" == "true" ]]; then
    SUFFIX="sc"
  fi

  OUTPUT_DIR="$PROJECT_ROOT/publish/$RUNTIME-$SUFFIX"

  echo "Running: dotnet publish $TARGET -c $CONFIGURATION -r $RUNTIME --self-contained $SELF_CONTAINED -o $OUTPUT_DIR"
  dotnet publish "$TARGET" -c "$CONFIGURATION" -r "$RUNTIME" --self-contained "$SELF_CONTAINED" -o "$OUTPUT_DIR"
  exit $?
fi

echo "Unsupported action: $ACTION" >&2
exit 1
