#!/bin/bash
# Clean up disk space on Raspberry Pi
# Run: bash pi-cleanup-space.sh  (or chmod +x and ./pi-cleanup-space.sh)
# Use: scp deploy/pi-cleanup-space.sh pi@192.168.1.251:~/
#      ssh pi@192.168.1.251 "bash ~/pi-cleanup-space.sh"

set -e
echo "=== Pi disk space cleanup ==="
echo "Before:"
df -h /

echo ""
echo "1. Clearing apt cache..."
sudo apt clean

echo ""
echo "2. Removing unused packages..."
sudo apt autoremove -y

echo ""
echo "3. Trimming old journal logs (keep 3 days)..."
sudo journalctl --vacuum-time=3d

echo ""
echo "4. Removing thumbnail cache..."
rm -rf ~/.cache/thumbnails/* 2>/dev/null || true

echo ""
echo "5. Removing old SdCardCopier deploy archives (if any)..."
rm -f ~/SdCardCopier/SdCardCopier-deploy.tar.gz 2>/dev/null || true

echo ""
echo "6. Clearing pip cache (if present)..."
rm -rf ~/.cache/pip 2>/dev/null || true

echo ""
echo "After:"
df -h /

echo ""
echo "Done. Consider: raspi-config → Advanced Options → Expand Filesystem (if root partition hasn't been expanded)."
