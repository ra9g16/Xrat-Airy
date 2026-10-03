# Airy protocol notes (as implemented)

A summary of the parts of *Airy User Guide v1.0* §4.4 and Appendix A that the decoder uses, plus how
contradictions in the manual were resolved. All multi-byte fields are **big-endian** (Appendix A).

Code: `AiryUnity/Packages/com.xrat.airy/Runtime/Core/` (C#) and `tools/airy_protocol.py` (Python).

## MSOP: point data, UDP 6699, 1248 bytes

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `55 AA 05 5A` header |
| 12 | 4 | packet counter (documented range 0–65535) |
| 20 | 10 | timestamp: 6 B seconds + 4 B sub-second |
| 31 | 1 | LiDAR type, `0x10` = Airy |
| 32 | 1 | model, `0x02` = 96 beams |
| 42 | 8 × 148 | data blocks |
| 1226 | 6 | tail (…`00 FF`) |
| 1232 | 16 | reserved |

Each block is `FF EE` + azimuth (u16, 0.01°) + 48 × {distance u16 (0.5 cm), reflectivity u8}.
Blocks 1, 3, 5, 7 carry channels 1–48. Blocks 2, 4, 6, 8 carry channels 49–96 (Table 10). In dual-return
mode, blocks 1–2 are the first echo and 3–4 the second, and the same goes for 5–8. A distance of 0 means no return.

Worked example from the manual, checked by the unit tests: distance `01 0D` → 269 × 0.5 cm = **1.345 m**,
azimuth `00 8B` → **1.39°**, reflectivity `6E` → **110**.

## DIFOP: device info, UDP 7788, 1248 bytes, ~1 Hz

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | `A5 FF 00 5A 11 11 55 55` |
| 8 | 2 | motor speed (rpm) |
| 10 / 14 | 4 / 4 | LiDAR IP / destination IP |
| 18 | 6 | MAC |
| 24 / 28 | 2 / 2 | MSOP / DIFOP port |
| 40, 45, 50, 55 | 5 each | mainboard, baseboard, app, motor firmware |
| 292 | 6 | serial number |
| 300 | 1 | return mode: `00` dual, `04` strongest, `05` last, `06` first |
| 301 / 302 | 1 / 1 | time-sync mode (`00` GPS, `01` E2E-L4, `02` P2P, `03` gPTP, `04` E2E-L2) / synced flag |
| 303 | 10 | device time (6 B s + 4 B µs) |
| 1044, 1066, 1068 | 2 each | mainboard, input, 12 V-rail voltage (÷100 V) |
| 1080 | 2 | mainboard emit temperature (÷100 °C, read as signed) |
| 1092 | 28 | IMU calibration (q_x, q_y, q_z, q_w, x, y, z) |
| 1246 | 2 | `0F F0` tail |

## Geometry

```
x =  r·cos(ω)·cos(α) + R·cos(α)
y = −r·cos(ω)·sin(α) − R·sin(α)
z =  r·sin(ω) + Z
```

r = distance, ω = vertical angle of the channel, α = block azimuth plus the channel's horizontal offset.
Z = 45.34 mm, the optical-centre height from Appendix B. R = 0 by default.

## Where the manual contradicts itself

| Topic | Manual says | Implementation |
|---|---|---|
| Azimuth convention | §2.5.1 equations give `x = r cosω sinα, y = r cosω cosα`, which puts 0° on +Y | Figures 4 and 12 show 0° on +X, increasing clockwise from above (also rs_driver's convention). Default `Figure12`; `ManualEquation` is selectable for comparison |
| Timestamp sub-second unit | Table 9: "last 4 bytes nanoseconds"; its note and A.9: microseconds (0–999,999) | Values < 1,000,000 are read as µs, larger ones as ns |
| Return-mode location | Table 2: "DIFOP Offset 1"; text and Table 12: byte 300 | Byte 300 |
| IMU block size | Table 12 / A.14 title: 24 bytes; A.14 layout: 7 × 4 = 28 bytes, and the next field starts at 1120 | 28 bytes, kept raw (encoding not documented) |
| Vertical angle step | Fig. 2: 0.937°; Table 1: 0.947° (= 90°/95) | 90°/95 nominal; calibration CSV overrides |
| Packet rate | Table 8: one MSOP every 666.67 µs (1500/s); Table 1: 860,544 pts/s (≈ 2240 packets of 384 points) | Nothing depends on it. Frames split on azimuth wrap, loss is measured from the packet counter |
| Packet counter | 4-byte field, "0–65535" | Gaps counted modulo 65536; jumps of half the range or more are treated as a restart |
| Per-unit vertical/horizontal calibration | Not documented in DIFOP | Nominal table, or load a CSV |

## Frame assembly

A frame ends when the azimuth, measured from *Frame Split Angle*, jumps back by more than 180°. It also
ends when packet timestamps jump by more than 1 s (pcap loop, clock step). The decoder owns one reusable
frame buffer: `AiryDecoder.FrameCompleted` fires on the receive thread, and `AiryLidar` copies the frame
and hands it to the main thread through a double buffer.
