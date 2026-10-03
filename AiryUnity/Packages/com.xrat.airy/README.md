# Xrat Airy LiDAR (com.xrat.airy)

Unity 6 package for the RoboSense Airy 96-beam LiDAR: UDP (MSOP 6699 / DIFOP 7788) or `.pcap` input,
background decoding into 10 Hz frames, and a GPU point-cloud renderer.

- **GameObject → Xrat → Airy LiDAR** creates a ready-to-run object (`AiryLidar` + `AiryPointCloudRenderer` + `AiryStatusHud`).
- Subscribe to `AiryLidar.FrameReceived` (main thread) and read `Points[0..PointCount)`. Don't keep the array between frames.
- Core types (`AiryDecoder`, `PcapReader`, `UdpPacketReceiver`) have no UnityEngine dependency.

Full setup guide and protocol notes: `docs/SETUP.md` and `docs/PROTOCOL.md` in the repository.
