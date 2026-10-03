#!/usr/bin/env bash
# Prepares a Mac to receive a RoboSense Airy (manual 3.4): static 192.168.1.102/24 on the network
# service (adapter) the LiDAR is plugged into.
#
# Usage:  tools/network/setup-macos.sh --list
#         sudo tools/network/setup-macos.sh "USB 10/100/1000 LAN"
#         sudo tools/network/setup-macos.sh "USB 10/100/1000 LAN" --revert
#
# Firewall: if the macOS application firewall is on, allow incoming connections for Unity (and for the
# built player) when prompted, or in System Settings > Network > Firewall > Options.
set -euo pipefail

HOST_IP="${HOST_IP:-192.168.1.102}"
NETMASK="255.255.255.0"

if [[ "${1:-}" == "--list" || $# -eq 0 ]]; then
  networksetup -listallnetworkservices | tail -n +2
  [[ $# -eq 0 ]] && echo "Usage: sudo $0 \"<network service>\" [--revert]"
  exit 0
fi

SERVICE="$1"
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }

if [[ "${2:-}" == "--revert" ]]; then
  networksetup -setdhcp "$SERVICE"
  echo "$SERVICE is back on DHCP."
  exit 0
fi

# No router: the Airy subnet must not become the default route.
networksetup -setmanual "$SERVICE" "$HOST_IP" "$NETMASK"
echo "$SERVICE -> $HOST_IP/$NETMASK"

# The default kern.ipc.maxsockbuf (8 MB) already allows the 4 MB receive buffer the Unity receiver requests.
echo
echo "Next: power the Airy, then: ping 192.168.1.200 && python3 tools/airy_monitor.py"
