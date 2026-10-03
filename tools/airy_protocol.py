"""RoboSense Airy MSOP/DIFOP packet encoding and decoding (Airy User Guide v1.0, section 4.4 / Appendix A).

Standard library only, shared by the simulator, monitor and tests. Mirrors the C# decoder in
AiryUnity/Packages/com.xrat.airy/Runtime/Core.
"""
from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field
from typing import Callable, List, Optional, Sequence, Tuple

PACKET_SIZE = 1248
MSOP_PORT = 6699
DIFOP_PORT = 7788
LIDAR_IP = "192.168.1.200"
HOST_IP = "192.168.1.102"

CHANNELS = 96
BLOCKS = 8
CHANNELS_PER_BLOCK = 48
HEADER_SIZE = 42
BLOCK_SIZE = 4 + CHANNELS_PER_BLOCK * 3  # flag + azimuth + 48 x (distance u16, reflectivity u8)
TAIL_OFFSET = HEADER_SIZE + BLOCKS * BLOCK_SIZE  # 1226
DISTANCE_RESOLUTION = 0.005  # m
AZIMUTH_RESOLUTION = 0.01  # deg
OPTICAL_CENTER_HEIGHT = 0.04534  # m, Appendix B
NOMINAL_VERTICAL_STEP = 90.0 / (CHANNELS - 1)

MSOP_ID = b"\x55\xAA\x05\x5A"
DIFOP_ID = b"\xA5\xFF\x00\x5A\x11\x11\x55\x55"
LIDAR_TYPE_AIRY = 0x10
LIDAR_MODEL_96 = 0x02

RETURN_MODES = {0x00: "Dual", 0x04: "Strongest", 0x05: "Last", 0x06: "First"}
SYNC_MODES = {0x00: "GPS", 0x01: "PTP E2E-L4", 0x02: "PTP P2P", 0x03: "gPTP", 0x04: "PTP E2E-L2"}


def nominal_vertical_angles(descending: bool = False) -> List[float]:
    angles = [i * NOMINAL_VERTICAL_STEP for i in range(CHANNELS)]
    return angles[::-1] if descending else angles


def write_timestamp(buf: bytearray, offset: int, t: float) -> None:
    seconds = int(math.floor(t))
    micros = min(999_999, int(round((t - seconds) * 1e6)))
    buf[offset:offset + 6] = seconds.to_bytes(6, "big")
    buf[offset + 6:offset + 10] = micros.to_bytes(4, "big")


def read_timestamp(data: bytes, offset: int) -> float:
    seconds = int.from_bytes(data[offset:offset + 6], "big")
    fraction = int.from_bytes(data[offset + 6:offset + 10], "big")
    # Table 9 says nanoseconds, Appendix A.9 says microseconds; values >= 1e6 can only be ns.
    return seconds + (fraction * 1e-6 if fraction < 1_000_000 else fraction * 1e-9)


# ---------------------------------------------------------------- MSOP

Firing = Tuple[int, Sequence[Tuple[int, int]]]  # (azimuth in 0.01 deg, 96 x (raw distance, reflectivity))


def encode_msop(packet_count: int, timestamp: float, firings: Sequence[Firing]) -> bytes:
    """Encode four single-return firings (blocks 1-2, 3-4, 5-6, 7-8)."""
    if len(firings) != BLOCKS // 2:
        raise ValueError("an MSOP packet carries 4 firings in single-return mode")
    buf = bytearray(PACKET_SIZE)
    buf[0:4] = MSOP_ID
    struct.pack_into(">I", buf, 12, packet_count & 0xFFFFFFFF)
    write_timestamp(buf, 20, timestamp)
    buf[31] = LIDAR_TYPE_AIRY
    buf[32] = LIDAR_MODEL_96
    for block in range(BLOCKS):
        azimuth, channels = firings[block // 2]
        offset = HEADER_SIZE + block * BLOCK_SIZE
        struct.pack_into(">BBH", buf, offset, 0xFF, 0xEE, azimuth % 36000)
        first = (block & 1) * CHANNELS_PER_BLOCK
        for i in range(CHANNELS_PER_BLOCK):
            distance, reflectivity = channels[first + i]
            struct.pack_into(">HB", buf, offset + 4 + i * 3, distance, reflectivity)
    buf[TAIL_OFFSET + 4] = 0x00
    buf[TAIL_OFFSET + 5] = 0xFF
    return bytes(buf)


@dataclass
class MsopPacket:
    packet_count: int
    timestamp: float
    lidar_type: int
    lidar_model: int
    blocks: List[Tuple[int, List[Tuple[int, int]]]]  # (azimuth raw, 48 x (distance raw, reflectivity))


def decode_msop(data: bytes) -> Optional[MsopPacket]:
    if len(data) < TAIL_OFFSET or data[0:4] != MSOP_ID:
        return None
    blocks = []
    for block in range(BLOCKS):
        offset = HEADER_SIZE + block * BLOCK_SIZE
        if data[offset] != 0xFF or data[offset + 1] != 0xEE:
            continue
        azimuth = struct.unpack_from(">H", data, offset + 2)[0]
        channels = [struct.unpack_from(">HB", data, offset + 4 + i * 3) for i in range(CHANNELS_PER_BLOCK)]
        blocks.append((azimuth, channels))
    return MsopPacket(
        packet_count=struct.unpack_from(">I", data, 12)[0],
        timestamp=read_timestamp(data, 20),
        lidar_type=data[31],
        lidar_model=data[32],
        blocks=blocks,
    )


def to_xyz(distance_m: float, azimuth_deg: float, elevation_deg: float,
           optical_center_height: float = OPTICAL_CENTER_HEIGHT) -> Tuple[float, float, float]:
    """LiDAR frame of manual Figure 12: +X forward, +Y left, +Z up; azimuth clockwise from +X seen from above."""
    a = math.radians(azimuth_deg)
    w = math.radians(elevation_deg)
    horizontal = distance_m * math.cos(w)
    return horizontal * math.cos(a), -horizontal * math.sin(a), distance_m * math.sin(w) + optical_center_height


# ---------------------------------------------------------------- DIFOP

@dataclass
class DifopInfo:
    motor_rpm: int = 600
    lidar_ip: str = LIDAR_IP
    dest_ip: str = HOST_IP
    mac: bytes = b"\x40\x2C\x76\x00\x00\x01"
    msop_port: int = MSOP_PORT
    difop_port: int = DIFOP_PORT
    mainboard_fw: bytes = b"\x00\x00\x00\x00\x01"
    baseboard_fw: bytes = b"\x00\x00\x00\x00\x01"
    app_fw: bytes = b"\x00\x00\x00\x00\x01"
    motor_fw: bytes = b"\x00\x00\x00\x00\x01"
    serial: bytes = b"\x00\x00\x00\x00\x00\x01"
    return_mode: int = 0x04
    sync_mode: int = 0x00
    sync_ok: bool = False
    device_time: float = 0.0
    mainboard_voltage: float = 12.0
    machine_voltage: float = 12.0
    bottom_voltage: float = 12.0
    temperature: float = 40.0
    imu_raw: bytes = field(default=b"\x00" * 28)

    def summary(self) -> str:
        fw = lambda b: " ".join(f"{x:02X}" for x in b)
        return (
            f"SN {self.serial.hex().upper()}  {self.lidar_ip} -> {self.dest_ip}  MAC {self.mac.hex(':').upper()}\n"
            f"  ports MSOP {self.msop_port} / DIFOP {self.difop_port}  motor {self.motor_rpm} rpm  "
            f"return {RETURN_MODES.get(self.return_mode, hex(self.return_mode))}\n"
            f"  time sync {SYNC_MODES.get(self.sync_mode, hex(self.sync_mode))} "
            f"({'synchronized' if self.sync_ok else 'not synchronized'})  device time {self.device_time:.6f}\n"
            f"  input {self.machine_voltage:.2f} V  mainboard {self.mainboard_voltage:.2f} V  "
            f"12V rail {self.bottom_voltage:.2f} V  emit temp {self.temperature:.2f} C\n"
            f"  firmware main {fw(self.mainboard_fw)} | base {fw(self.baseboard_fw)} | "
            f"app {fw(self.app_fw)} | motor {fw(self.motor_fw)}"
        )


def _ip_bytes(ip: str) -> bytes:
    return bytes(int(p) for p in ip.split("."))


def encode_difop(info: DifopInfo) -> bytes:
    buf = bytearray(PACKET_SIZE)
    buf[0:8] = DIFOP_ID
    struct.pack_into(">H", buf, 8, info.motor_rpm)
    buf[10:14] = _ip_bytes(info.lidar_ip)
    buf[14:18] = _ip_bytes(info.dest_ip)
    buf[18:24] = info.mac
    struct.pack_into(">H", buf, 24, info.msop_port)
    struct.pack_into(">H", buf, 28, info.difop_port)
    buf[40:45] = info.mainboard_fw
    buf[45:50] = info.baseboard_fw
    buf[50:55] = info.app_fw
    buf[55:60] = info.motor_fw
    buf[292:298] = info.serial
    buf[300] = info.return_mode
    buf[301] = info.sync_mode
    buf[302] = 1 if info.sync_ok else 0
    write_timestamp(buf, 303, info.device_time)
    struct.pack_into(">H", buf, 1044, int(round(info.mainboard_voltage * 100)))
    struct.pack_into(">H", buf, 1066, int(round(info.machine_voltage * 100)))
    struct.pack_into(">H", buf, 1068, int(round(info.bottom_voltage * 100)))
    struct.pack_into(">h", buf, 1080, int(round(info.temperature * 100)))
    buf[1092:1120] = info.imu_raw
    buf[1246] = 0x0F
    buf[1247] = 0xF0
    return bytes(buf)


def decode_difop(data: bytes) -> Optional[DifopInfo]:
    if len(data) < 1246 or data[0:8] != DIFOP_ID:
        return None
    ip = lambda o: ".".join(str(b) for b in data[o:o + 4])
    u16 = lambda o: struct.unpack_from(">H", data, o)[0]
    return DifopInfo(
        motor_rpm=u16(8),
        lidar_ip=ip(10),
        dest_ip=ip(14),
        mac=bytes(data[18:24]),
        msop_port=u16(24),
        difop_port=u16(28),
        mainboard_fw=bytes(data[40:45]),
        baseboard_fw=bytes(data[45:50]),
        app_fw=bytes(data[50:55]),
        motor_fw=bytes(data[55:60]),
        serial=bytes(data[292:298]),
        return_mode=data[300],
        sync_mode=data[301],
        sync_ok=data[302] == 1,
        device_time=read_timestamp(data, 303),
        mainboard_voltage=u16(1044) / 100,
        machine_voltage=u16(1066) / 100,
        bottom_voltage=u16(1068) / 100,
        temperature=struct.unpack_from(">h", data, 1080)[0] / 100,
        imu_raw=bytes(data[1092:1120]),
    )


# ---------------------------------------------------------------- pcap

class PcapWriter:
    """Classic little-endian microsecond pcap with Ethernet/IPv4/UDP framing (what Wireshark 'pcap' writes)."""

    def __init__(self, path: str):
        self._f = open(path, "wb")
        self._f.write(struct.pack("<IHHiIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 1))
        self._ip_id = 0

    def write(self, timestamp: float, payload: bytes, port: int,
              src_ip: str = LIDAR_IP, dst_ip: str = HOST_IP) -> None:
        udp = struct.pack(">HHHH", port, port, 8 + len(payload), 0) + payload
        self._ip_id = (self._ip_id + 1) & 0xFFFF
        header = struct.pack(">BBHHHBBH4s4s", 0x45, 0, 20 + len(udp), self._ip_id, 0x4000, 64, 17, 0,
                             _ip_bytes(src_ip), _ip_bytes(dst_ip))
        checksum = _ip_checksum(header)
        header = header[:10] + struct.pack(">H", checksum) + header[12:]
        ethernet = b"\xff\xff\xff\xff\xff\xff" + b"\x40\x2c\x76\x00\x00\x01" + b"\x08\x00"
        frame = ethernet + header + udp
        seconds = int(timestamp)
        micros = int(round((timestamp - seconds) * 1e6))
        if micros >= 1_000_000:
            seconds, micros = seconds + 1, micros - 1_000_000
        self._f.write(struct.pack("<IIII", seconds, micros, len(frame), len(frame)))
        self._f.write(frame)

    def close(self) -> None:
        self._f.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def _ip_checksum(header: bytes) -> int:
    total = sum(struct.unpack(">10H", header))
    while total >> 16:
        total = (total & 0xFFFF) + (total >> 16)
    return ~total & 0xFFFF


def read_pcap_udp(path: str, on_packet: Callable[[float, int, bytes], None]) -> int:
    """Call on_packet(timestamp, dst_port, payload) for each IPv4/UDP packet of a classic pcap (Ethernet or Linux SLL)."""
    with open(path, "rb") as f:
        header = f.read(24)
        if len(header) < 24:
            raise ValueError("file too short for a pcap header")
        magic = struct.unpack("<I", header[:4])[0]
        if magic == 0x0A0D0D0A:
            raise ValueError("pcapng is not supported; in Wireshark use File > Save As > pcap")
        formats = {0xA1B2C3D4: ("<", 1e-6), 0xD4C3B2A1: (">", 1e-6), 0xA1B23C4D: ("<", 1e-9), 0x4D3CB2A1: (">", 1e-9)}
        if magic not in formats:
            raise ValueError(f"not a pcap file (magic {magic:#010x})")
        endian, scale = formats[magic]
        link = struct.unpack(endian + "I", header[20:24])[0] & 0x0FFFFFFF
        count = 0
        while True:
            record = f.read(16)
            if len(record) < 16:
                return count
            sec, frac, caplen, _ = struct.unpack(endian + "IIII", record)
            frame = f.read(caplen)
            if len(frame) < caplen:
                return count
            if link == 1:
                offset, ethertype = 14, struct.unpack(">H", frame[12:14])[0]
                while ethertype in (0x8100, 0x88A8):
                    ethertype = struct.unpack(">H", frame[offset + 2:offset + 4])[0]
                    offset += 4
            elif link == 113:
                offset, ethertype = 16, struct.unpack(">H", frame[14:16])[0]
            else:
                continue
            if ethertype != 0x0800 or len(frame) < offset + 28 or frame[offset + 9] != 17:
                continue
            ihl = (frame[offset] & 0x0F) * 4
            udp = offset + ihl
            dst_port, length = struct.unpack(">HH", frame[udp + 2:udp + 6])
            on_packet(sec + frac * scale, dst_port, frame[udp + 8:udp + length])
            count += 1
