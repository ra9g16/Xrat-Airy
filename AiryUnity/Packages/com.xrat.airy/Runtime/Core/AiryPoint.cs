using System.Runtime.InteropServices;

namespace Xrat.Airy
{
    /// <summary>
    /// One decoded return, in the LiDAR frame of manual 4.1 / Figure 12:
    /// right-handed, origin at the centre of the base, +X forward (0 deg), +Y left, +Z up, metres.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AiryPoint
    {
        public float X;
        public float Y;
        public float Z;
        public float Distance;
        /// <summary>Horizontal angle in degrees (block azimuth plus channel offset), clockwise from +X seen from above.</summary>
        public float Azimuth;
        public double Timestamp;
        /// <summary>Calibrated reflectivity, 1..255 (2.5.2).</summary>
        public byte Intensity;
        /// <summary>Zero-based channel (laser) index, 0..95.</summary>
        public byte Channel;
        /// <summary>0 for single return or the first echo of dual return, 1 for the second echo.</summary>
        public byte ReturnIndex;
    }

    /// <summary>
    /// A full 360 deg sweep. Instances are reused by the decoder: copy what you need inside the callback.
    /// </summary>
    public sealed class AiryFrame
    {
        public AiryPoint[] Points;
        public int Count;
        public uint Sequence;
        public double StartTimestamp;
        public double EndTimestamp;
        public AiryReturnMode ReturnMode;

        public AiryFrame(int capacity)
        {
            Points = new AiryPoint[capacity];
        }
    }
}
