#!/usr/bin/env bash
# Prepares a Linux PC to receive a RoboSense Airy (manual 3.4):
#   - static 192.168.1.102/24 on the interface the LiDAR is plugged into (NetworkManager or iproute2)
#   - opens UDP 6699/7788 in ufw or firewalld when either is active
#   - raises net.core.rmem_max so the 4 MB socket buffer requested by the Unity receiver is honoured
#
# Usage:  sudo tools/network/setup-linux.sh <interface>        e.g. enp3s0
#         sudo tools/network/setup-linux.sh <interface> --revert
#         tools/network/setup-linux.sh --list
set -euo pipefail

HOST_IP="${HOST_IP:-192.168.1.102}"
PREFIX="${PREFIX:-24}"
MSOP_PORT="${MSOP_PORT:-6699}"
DIFOP_PORT="${DIFOP_PORT:-7788}"
CON_NAME="airy-lidar"

if [[ "${1:-}" == "--list" || $# -eq 0 ]]; then
  ip -brief link show | grep -v '^lo '
  [[ $# -eq 0 ]] && echo "Usage: sudo $0 <interface> [--revert]"
  exit 0
fi

IFACE="$1"
MODE="${2:-}"
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
ip link show "$IFACE" >/dev/null

if [[ "$MODE" == "--revert" ]]; then
  if command -v nmcli >/dev/null && nmcli -t -f NAME connection show | grep -qx "$CON_NAME"; then
    nmcli connection delete "$CON_NAME"
  else
    ip addr del "$HOST_IP/$PREFIX" dev "$IFACE" 2>/dev/null || true
  fi
  if command -v ufw >/dev/null; then
    ufw delete allow "$MSOP_PORT/udp" >/dev/null 2>&1 || true
    ufw delete allow "$DIFOP_PORT/udp" >/dev/null 2>&1 || true
  fi
  if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --remove-port="$MSOP_PORT/udp" --remove-port="$DIFOP_PORT/udp" >/dev/null || true
    firewall-cmd --reload >/dev/null
  fi
  echo "Reverted $IFACE."
  exit 0
fi

if command -v nmcli >/dev/null && systemctl is-active --quiet NetworkManager; then
  nmcli connection delete "$CON_NAME" >/dev/null 2>&1 || true
  nmcli connection add type ethernet ifname "$IFACE" con-name "$CON_NAME" \
    ipv4.method manual ipv4.addresses "$HOST_IP/$PREFIX" ipv4.never-default yes ipv6.method disabled >/dev/null
  nmcli connection up "$CON_NAME" >/dev/null
  echo "NetworkManager profile '$CON_NAME': $IFACE -> $HOST_IP/$PREFIX"
else
  ip link set "$IFACE" up
  ip addr replace "$HOST_IP/$PREFIX" dev "$IFACE"
  echo "$IFACE -> $HOST_IP/$PREFIX (not persistent; add it to your netplan/systemd-networkd config)"
fi

if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow "$MSOP_PORT/udp" >/dev/null
  ufw allow "$DIFOP_PORT/udp" >/dev/null
  echo "ufw: allowed UDP $MSOP_PORT,$DIFOP_PORT"
fi
if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
  firewall-cmd --permanent --add-port="$MSOP_PORT/udp" --add-port="$DIFOP_PORT/udp" >/dev/null
  firewall-cmd --reload >/dev/null
  echo "firewalld: allowed UDP $MSOP_PORT,$DIFOP_PORT"
fi

# ~1.9 MB/s of MSOP; the default rmem_max (208 KB) caps the receive buffer and drops packets during hitches.
sysctl -q -w net.core.rmem_max=8388608
echo "net.core.rmem_max=8388608" > /etc/sysctl.d/90-airy-lidar.conf
echo "net.core.rmem_max raised to 8 MB"

echo
echo "Next: power the Airy, then: ping 192.168.1.200 && python3 tools/airy_monitor.py"
