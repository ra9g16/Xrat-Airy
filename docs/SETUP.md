# Environment setup: RoboSense Airy + Unity 6

This guide follows the *Airy User Guide v1.0* (section numbers in brackets) and adds what Unity needs.
Do it in this order: **hardware → host network → check without Unity → Unity**. A problem caught at
one step won't look like a Unity bug at a later one.

---

## 0. What you need

| Item | Notes |
|---|---|
| Airy LiDAR + interface cable [3.1, 3.3.2] | The cable splits into a DC 5.5×2.1 power jack, an RJ45 port and a 4-pin GPS/PPS connector. |
| Power supply, **9–32 V DC** [2.4] | The optional adapter is 12 V / 3.34 A. At 12 V the supply must give **≥ 2 A** [6], or the unit keeps restarting at start-up. |
| A wired Ethernet port on the PC | Use one port only for the LiDAR, not the one carrying your internet connection. 100BASE-TX [2.4]. |
| Unity Hub + **Unity 6** (6000.0 LTS or newer) | The project pins 6000.0.58f2. Any Unity 6 editor opens it; Hub offers to switch versions. |
| Python 3.8+ | Only for the tools in `tools/` (simulator, monitor, replay). Standard library only. |
| Wireshark (optional) | Finds an unknown LiDAR IP [3.4] and records `.pcap` captures. |
| RSView (optional) | RoboSense's viewer [4.3]. Get it from robosense.ai (or your FAE for prototypes). Use it to cross-check geometry. |

## 1. Hardware [3.2–3.4]

1. Mount the Airy on a flat, rigid surface (flatness < 0.2 mm) with 3 × M3 screws, 13 ± 1 kgf·cm. Leave the
   dome's field of view clear: the beams cover the whole upper hemisphere. Leave ≥ 2 cm slack in the cable [3.2].
2. Connect the LiDAR's aviation plug to the interface cable, and the cable's RJ45 to the PC.
3. Plug in power. After power-on the red and green LEDs on the interface box stay lit [3.4], and the motor spins
   at 600 rpm (10 Hz).

> **Safety [1.5]:** Class 1 eye-safe laser (IEC 60825-1:2014). The housing can get hot. Do not open the unit.
> To clean the dome, rinse grit off with water first, then wipe with a soft cloth and a neutral solution [5.3].

## 2. Host network [3.4]

Factory defaults (Table 7):

| Device | IP | MSOP (points) | DIFOP (device info) |
|---|---|---|---|
| Airy | 192.168.1.200 | UDP 6699 | UDP 7788 |
| PC | **192.168.1.102** | UDP 6699 | UDP 7788 |

The Airy sends unicast to its configured *destination IP*, so the PC must actually own `192.168.1.102`
(netmask 255.255.255.0). Being on the same subnet is enough to reach the Web UI, but packets only arrive
at the destination address.

### Scripted

| OS | Command |
|---|---|
| Windows (admin PowerShell) | `tools\network\setup-windows.ps1 -List` then `tools\network\setup-windows.ps1 -InterfaceAlias "Ethernet 2"` |
| Linux | `tools/network/setup-linux.sh --list` then `sudo tools/network/setup-linux.sh enp3s0` |
| macOS | `tools/network/setup-macos.sh --list` then `sudo tools/network/setup-macos.sh "USB 10/100/1000 LAN"` |

Each script sets the static IP with no gateway. It also opens inbound UDP 6699/7788 in the firewall
(Windows Defender, ufw or firewalld). On Linux it raises `net.core.rmem_max` so the receiver can use a 4 MB
socket buffer. Pass `-Revert` / `--revert` to undo.

> The Windows script removes the adapter's other IPv4 addresses. Point it at the LiDAR's dedicated adapter.

### By hand (Windows, as in the manual)

Control Panel → Network and Internet → Network and Sharing Center → your Ethernet → Properties →
*Internet Protocol Version 4 (TCP/IPv4)* → *Use the following IP address*: `192.168.1.102`,
mask `255.255.255.0`, no gateway.

Unity listens on UDP, so **Windows Firewall must allow the Unity Editor**. The first time you press Play,
Windows may ask. A cable straight to a LiDAR counts as a *Public* network, so tick **Public** too. The
script's port rule covers this without the prompt.

### Unknown LiDAR IP [3.4 step 2]

If the unit was reconfigured, start Wireshark on the adapter and filter for `arp`. The LiDAR (vendor
"SutengIn", RoboSense's MAC prefix) broadcasts `Who has <PC IP>? Tell <LiDAR IP>`. Give the PC the address
being asked for. To catch the data itself, use `udp.port == 6699 || udp.port == 7788`.

Avoid routers with DHCP between the LiDAR and the PC [6]. A direct cable or an unmanaged switch is best.
Under heavy load, other broadcast traffic on the same network causes packet loss.

## 3. Check without Unity

```bash
ping 192.168.1.200                # the LiDAR answers
python3 tools/airy_monitor.py     # packet rate, loss and decoded DIFOP, once per second
```

Expected:

```
MSOP   2250 pkt/s     864000 pts/s  total 11234  lost 0  DIFOP 5  from 192.168.1.200
SN XXXXXXXXXXXX  192.168.1.200 -> 192.168.1.102  MAC 40:2C:76:..
  ports MSOP 6699 / DIFOP 7788  motor 600 rpm  return Strongest
  ...
```

(Exact packet rates depend on firmware. The manual gives both a 666.67 µs packet interval and 860,544 pts/s,
which don't quite agree. What matters: steady rate, `lost` stays at 0, ~10 frames/s.)

The Web UI at **http://192.168.1.200** [4.2] sets the destination IP and ports, return mode, phase lock and
time sync, and upgrades firmware. After changing the destination IP or ports there, update `AiryLidar`
to match.

> Only one program can bind a UDP port. Close the monitor, RSView and Wireshark-with-capture-filters-that-bind
> (Wireshark itself does not bind) before pressing Play in Unity.

### No hardware yet?

```bash
python3 tools/airy_simulator.py                 # emulated Airy on 127.0.0.1 (room, pillar, ball)
python3 tools/airy_simulator.py --pcap room.pcap --frames 50
```

## 4. Unity 6

1. Unity Hub → **Add → Add project from disk** → select `AiryUnity/`. If the pinned 6000.0.58f2 isn't
   installed, pick any installed Unity 6 editor when Hub asks.
2. First import compiles the embedded package `Packages/com.xrat.airy`.
3. **GameObject → Xrat → Airy LiDAR**. This adds `AiryLidar` (receiver/decoder), `AiryPointCloudRenderer`
   and `AiryStatusHud`.
4. Press **Play**. Look at the Scene view, or put the Main Camera at about (0, 1.5, −4). The HUD and the
   inspector show packet rates and the decoded DIFOP.

`AiryLidar` sets `Application.runInBackground` so data keeps flowing when the editor loses focus.
**Tools → Xrat Airy → Enable Run In Background** writes the same setting into Player Settings for builds.

### Key settings (`AiryLidar`)

| Field | Default | Meaning |
|---|---|---|
| Source | LiveUdp | `PcapFile` replays a capture instead (loops, adjustable speed) |
| Bind Address | 0.0.0.0 | Set to `192.168.1.102` to listen on the LiDAR adapter only |
| MSOP / DIFOP Port | 6699 / 7788 | Must match the Web UI |
| Lidar Address Filter | empty | Set `192.168.1.200` to ignore other senders |
| Angle Calibration Csv | none | 96 rows of `vertical,horizontal` degrees (see §5) |
| Nominal Channel Order | Ascending | Channel 1 at 0° (horizon) … channel 96 at 90° (zenith) |
| Coordinate Convention | Figure12 | Azimuth 0° = +X (away from the connector), clockwise seen from above |
| Frame Split Angle | 0° | Where one sweep ends and the next begins |
| Min / Max Range | 0.1 / 200 m | 0.1 m is the blind zone [2.4] |
| Optical Center Height | 0.04534 m | Appendix B; points are relative to the base centre [4.1] |

### Coordinate frames

The Airy frame [4.1, Fig. 12] is right-handed, Z up, origin at the centre of the base, with +X pointing away
from the cable connector. Unity is left-handed and Y up. The package converts:

```
Unity.x = −Airy.Y     Unity.y = Airy.Z     Unity.z = Airy.X
```

So with the GameObject at identity, the LiDAR's forward (+X) is Unity's +Z, and its dome points up (+Y).
Move or rotate the GameObject to place the sensor in your scene. `AiryLidar.GetWorldPosition(point)`
applies the transform.

### Render pipelines

The shipped project uses the Built-in pipeline, so there's nothing to set up. The renderer
(`Graphics.RenderPrimitives` plus an unlit, tag-less shader) also works unchanged in **URP**. For a URP
project, install URP from the Package Manager or create the project from the URP template, then add the package.

For XR headsets, the point shader uses Unity's stereo-instancing macros, so *Single Pass Instanced* and
*Multi Pass* should both work. Neither has been tested on a headset yet. If points show in only one eye,
switch the XR plug-in to *Multi Pass*. Point size stays in pixels per eye.

### Using the package in another Unity 6 project

Package Manager → **+ → Add package from git URL…**

```
https://github.com/ra9g16/Xrat-Airy.git?path=/AiryUnity/Packages/com.xrat.airy
```

Or copy `AiryUnity/Packages/com.xrat.airy` into that project's `Packages/` folder.

## 5. Accuracy: angles, time and return modes

- **Vertical angles.** The manual only says the 96 beams are spread *uniformly* over 0–90° (≈0.947° steps) [2.3].
  It doesn't document the per-unit calibration. The default table uses that nominal spread, so expect small
  distortions. For exact geometry, assign a 96-row `vertical,horizontal` CSV (rs_driver's `angle.csv` layout)
  built from your unit's calibration. RSView's CSV export lists each laser's angles. If the room looks
  upside-down in elevation (ceiling detail near the horizon), switch *Nominal Channel Order* to Descending.
- **Blind sector.** Every 10th frame has a ~20° gap starting at 180° (about 5 ms per second) [2.3]. This is normal.
- **Time sync** [2.5.5]. Choose GPS+PPS, PTP (1588v2) or gPTP (802.1AS) in the Web UI. The DIFOP status line
  shows the mode and whether it is locked. Point timestamps come from the packet header, in seconds.
- **Return mode** [2.5.3]. Strongest (factory), Last, First or Dual. In Dual, `AiryPoint.ReturnIndex` marks the
  second echo (0/1), and point counts double.

## 6. Recording and replay

- Record with Wireshark (capture filter `udp port 6699 or udp port 7788`) or with RSView. Save as **pcap,
  not pcapng** (Wireshark: *File → Save As → Wireshark/tcpdump – pcap*). Neither RSView [4.3.1] nor this
  package reads pcapng.
- Replay in Unity: set `AiryLidar` Source to *PcapFile* and click *Browse pcap…*.
- Replay over the network (exercises the live path): `python3 tools/pcap_replay.py capture.pcap --loop`.
- Summarise a capture: `python3 tools/airy_monitor.py --pcap capture.pcap`.

## 7. Troubleshooting

| Symptom | Check |
|---|---|
| Motor doesn't spin | Aviation plug seated; cable damage [3.3.3, 6] |
| Restarts at power-on | Supply polarity, ≥ 2 A at 12 V; mounting screws not over-tight on an uneven surface [6] |
| `ping` fails | PC IP 192.168.1.102/24 on the right adapter; LEDs on; try the Wireshark ARP trick (§2) |
| Monitor shows nothing, ping works | Firewall; destination IP in the Web UI isn't this PC; another app owns the port |
| Monitor works, Unity shows nothing | Monitor/RSView still running (port busy: see Console); Windows Firewall blocking *Unity.exe*; Bind Address isn't a local IP |
| Data stops when the editor is unfocused | `runInBackground` on the component, or **Tools → Xrat Airy → Enable Run In Background** |
| `lost` keeps rising | Other traffic on the link; use a direct cable [6]. On Linux run the setup script (rmem_max) |
| "not Airy/96-beam" warning | The stream comes from another RoboSense model (header type ≠ 0x10 / model ≠ 0x02) |
| Geometry looks skewed or mirrored | Coordinate Convention, Nominal Channel Order, or load the calibration CSV (§5) |
| pcap won't open | It's pcapng: re-save as pcap |

If the manual's checklist doesn't help, RoboSense support is support@robosense.cn [7].
