#!/usr/bin/env python3
"""Check an Airy connection without Unity: listen on the MSOP/DIFOP ports, report packet rates,
loss and the device information from DIFOP.

  python3 tools/airy_monitor.py                 # listen on all adapters, ports 6699/7788
  python3 tools/airy_monitor.py --bind 192.168.1.102
  python3 tools/airy_monitor.py --pcap capture.pcap   # summarise a capture instead

If nothing arrives: check the host IP is 192.168.1.102/24 (manual 3.4), the interface box LEDs are on,
and the firewall allows inbound UDP 6699/7788. Close Unity/RSView first; only one program can bind a port.
"""
from __future__ import annotations

import argparse
import select
import socket
import sys
import time

import airy_protocol as ap


class Stats:
    def __init__(self):
        self.msop = 0
        self.difop = 0
        self.other = 0
        self.lost = 0
        self.last_count = None
        self.sources = set()
        self.wrong_model = 0
        self.points = 0
        self.last_difop = None

    def on_payload(self, payload: bytes, source: str = "") -> None:
        if source:
            self.sources.add(source)
        msop = ap.decode_msop(payload)
        if msop is not None:
            self.msop += 1
            if (msop.lidar_type, msop.lidar_model) != (ap.LIDAR_TYPE_AIRY, ap.LIDAR_MODEL_96):
                self.wrong_model += 1
            if self.last_count is not None:
                gap = (msop.packet_count - self.last_count) & 0xFFFF
                if 1 < gap < 0x8000:
                    self.lost += gap - 1
            self.last_count = msop.packet_count
            self.points += sum(1 for _, channels in msop.blocks for d, _ in channels if d)
            return
        difop = ap.decode_difop(payload)
        if difop is not None:
            self.difop += 1
            self.last_difop = difop
            return
        self.other += 1


def monitor_live(args) -> int:
    sockets = []
    for port in sorted({args.msop_port, args.difop_port}):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind((args.bind, port))
        except OSError as e:
            print(f"cannot bind {args.bind}:{port}: {e}. Is Unity/RSView already listening?", file=sys.stderr)
            return 1
        sockets.append(s)
    print(f"listening on {args.bind} UDP {args.msop_port} (MSOP) / {args.difop_port} (DIFOP); Ctrl+C to stop")

    stats = Stats()
    window_start = time.monotonic()
    window_msop = 0
    window_points = 0
    printed_difop = None
    deadline = time.monotonic() + args.duration if args.duration > 0 else None
    try:
        while deadline is None or time.monotonic() < deadline:
            readable, _, _ = select.select(sockets, [], [], 0.5)
            for s in readable:
                payload, (source, _) = s.recvfrom(65535)
                stats.on_payload(payload, source)

            now = time.monotonic()
            if now - window_start >= 1.0:
                elapsed = now - window_start
                rate = (stats.msop - window_msop) / elapsed
                pts = (stats.points - window_points) / elapsed
                print(f"MSOP {rate:6.0f} pkt/s  {pts:9.0f} pts/s  total {stats.msop}  lost {stats.lost}  "
                      f"DIFOP {stats.difop}  from {', '.join(sorted(stats.sources)) or '-'}")
                if stats.msop == 0 and stats.difop == 0:
                    print("  nothing received yet: check IP 192.168.1.102/24, cabling/power and the firewall")
                if stats.wrong_model:
                    print(f"  warning: {stats.wrong_model} MSOP packets are not Airy 96-beam (type 0x10/model 0x02)")
                window_start, window_msop, window_points = now, stats.msop, stats.points
                if stats.last_difop is not None and stats.last_difop.serial != printed_difop:
                    print(stats.last_difop.summary())
                    printed_difop = stats.last_difop.serial
    except KeyboardInterrupt:
        pass
    return 0 if stats.msop else 2


def monitor_pcap(args) -> int:
    stats = Stats()
    first = last = None

    def on_packet(timestamp, port, payload):
        nonlocal first, last
        first = timestamp if first is None else first
        last = timestamp
        stats.on_payload(payload)

    ap.read_pcap_udp(args.pcap, on_packet)
    span = (last - first) if first is not None else 0
    print(f"{args.pcap}: {stats.msop} MSOP, {stats.difop} DIFOP, {stats.other} other UDP over {span:.2f} s")
    if span > 0:
        print(f"  {stats.msop / span:.0f} MSOP pkt/s, {stats.points / span:.0f} points/s, {stats.lost} lost")
    if stats.last_difop:
        print(stats.last_difop.summary())
    return 0 if stats.msop else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--msop-port", type=int, default=ap.MSOP_PORT)
    parser.add_argument("--difop-port", type=int, default=ap.DIFOP_PORT)
    parser.add_argument("--duration", type=float, default=0, help="seconds to listen (0 = until Ctrl+C)")
    parser.add_argument("--pcap", help="summarise a pcap capture instead of listening")
    args = parser.parse_args()
    return monitor_pcap(args) if args.pcap else monitor_live(args)


if __name__ == "__main__":
    sys.exit(main())
