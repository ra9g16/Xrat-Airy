#!/usr/bin/env python3
"""Replay the Airy packets of a Wireshark/RSView .pcap to a UDP target, in real time.

Useful for exercising the live-UDP path (AiryLidar source = LiveUdp) with a real recording.
For simple viewing, AiryLidar can also open the .pcap directly (source = PcapFile).

  python3 tools/pcap_replay.py capture.pcap                    # to 127.0.0.1
  python3 tools/pcap_replay.py capture.pcap --host 192.168.1.102 --loop --speed 0.5
"""
from __future__ import annotations

import argparse
import socket
import sys
import time

import airy_protocol as ap


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("pcap")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--msop-port", type=int, default=ap.MSOP_PORT)
    parser.add_argument("--difop-port", type=int, default=ap.DIFOP_PORT)
    parser.add_argument("--speed", type=float, default=1.0, help="playback speed (0 = as fast as possible)")
    parser.add_argument("--loop", action="store_true")
    args = parser.parse_args()

    packets = []
    ap.read_pcap_udp(args.pcap, lambda t, port, payload: packets.append((t, payload)))
    packets = [(t, p) for t, p in packets if p[:4] == ap.MSOP_ID or p[:8] == ap.DIFOP_ID]
    if not packets:
        print("no MSOP/DIFOP packets found in the capture", file=sys.stderr)
        return 1
    print(f"replaying {len(packets)} packets ({packets[-1][0] - packets[0][0]:.1f} s) to {args.host}")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        while True:
            start = time.perf_counter()
            t0 = packets[0][0]
            for t, payload in packets:
                if args.speed > 0:
                    wait = (t - t0) / args.speed - (time.perf_counter() - start)
                    if wait > 0.002:
                        time.sleep(wait)
                port = args.msop_port if payload[:4] == ap.MSOP_ID else args.difop_port
                sock.sendto(payload, (args.host, port))
            if not args.loop:
                break
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
