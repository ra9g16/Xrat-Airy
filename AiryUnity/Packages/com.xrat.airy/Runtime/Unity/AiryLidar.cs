using System;
using System.IO;
using System.Net;
using UnityEngine;

namespace Xrat.Airy
{
    /// <summary>
    /// Receives a RoboSense Airy stream (live UDP or a recorded .pcap), decodes it on background threads and
    /// publishes complete 360 deg frames on the main thread through <see cref="FrameReceived"/>.
    /// </summary>
    [AddComponentMenu("Xrat/Airy LiDAR")]
    [DisallowMultipleComponent]
    public sealed class AiryLidar : MonoBehaviour
    {
        public enum SourceMode
        {
            LiveUdp,
            PcapFile,
        }

        [Header("Source")]
        public SourceMode source = SourceMode.LiveUdp;

        [Tooltip("Local address to listen on. 0.0.0.0 listens on every adapter; set the adapter's IP (factory host IP is 192.168.1.102) to pin one.")]
        public string bindAddress = "0.0.0.0";

        [Tooltip("MSOP (point cloud) destination port. Factory default 6699.")]
        public int msopPort = AiryProtocol.DefaultMsopPort;

        [Tooltip("DIFOP (device info) destination port. Factory default 7788.")]
        public int difopPort = AiryProtocol.DefaultDifopPort;

        [Tooltip("Only accept packets from this LiDAR IP (factory default 192.168.1.200). Empty accepts any sender.")]
        public string lidarAddressFilter = "";

        [Tooltip("Path of a classic .pcap capture (Wireshark: Save As > pcap). Relative paths resolve against the project folder.")]
        public string pcapPath = "";

        public bool loopPcap = true;

        [Min(0f)]
        [Tooltip("Playback speed multiplier. 0 replays as fast as possible.")]
        public float playbackSpeed = 1f;

        [Header("Decoding")]
        [Tooltip("Optional per-channel calibration: 96 lines of \"vertical,horizontal\" degrees (rs_driver angle.csv layout).")]
        public TextAsset angleCalibrationCsv;

        [Tooltip("Channel order of the nominal angle table, used when no calibration CSV is assigned.")]
        public AiryChannelOrder nominalChannelOrder = AiryChannelOrder.Ascending;

        public AiryCoordinateConvention coordinateConvention = AiryCoordinateConvention.Figure12;

        [Range(0f, 359.99f)]
        [Tooltip("Azimuth (deg) where one frame ends and the next begins.")]
        public float frameSplitAngle;

        [Min(0f)] public float minRange = AiryProtocol.MinimumRange;
        [Min(0f)] public float maxRange = 200f;

        [Tooltip("Height of the optical centre above the base (Z in manual 2.5.1). Appendix B: 45.34 mm.")]
        public float opticalCenterHeight = AiryProtocol.OpticalCenterHeight;

        [Tooltip("Horizontal offset of the optical centre (R in manual 2.5.1).")]
        public float opticalCenterRadius;

        [Header("Runtime")]
        [Tooltip("Keep receiving while the Editor/Player window is unfocused.")]
        public bool runInBackground = true;

        /// <summary>Raised on the main thread when a new frame is available in <see cref="Points"/>.</summary>
        public event Action<AiryLidar> FrameReceived;

        /// <summary>Latest frame, in the LiDAR frame (see <see cref="AiryPoint"/>). Valid entries: [0, PointCount).</summary>
        public AiryPoint[] Points => _front;
        public int PointCount { get; private set; }
        public uint FrameSequence { get; private set; }
        public double FrameTimestamp { get; private set; }
        public AiryReturnMode FrameReturnMode { get; private set; } = AiryReturnMode.Unknown;

        public AiryDifopInfo Difop { get; private set; }
        public AiryDecoderStats Stats { get; private set; }
        public float PacketsPerSecond { get; private set; }
        public float FramesPerSecond { get; private set; }
        public bool IsRunning => _udpMsop != null || _udpDifop != null || _pcap != null;
        public string LastError { get; private set; }

        readonly object _swapLock = new object();
        AiryPoint[] _front = Array.Empty<AiryPoint>();
        AiryPoint[] _pending = Array.Empty<AiryPoint>();
        int _pendingCount;
        uint _pendingSequence;
        double _pendingTimestamp;
        AiryReturnMode _pendingReturnMode;
        bool _hasPending;
        string _pendingError;

        AiryDecoder _decoder;
        UdpPacketReceiver _udpMsop;
        UdpPacketReceiver _udpDifop;
        PcapPlayer _pcap;
        IPAddress _sourceFilter;

        float _rateWindowStart;
        long _rateMsop;
        long _rateFrames;

        /// <summary>Converts a LiDAR-frame point (X fwd, Y left, Z up; right-handed) to Unity local space (X right, Y up, Z fwd).</summary>
        public static Vector3 ToUnity(in AiryPoint p) => new Vector3(-p.Y, p.Z, p.X);

        public Vector3 GetWorldPosition(in AiryPoint p) => transform.TransformPoint(ToUnity(p));

        void OnEnable()
        {
            if (runInBackground)
                Application.runInBackground = true;
            StartReceiving();
        }

        void OnDisable()
        {
            StopReceiving();
        }

        /// <summary>Restarts the source and decoder, picking up any changed settings.</summary>
        public void Restart()
        {
            StopReceiving();
            if (isActiveAndEnabled)
                StartReceiving();
        }

        void StartReceiving()
        {
            LastError = null;
            try
            {
                _decoder = new AiryDecoder(BuildConfig());
                _decoder.FrameCompleted += OnFrameCompleted;
                _sourceFilter = string.IsNullOrWhiteSpace(lidarAddressFilter) ? null : IPAddress.Parse(lidarAddressFilter.Trim());

                if (source == SourceMode.LiveUdp)
                {
                    IPAddress bind = string.IsNullOrWhiteSpace(bindAddress) ? IPAddress.Any : IPAddress.Parse(bindAddress.Trim());
                    _udpMsop = new UdpPacketReceiver(bind, msopPort, OnUdpPacket, OnWorkerError);
                    _udpMsop.Start();
                    if (difopPort != msopPort)
                    {
                        _udpDifop = new UdpPacketReceiver(bind, difopPort, OnUdpPacket, OnWorkerError, 256 * 1024);
                        _udpDifop.Start();
                    }
                    Debug.Log($"[Airy] Listening for MSOP on {bind}:{msopPort} and DIFOP on {bind}:{difopPort}.", this);
                }
                else
                {
                    string path = ResolvePcapPath();
                    _pcap = new PcapPlayer(path, OnPcapPacket, OnWorkerError) { Loop = loopPcap, Speed = playbackSpeed };
                    _pcap.Looped += () => _decoder?.Reset();
                    _pcap.Start();
                    Debug.Log($"[Airy] Replaying {path}.", this);
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogError($"[Airy] Could not start {source}: {e.Message}\n"
                    + "Check the port is free, the bind address belongs to this machine, and the pcap path exists.", this);
                StopReceiving();
            }

            _rateWindowStart = Time.unscaledTime;
            _rateMsop = 0;
            _rateFrames = 0;
        }

        void StopReceiving()
        {
            _udpMsop?.Stop();
            _udpDifop?.Stop();
            _pcap?.Stop();
            _udpMsop = null;
            _udpDifop = null;
            _pcap = null;
            if (_decoder != null)
                _decoder.FrameCompleted -= OnFrameCompleted;
            _decoder = null;
        }

        AiryDecoderConfig BuildConfig()
        {
            AiryAngleTable angles = angleCalibrationCsv != null
                ? AiryAngleTable.ParseCsv(angleCalibrationCsv.text)
                : AiryAngleTable.CreateNominal(nominalChannelOrder);

            return new AiryDecoderConfig
            {
                Angles = angles,
                Convention = coordinateConvention,
                FrameSplitAngle = frameSplitAngle,
                MinRange = minRange,
                MaxRange = maxRange,
                OpticalCenterHeight = opticalCenterHeight,
                OpticalCenterRadius = opticalCenterRadius,
            };
        }

        string ResolvePcapPath()
        {
            if (string.IsNullOrWhiteSpace(pcapPath))
                throw new InvalidOperationException("Source is PcapFile but no pcap path is set.");
            string path = pcapPath.Trim();
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"pcap file not found: {path}");
            return path;
        }

        // ---- background threads ----

        void OnUdpPacket(ReadOnlySpan<byte> payload, IPAddress sender)
        {
            if (_sourceFilter != null && !_sourceFilter.Equals(sender))
                return;
            _decoder?.ProcessPacket(payload);
        }

        void OnPcapPacket(ReadOnlySpan<byte> payload, in PcapUdpPacket packet)
        {
            if (packet.DestinationPort != msopPort && packet.DestinationPort != difopPort)
                return;
            _decoder?.ProcessPacket(payload);
        }

        void OnFrameCompleted(AiryFrame frame)
        {
            lock (_swapLock)
            {
                if (_pending.Length < frame.Count)
                    _pending = new AiryPoint[Mathf.NextPowerOfTwo(frame.Count)];
                Array.Copy(frame.Points, _pending, frame.Count);
                _pendingCount = frame.Count;
                _pendingSequence = frame.Sequence;
                _pendingTimestamp = frame.StartTimestamp;
                _pendingReturnMode = frame.ReturnMode;
                _hasPending = true;
            }
        }

        void OnWorkerError(Exception e)
        {
            lock (_swapLock)
                _pendingError = e.Message;
        }

        // ---- main thread ----

        void Update()
        {
            bool newFrame = false;
            string error = null;
            lock (_swapLock)
            {
                if (_hasPending)
                {
                    (_front, _pending) = (_pending, _front);
                    PointCount = _pendingCount;
                    FrameSequence = _pendingSequence;
                    FrameTimestamp = _pendingTimestamp;
                    FrameReturnMode = _pendingReturnMode;
                    _hasPending = false;
                    newFrame = true;
                }
                if (_pendingError != null)
                {
                    error = _pendingError;
                    _pendingError = null;
                }
            }

            if (error != null && error != LastError)
            {
                LastError = error;
                Debug.LogWarning($"[Airy] {error}", this);
            }

            if (_decoder != null)
            {
                Difop = _decoder.LatestDifop;
                Stats = _decoder.Stats;
                UpdateRates();
            }

            if (newFrame)
                FrameReceived?.Invoke(this);
        }

        void UpdateRates()
        {
            float elapsed = Time.unscaledTime - _rateWindowStart;
            if (elapsed < 1f)
                return;
            AiryDecoderStats stats = Stats;
            PacketsPerSecond = (stats.MsopPackets - _rateMsop) / elapsed;
            FramesPerSecond = (stats.Frames - _rateFrames) / elapsed;
            _rateMsop = stats.MsopPackets;
            _rateFrames = stats.Frames;
            _rateWindowStart = Time.unscaledTime;
        }

        void OnValidate()
        {
            if (_pcap != null)
            {
                _pcap.Speed = playbackSpeed;
                _pcap.Loop = loopPcap;
            }
        }
    }
}
