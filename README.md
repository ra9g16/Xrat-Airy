# Xrat-Airy

A Unity 6 environment for the **RoboSense Airy** 96-beam hemispherical LiDAR, built from the *Airy User Guide v1.0*.

- **`AiryUnity/`**: a Unity 6 project with the embedded package **`com.xrat.airy`**:
  - live MSOP/DIFOP reception over UDP, or replay of Wireshark/RSView `.pcap` captures
  - a decoder that assembles 10 Hz frames (up to 172k points) on background threads
  - a GPU point-cloud renderer (one draw call; Built-in RP and URP) coloured by reflectivity, height, distance or channel
  - a status HUD and inspector showing packet rate, loss and device info (SN, firmware, voltage, temperature, time sync)
- **`tools/`**: Python helpers using only the standard library: an Airy **simulator** (no hardware needed),
  a connection **monitor**, a pcap **replayer**, and host **network setup scripts** for Windows, Linux and macOS.
- **`docs/SETUP.md`**: step-by-step setup (hardware → network → check → Unity) with troubleshooting.
  **`docs/PROTOCOL.md`**: packet layout and how contradictions in the manual were resolved.

## Quick start

**Without hardware**

```bash
python3 tools/airy_simulator.py          # emulated Airy streaming to 127.0.0.1:6699/7788
```

In Unity Hub, add `AiryUnity/` and open it with a Unity 6 editor. Choose **GameObject → Xrat → Airy LiDAR**,
then press **Play**. A simulated room with a pillar and a ball appears.

**With an Airy** (factory settings: LiDAR `192.168.1.200` → PC `192.168.1.102`, UDP 6699/7788)

```powershell
# Windows, elevated PowerShell. Linux/macOS: see tools/network/
tools\network\setup-windows.ps1 -List
tools\network\setup-windows.ps1 -InterfaceAlias "Ethernet 2"
```
```bash
ping 192.168.1.200
python3 tools/airy_monitor.py            # confirm packets arrive before opening Unity
```

Then close the monitor and press Play in Unity, as above. The Web UI is at http://192.168.1.200.

**From a recording**: on the `AiryLidar` component set *Source = PcapFile*, then click *Browse pcap…*.

## Using the package elsewhere

Package Manager → *Add package from git URL*:

```
https://github.com/ra9g16/Xrat-Airy.git?path=/AiryUnity/Packages/com.xrat.airy
```

```csharp
using UnityEngine;
using Xrat.Airy;

public class ObstacleProbe : MonoBehaviour
{
    public AiryLidar lidar;

    void OnEnable() => lidar.FrameReceived += OnFrame;
    void OnDisable() => lidar.FrameReceived -= OnFrame;

    void OnFrame(AiryLidar l)
    {
        float nearest = float.MaxValue;
        for (int i = 0; i < l.PointCount; i++)
            nearest = Mathf.Min(nearest, l.Points[i].Distance);
        Debug.Log($"frame {l.FrameSequence}: {l.PointCount} points, nearest {nearest:0.00} m");
    }
}
```

`AiryPoint` holds X/Y/Z in the LiDAR frame (X forward, Y left, Z up), plus distance, azimuth, reflectivity,
channel, return index and timestamp. `AiryLidar.ToUnity(p)` / `GetWorldPosition(p)` convert to Unity space.

## Tests

```bash
dotnet test tests/Xrat.Airy.Core.Tests          # C# decoder/pcap/DIFOP tests (same files run in Unity's Test Runner)
python3 -m unittest discover -s tools/tests     # Python protocol + simulator round-trip
```

A ready-made GitHub Actions workflow running both, plus a check that every package asset has a `.meta` file,
is in `tools/ci/ci.yml`. Copy it to `.github/workflows/` to enable it.

## Layout

```
AiryUnity/                         Unity 6 project (open this in Unity Hub)
  Packages/com.xrat.airy/
    Runtime/Core/                  engine-independent: protocol, decoder, pcap reader, UDP receiver
    Runtime/Unity/                 AiryLidar, AiryPointCloudRenderer, AiryStatusHud, point shader
    Editor/                        inspector, GameObject/Tools menus
    Tests/Editor/                  NUnit tests (Unity Test Runner and dotnet)
docs/                              SETUP.md, PROTOCOL.md
tests/Xrat.Airy.Core.Tests/        dotnet project running the package tests outside Unity
tools/                             simulator, monitor, pcap replay, network setup, .meta generator, CI workflow
```

Not affiliated with RoboSense. "RoboSense" and "Airy" are trademarks of Suteng Innovation Technology Co., Ltd.
