using System;
using System.Buffers.Binary;

namespace Xrat.Airy
{
    public enum AiryCoordinateConvention
    {
        /// <summary>
        /// Matches manual Figures 4 and 12 (and RoboSense's rs_driver): azimuth 0 deg on +X, increasing clockwise seen from above.
        /// x = r cos(w) cos(a), y = -r cos(w) sin(a), z = r sin(w).
        /// </summary>
        Figure12,
        /// <summary>
        /// The literal equations of manual 2.5.1: x = r cos(w) sin(a), y = r cos(w) cos(a), z = r sin(w).
        /// They put azimuth 0 deg on +Y, which conflicts with Figure 4; kept for comparison against other tools.
        /// </summary>
        ManualEquation,
    }

    public sealed class AiryDecoderConfig
    {
        public float MinRange = AiryProtocol.MinimumRange;
        public float MaxRange = 200f;
        /// <summary>A new frame starts when the azimuth crosses this angle (degrees).</summary>
        public float FrameSplitAngle;
        public AiryCoordinateConvention Convention = AiryCoordinateConvention.Figure12;
        public AiryAngleTable Angles = AiryAngleTable.CreateNominal();
        /// <summary>R in manual 2.5.1: horizontal distance from the optical centre to the origin.</summary>
        public float OpticalCenterRadius;
        /// <summary>Z in manual 2.5.1: height of the optical centre above the origin.</summary>
        public float OpticalCenterHeight = AiryProtocol.OpticalCenterHeight;
        /// <summary>Upper bound of points per frame; 172k is the dual-return maximum at 10 Hz.</summary>
        public int FrameCapacity = 262144;
        /// <summary>Return mode to assume until a DIFOP packet arrives.</summary>
        public AiryReturnMode AssumedReturnMode = AiryReturnMode.Strongest;
    }

    public struct AiryDecoderStats
    {
        public long MsopPackets;
        public long DifopPackets;
        public long RejectedPackets;
        public long LostPackets;
        public long Frames;
        public long WrongModelPackets;
    }

    /// <summary>
    /// Decodes MSOP/DIFOP packets and assembles points into 360 deg frames. Thread-safe: packets may be
    /// pushed from several receive threads. <see cref="FrameCompleted"/> runs on the pushing thread,
    /// and the frame is reused once the handler returns.
    /// </summary>
    public sealed class AiryDecoder
    {
        public event Action<AiryFrame> FrameCompleted;
        public event Action<AiryDifopInfo> DifopReceived;

        readonly object _sync = new object();
        readonly AiryDecoderConfig _config;
        readonly float[] _cos = new float[AiryProtocol.AzimuthUnitsPerRevolution];
        readonly float[] _sin = new float[AiryProtocol.AzimuthUnitsPerRevolution];
        readonly float[] _cosVertical = new float[AiryProtocol.ChannelCount];
        readonly float[] _sinVertical = new float[AiryProtocol.ChannelCount];
        readonly int[] _horizontalOffset = new int[AiryProtocol.ChannelCount];
        readonly int _splitAzimuth;

        AiryFrame _frame;
        AiryDifopInfo _difop;
        AiryDecoderStats _stats;
        uint _sequence;
        int _lastRelativeAzimuth = -1;
        double _lastTimestamp = double.NaN;
        long _lastPacketCount = -1;

        public AiryDecoder(AiryDecoderConfig config = null)
        {
            _config = config ?? new AiryDecoderConfig();
            _frame = new AiryFrame(Math.Max(1024, _config.FrameCapacity));

            for (int i = 0; i < AiryProtocol.AzimuthUnitsPerRevolution; i++)
            {
                double radians = i * AiryProtocol.AzimuthResolution * Math.PI / 180.0;
                _cos[i] = (float)Math.Cos(radians);
                _sin[i] = (float)Math.Sin(radians);
            }

            AiryAngleTable angles = _config.Angles ?? AiryAngleTable.CreateNominal();
            for (int ch = 0; ch < AiryProtocol.ChannelCount; ch++)
            {
                double radians = angles.Vertical[ch] * Math.PI / 180.0;
                _cosVertical[ch] = (float)Math.Cos(radians);
                _sinVertical[ch] = (float)Math.Sin(radians);
                _horizontalOffset[ch] = (int)Math.Round(angles.Horizontal[ch] / AiryProtocol.AzimuthResolution);
            }

            _splitAzimuth = WrapAzimuth((int)Math.Round(_config.FrameSplitAngle / AiryProtocol.AzimuthResolution));
        }

        public AiryDecoderConfig Config => _config;

        public AiryDifopInfo LatestDifop
        {
            get { lock (_sync) return _difop; }
        }

        public AiryDecoderStats Stats
        {
            get { lock (_sync) return _stats; }
        }

        public AiryReturnMode ReturnMode
        {
            get { lock (_sync) return CurrentReturnMode; }
        }

        AiryReturnMode CurrentReturnMode => _difop != null && _difop.ReturnMode != AiryReturnMode.Unknown
            ? _difop.ReturnMode
            : _config.AssumedReturnMode;

        /// <summary>Routes a UDP payload by its header. Returns false if it is neither MSOP nor DIFOP.</summary>
        public bool ProcessPacket(ReadOnlySpan<byte> data)
        {
            if (AiryProtocol.IsMsop(data))
                return ProcessMsop(data);
            if (AiryProtocol.IsDifop(data))
                return ProcessDifop(data);

            lock (_sync)
                _stats.RejectedPackets++;
            return false;
        }

        public bool ProcessDifop(ReadOnlySpan<byte> data)
        {
            if (!AiryDifopInfo.TryParse(data, out AiryDifopInfo info))
            {
                lock (_sync)
                    _stats.RejectedPackets++;
                return false;
            }

            lock (_sync)
            {
                _difop = info;
                _stats.DifopPackets++;
            }
            DifopReceived?.Invoke(info);
            return true;
        }

        public bool ProcessMsop(ReadOnlySpan<byte> data)
        {
            if (!AiryProtocol.IsMsop(data) || data.Length < AiryProtocol.MsopTailOffset)
            {
                lock (_sync)
                    _stats.RejectedPackets++;
                return false;
            }

            lock (_sync)
            {
                _stats.MsopPackets++;
                if (data[AiryProtocol.MsopLidarTypeOffset] != AiryProtocol.LidarTypeAiry
                    || data[AiryProtocol.MsopLidarModelOffset] != AiryProtocol.LidarModel96Beams)
                    _stats.WrongModelPackets++;

                TrackPacketLoss(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(AiryProtocol.MsopPacketCountOffset)));

                double timestamp = AiryTimestamp.Read(data.Slice(AiryProtocol.MsopTimestampOffset, 10));
                if (!double.IsNaN(_lastTimestamp) && Math.Abs(timestamp - _lastTimestamp) > 1.0)
                    EmitFrame(); // clock jump, capture gap or pcap loop
                _lastTimestamp = timestamp;

                bool dual = CurrentReturnMode == AiryReturnMode.Dual;
                for (int block = 0; block < AiryProtocol.BlocksPerPacket; block++)
                {
                    int offset = AiryProtocol.MsopHeaderSize + block * AiryProtocol.BlockSize;
                    if (data[offset] != AiryProtocol.BlockFlag0 || data[offset + 1] != AiryProtocol.BlockFlag1)
                        continue;

                    int azimuth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2));
                    if (azimuth >= AiryProtocol.AzimuthUnitsPerRevolution)
                        continue;

                    int relative = WrapAzimuth(azimuth - _splitAzimuth);
                    if (_lastRelativeAzimuth >= 0 && relative + AiryProtocol.AzimuthUnitsPerRevolution / 2 < _lastRelativeAzimuth)
                        EmitFrame();
                    _lastRelativeAzimuth = relative;

                    if (_frame.Count == 0)
                        _frame.StartTimestamp = timestamp;
                    _frame.EndTimestamp = timestamp;

                    // Table 10: odd blocks carry channels 1-48, even blocks 49-96. In dual return mode
                    // blocks 1-2 hold the first echo and 3-4 the second, repeating for blocks 5-8.
                    int firstChannel = (block & 1) * AiryProtocol.ChannelsPerBlock;
                    byte returnIndex = dual ? (byte)((block >> 1) & 1) : (byte)0;
                    DecodeBlock(data.Slice(offset + 4, AiryProtocol.ChannelsPerBlock * AiryProtocol.ChannelDataSize),
                        azimuth, firstChannel, returnIndex, timestamp);
                }
            }
            return true;
        }

        /// <summary>Emits the partially assembled frame, if any.</summary>
        public void Flush()
        {
            lock (_sync)
                EmitFrame();
        }

        /// <summary>Drops the partial frame and forgets sequence tracking (e.g. when a pcap loops).</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _frame.Count = 0;
                _lastRelativeAzimuth = -1;
                _lastTimestamp = double.NaN;
                _lastPacketCount = -1;
            }
        }

        void DecodeBlock(ReadOnlySpan<byte> channels, int azimuth, int firstChannel, byte returnIndex, double timestamp)
        {
            float minRange = _config.MinRange;
            float maxRange = _config.MaxRange;
            float radius = _config.OpticalCenterRadius;
            float height = _config.OpticalCenterHeight;
            bool manual = _config.Convention == AiryCoordinateConvention.ManualEquation;
            float cosBlock = _cos[azimuth];
            float sinBlock = _sin[azimuth];

            for (int i = 0; i < AiryProtocol.ChannelsPerBlock; i++)
            {
                int o = i * AiryProtocol.ChannelDataSize;
                int raw = channels[o] << 8 | channels[o + 1];
                if (raw == 0)
                    continue;

                float distance = raw * AiryProtocol.DistanceResolution;
                if (distance < minRange || distance > maxRange)
                    continue;

                if (_frame.Count >= _frame.Points.Length)
                {
                    EmitFrame();
                    _frame.StartTimestamp = timestamp;
                }

                int channel = firstChannel + i;
                int h = WrapAzimuth(azimuth + _horizontalOffset[channel]);
                float horizontalDistance = distance * _cosVertical[channel];

                ref AiryPoint p = ref _frame.Points[_frame.Count++];
                if (manual)
                {
                    p.X = horizontalDistance * _sin[h] + radius * cosBlock;
                    p.Y = horizontalDistance * _cos[h] + radius * sinBlock;
                }
                else
                {
                    p.X = horizontalDistance * _cos[h] + radius * cosBlock;
                    p.Y = -horizontalDistance * _sin[h] - radius * sinBlock;
                }
                p.Z = distance * _sinVertical[channel] + height;
                p.Distance = distance;
                p.Azimuth = h * AiryProtocol.AzimuthResolution;
                p.Timestamp = timestamp;
                p.Intensity = channels[o + 2];
                p.Channel = (byte)channel;
                p.ReturnIndex = returnIndex;
            }
        }

        void TrackPacketLoss(uint packetCount)
        {
            // pktcnt is documented as cycling 0..65535.
            if (_lastPacketCount >= 0)
            {
                long gap = (packetCount - _lastPacketCount) & 0xFFFF;
                if (gap > 1 && gap < 0x8000)
                    _stats.LostPackets += gap - 1;
            }
            _lastPacketCount = packetCount;
        }

        void EmitFrame()
        {
            if (_frame.Count == 0)
                return;

            _frame.Sequence = _sequence++;
            _frame.ReturnMode = CurrentReturnMode;
            _stats.Frames++;
            try
            {
                FrameCompleted?.Invoke(_frame);
            }
            finally
            {
                _frame.Count = 0;
            }
        }

        static int WrapAzimuth(int value)
        {
            value %= AiryProtocol.AzimuthUnitsPerRevolution;
            return value < 0 ? value + AiryProtocol.AzimuthUnitsPerRevolution : value;
        }
    }
}
