#!/bin/bash
# SD Card Importer - Watch mode, auto-confirm, web UI. Delete from source when copied or skipped.
# Destination from .env (DESTINATION_PATH). Add WEB_URL=http://192.168.1.251:5050/ to .env for Telegram monitor link
cd /home/pi/SdCardCopier || { echo "Directory not found. Run deploy-to-pi.ps1 first."; read -p "Press Enter to close..."; exit 1; }

# Don't let these override the app's runtime lookup (can cause "no frameworks found")
unset DOTNET_ROOT
unset DOTNET_MULTILEVEL_LOOKUP

# Check if port 5050 is already in use (can cause "Web server failed to start")
if command -v ss &>/dev/null && ss -tlnp 2>/dev/null | grep -q ':5050 '; then
  echo "Warning: Port 5050 may be in use. Close any other SDCardImporter or change port with -p 5051"
fi

# Get Pi IP for display
PI_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo "Starting... Web UI will be at http://${PI_IP:-localhost}:5050/"
echo ""

# Try native executable first; if it fails (e.g. "no frameworks found"), fall back to dotnet exec (uses system .NET)
if ./SDCardImporter -w -y --web --delete-skipped --skip-space-check; then
  EXIT=0
else
  EXIT=$?
  if command -v dotnet &>/dev/null && [ -f SDCardImporter.dll ]; then
    echo "Retrying with: dotnet exec SDCardImporter.dll ..."
    dotnet exec SDCardImporter.dll -w -y --web --delete-skipped --skip-space-check
    EXIT=$?
  fi
fi

if [ $EXIT -ne 0 ]; then
  echo ""
  read -p "App exited with error. Press Enter to close..."
fi
exit $EXIT
