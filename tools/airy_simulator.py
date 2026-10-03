#!/usr/bin/env python3
"""Emulate a RoboSense Airy so the Unity project can be developed without the sensor.

Streams MSOP (port 6699, ~2250 packets/s) and DIFOP (port 7788, 1 Hz) in the format of the Airy User Guide,
for a synthetic room: four walls, a ceiling, a pillar and a ball. Or writes the same stream to a .pcap file.

  python3 tools/airy_simulator.py                         # stream to 127.0.0.1 (Unity on this machine)
  python3 tools/airy_simulator.py --host 192.168.1.102    # stream to another machine
  python3 tools/airy_simulator.py --pcap room.pcap --frames 20

The geometry uses the nominal (uniform 0..90 deg, ascending) channel table and the Figure 12 convention,
the same defaults as the Unity AiryLidar component, so the room should appear upright and undistorted.
"""
from __future__ import annotations

import argparse
import math
import socket
import sys
import time

import airy_protocol as ap

AZIMUTH_STEP = 40  # 0.4 deg (horizontal resolution in manual 2.4)
FIRINGS_PER_PACKET = 4

# Scene in the LiDAR frame (metres, Z up, origin at the sensor base).
ROOM_MIN = (-4.0, -3.0)
ROOM_MAX = (5.0, 3.5)
CEILING = 2.6
PILLAR_MIN = (1.6, -0.4, -1.0)
PILLAR_MAX = (2.2, 0.2, 1.9)
BALL_CENTER = (-1.5, 1.6, 0.9)
BALL_RADIUS = 0.45


def ray_box_entry(origin, direction, lo, hi):
    t_near, t_far = -math.inf, math.inf
    for o, d, a, b in zip(origin, direction, lo, hi):
        if abs(d) < 1e-12:
            if o < a or o > b:
                return None
            continue
        t1, t2 = (a - o) / d, (b - o) / d
        if t1 > t2:
            t1, t2 = t2, t1
        t_near, t_far = max(t_near, t1), min(t_far, t2)
        if t_near > t_far:
            return None
    return t_near if t_near > 0 else None


def ray_sphere(origin, direction, center, radius):
    oc = [o - c for o, c in zip(origin, center)]
    b = sum(d * x for d, x in zip(direction, oc))
    c = sum(x * x for x in oc) - radius * radius
    disc = b * b - c
    if disc < 0:
        return None
    t = -b - math.sqrt(disc)
    return t if t > 0 else None


def trace(origin, direction):
    """Return (distance, reflectivity) of the first hit, or (0, 0) for no return."""
    hits = []
    # Room interior: exit distance through walls/ceiling.
    t_exit = math.inf
    for axis, (lo, hi) in enumerate(zip(ROOM_MIN, ROOM_MAX)):
        d = direction[axis]
        if d > 1e-12:
            t_exit = min(t_exit, (hi - origin[axis]) / d)
        elif d < -1e-12:
            t_exit = min(t_exit, (lo - origin[axis]) / d)
    wall_reflectivity = 35
    if direction[2] > 1e-12:
        t_ceiling = (CEILING - origin[2]) / direction[2]
        if t_ceiling < t_exit:
            t_exit, wall_reflectivity = t_ceiling, 90
    hits.append((t_exit, wall_reflectivity))

    t = ray_box_entry(origin, direction, PILLAR_MIN, PILLAR_MAX)
    if t is not None:
        hits.append((t, 200))
    t = ray_sphere(origin, direction, BALL_CENTER, BALL_RADIUS)
    if t is not None:
        hits.append((t, 140))

    distance, reflectivity = min(hits)
    if not math.isfinite(distance) or distance > 100:
        return 0, 0
    return int(round(distance / ap.DISTANCE_RESOLUTION)), reflectivity


def build_revolution():
    """Precompute one 360 deg sweep: list of (azimuth raw, 96 channel tuples)."""
    origin = (0.0, 0.0, ap.OPTICAL_CENTER_HEIGHT)
    vertical = [math.radians(v) for v in ap.nominal_vertical_angles()]
    firings = []
    for azimuth in range(0, 36000, AZIMUTH_STEP):
        a = math.radians(azimuth * ap.AZIMUTH_RESOLUTION)
        cos_a, sin_a = math.cos(a), math.sin(a)
        channels = []
        for w in vertical:
            cw = math.cos(w)
            direction = (cw * cos_a, -cw * sin_a, math.sin(w))  # Figure 12 convention
            channels.append(trace(origin, direction))
        firings.append((azimuth, channels))
    return firings


def packets_per_revolution(firings):
    return [firings[i:i + FIRINGS_PER_PACKET] for i in range(0, len(firings), FIRINGS_PER_PACKET)]


def difop_info(args, now):
    return ap.DifopInfo(
        motor_rpm=args.rpm,
        dest_ip=args.host if args.host.count(".") == 3 else ap.HOST_IP,
        msop_port=args.msop_port,
        difop_port=args.difop_port,
        serial=bytes.fromhex("51AEA1000001"),
        app_fw=bytes.fromhex("0000000001"),
        return_mode=0x04,
        device_time=now,
        machine_voltage=12.0,
        mainboard_voltage=11.9,
        bottom_voltage=12.0,
        temperature=38.5,
    )


def run_pcap(args, packets):
    rev_period = 60.0 / args.rpm
    packet_period = rev_period / len(packets)
    t = time.time()
    count = 0
    with ap.PcapWriter(args.pcap) as writer:
        for frame in range(args.frames):
            writer.write(t, ap.encode_difop(difop_info(args, t)), args.difop_port)
            for firings in packets:
                writer.write(t, ap.encode_msop(count, t, firings), args.msop_port)
                count = (count + 1) & 0xFFFF
                t += packet_period
    print(f"wrote {args.frames} frames ({count} MSOP packets) to {args.pcap}")


def run_stream(args, packets):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    msop_target = (args.host, args.msop_port)
    difop_target = (args.host, args.difop_port)
    packet_period = 60.0 / args.rpm / len(packets)
    print(f"streaming to {args.host}: MSOP :{args.msop_port} ({1 / packet_period:.0f} pkt/s), "
          f"DIFOP :{args.difop_port} (1 Hz). Ctrl+C to stop.")

    start = time.perf_counter()
    end = start + args.duration if args.duration > 0 else math.inf
    sent = 0
    next_difop = start
    next_report = start + 5
    try:
        while True:
            for firings in packets:
                due = start + sent * packet_period
                now = time.perf_counter()
                if now >= end:
                    return
                if due > now + 0.002:
                    time.sleep(due - now)
                wall = time.time()
                sock.sendto(ap.encode_msop(sent & 0xFFFF, wall, firings), msop_target)
                sent += 1
                if now >= next_difop:
                    sock.sendto(ap.encode_difop(difop_info(args, wall)), difop_target)
                    next_difop += 1.0
                if now >= next_report:
                    rate = sent / (now - start)
                    print(f"  {sent} MSOP packets sent, {rate:.0f} pkt/s")
                    next_report += 5
    except KeyboardInterrupt:
        pass
    finally:
        print(f"stopped after {sent} MSOP packets")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1", help="destination IP (default 127.0.0.1)")
    parser.add_argument("--msop-port", type=int, default=ap.MSOP_PORT)
    parser.add_argument("--difop-port", type=int, default=ap.DIFOP_PORT)
    parser.add_argument("--rpm", type=int, default=600, help="motor speed; 600 rpm = 10 Hz frames")
    parser.add_argument("--duration", type=float, default=0, help="seconds to stream (0 = until Ctrl+C)")
    parser.add_argument("--pcap", help="write a pcap file instead of streaming")
    parser.add_argument("--frames", type=int, default=20, help="frames to write with --pcap")
    args = parser.parse_args()

    t0 = time.perf_counter()
    packets = packets_per_revolution(build_revolution())
    print(f"scene traced in {time.perf_counter() - t0:.1f} s: {len(packets)} packets per revolution")

    if args.pcap:
        run_pcap(args, packets)
    else:
        run_stream(args, packets)
    return 0


if __name__ == "__main__":
    sys.exit(main())
