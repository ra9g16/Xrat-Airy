using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Xrat.Airy.Tests
{
    public class AiryDecoderTests
    {
        const float Tolerance = 1e-4f;

        static AiryDecoderConfig FlatConfig()
        {
            // All beams on the horizon and no optical-centre offset make expected positions easy to read.
            var angles = AiryAngleTable.CreateNominal();
            Array.Clear(angles.Vertical, 0, angles.Vertical.Length);
            return new AiryDecoderConfig { Angles = angles, OpticalCenterHeight = 0f };
        }

        static List<AiryPoint> DecodeOne(AiryDecoder decoder, byte[] packet)
        {
            var points = new List<AiryPoint>();
            decoder.FrameCompleted += frame =>
            {
                for (int i = 0; i < frame.Count; i++)
                    points.Add(frame.Points[i]);
            };
            Assert.IsTrue(decoder.ProcessPacket(packet));
            decoder.Flush();
            return points;
        }

        [Test]
        public void ManualWorkedExample_DistanceAzimuthReflectivity()
        {
            // Manual 4.4.2.2: distance 0x010D -> 1.345 m, azimuth 0x008B -> 1.39 deg, reflectivity 0x6E -> 110.
            var packet = AiryPacketBuilder.Msop(1, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0x008B, 40),
                (block, channel) => block == 0 && channel == 0 ? ((ushort)0x010D, (byte)0x6E) : ((ushort)0, (byte)0));

            var points = DecodeOne(new AiryDecoder(), packet);

            Assert.AreEqual(1, points.Count);
            Assert.AreEqual(1.345f, points[0].Distance, Tolerance);
            Assert.AreEqual(1.39f, points[0].Azimuth, Tolerance);
            Assert.AreEqual(110, points[0].Intensity);
            Assert.AreEqual(0, points[0].Channel);
        }

        [Test]
        public void Figure12Convention_AzimuthIsClockwiseFromPlusX()
        {
            // 1 m returns at 0, 90, 180 and 270 deg on channel 0.
            var azimuths = new ushort[] { 0, 0, 9000, 9000, 18000, 18000, 27000, 27000 };
            var packet = AiryPacketBuilder.Msop(1, 100.0, azimuths,
                (block, channel) => channel == 0 ? ((ushort)200, (byte)10) : ((ushort)0, (byte)0));

            var points = DecodeOne(new AiryDecoder(FlatConfig()), packet);

            Assert.AreEqual(4, points.Count);
            AssertPoint(points[0], 1, 0, 0);   // 0 deg: forward
            AssertPoint(points[1], 0, -1, 0);  // 90 deg: right (clockwise seen from above)
            AssertPoint(points[2], -1, 0, 0);  // 180 deg: back
            AssertPoint(points[3], 0, 1, 0);   // 270 deg: left, as in Figure 4
        }

        [Test]
        public void ManualEquationConvention_PutsZeroAzimuthOnPlusY()
        {
            var config = FlatConfig();
            config.Convention = AiryCoordinateConvention.ManualEquation;
            var azimuths = new ushort[] { 0, 0, 9000, 9000, 9000, 9000, 9000, 9000 };
            var packet = AiryPacketBuilder.Msop(1, 100.0, azimuths,
                (block, channel) => channel == 0 && block < 3 ? ((ushort)200, (byte)10) : ((ushort)0, (byte)0));

            var points = DecodeOne(new AiryDecoder(config), packet);

            AssertPoint(points[0], 0, 1, 0);
            AssertPoint(points[1], 1, 0, 0);
        }

        [Test]
        public void NominalAngles_SpanZeroToNinetyDegrees()
        {
            var config = new AiryDecoderConfig { OpticalCenterHeight = 0.05f };
            var packet = AiryPacketBuilder.Msop(1, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0, 40),
                (block, channel) => block < 2 && (channel == 0 || channel == 95) ? ((ushort)400, (byte)1) : ((ushort)0, (byte)0));

            var points = DecodeOne(new AiryDecoder(config), packet);

            Assert.AreEqual(2, points.Count);
            AssertPoint(points[0], 2, 0, 0.05f);  // channel 1: horizon
            AssertPoint(points[1], 0, 0, 2.05f);  // channel 96: zenith
            Assert.AreEqual(95, points[1].Channel);
        }

        [Test]
        public void ZeroAndOutOfRangeDistancesAreDropped()
        {
            var config = FlatConfig();
            config.MinRange = 0.1f;
            config.MaxRange = 10f;
            var packet = AiryPacketBuilder.Msop(1, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0, 40),
                (block, channel) =>
                {
                    if (block != 0) return (0, 0);
                    switch (channel)
                    {
                        case 0: return ((ushort)10, (byte)1);    // 0.05 m: inside blind zone
                        case 1: return ((ushort)100, (byte)1);   // 0.5 m: kept
                        case 2: return ((ushort)4000, (byte)1);  // 20 m: beyond MaxRange
                        default: return (0, 0);
                    }
                });

            var points = DecodeOne(new AiryDecoder(config), packet);

            Assert.AreEqual(1, points.Count);
            Assert.AreEqual(1, points[0].Channel);
        }

        [Test]
        public void SecondBlockOfPairCarriesChannels49To96()
        {
            var packet = AiryPacketBuilder.Msop(1, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0, 40),
                (block, channel) => block == 1 && channel == 48 ? ((ushort)200, (byte)7) : ((ushort)0, (byte)0));

            var points = DecodeOne(new AiryDecoder(), packet);

            Assert.AreEqual(1, points.Count);
            Assert.AreEqual(48, points[0].Channel);
        }

        [Test]
        public void FramesSplitWhenAzimuthWraps()
        {
            var decoder = new AiryDecoder(FlatConfig());
            var frameSizes = new List<int>();
            decoder.FrameCompleted += frame => frameSizes.Add(frame.Count);

            // 0.4 deg steps, 4 firings per packet: 225 packets per revolution. Play two revolutions.
            uint count = 0;
            for (int rev = 0; rev < 2; rev++)
            {
                for (int start = 0; start < 36000; start += 160)
                {
                    var packet = AiryPacketBuilder.Msop(count++, 100.0 + count * 0.0004, AiryPacketBuilder.SingleReturnAzimuths(start, 40),
                        (block, channel) => ((ushort)400, (byte)1));
                    decoder.ProcessPacket(packet);
                }
            }
            decoder.Flush();

            Assert.AreEqual(2, frameSizes.Count);
            Assert.AreEqual(900 * 96, frameSizes[0]);
            Assert.AreEqual(900 * 96, frameSizes[1]);
            Assert.AreEqual(0, decoder.Stats.LostPackets);
        }

        [Test]
        public void FrameSplitAngleMovesTheSeam()
        {
            var config = FlatConfig();
            config.FrameSplitAngle = 180f;
            var decoder = new AiryDecoder(config);
            var firstAzimuths = new List<float>();
            decoder.FrameCompleted += frame => firstAzimuths.Add(frame.Points[0].Azimuth);

            uint count = 0;
            for (int start = 0; start < 36000 * 2; start += 160)
            {
                var packet = AiryPacketBuilder.Msop(count++, 100.0, AiryPacketBuilder.SingleReturnAzimuths(start % 36000, 40),
                    (block, channel) => channel == 0 ? ((ushort)400, (byte)1) : ((ushort)0, (byte)0));
                decoder.ProcessPacket(packet);
            }

            Assert.AreEqual(2, firstAzimuths.Count);
            Assert.AreEqual(0f, firstAzimuths[0], Tolerance);   // partial first sweep
            Assert.AreEqual(180f, firstAzimuths[1], Tolerance); // full sweep starts at the seam
        }

        [Test]
        public void PacketCounterGapsAreCountedAsLoss()
        {
            var decoder = new AiryDecoder();
            foreach (uint count in new uint[] { 10, 11, 14, 60000, 65535, 1 })
            {
                decoder.ProcessPacket(AiryPacketBuilder.Msop(count, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0, 40),
                    (block, channel) => (0, 0)));
            }

            // 11 -> 14 loses two packets. 14 -> 60000 jumps half the counter range or more, which is treated as a
            // restart rather than loss. 60000 -> 65535 loses 5534. 65535 -> 1 wraps past 0, losing one.
            Assert.AreEqual(2 + 5534 + 1, decoder.Stats.LostPackets);
            Assert.AreEqual(6, decoder.Stats.MsopPackets);
        }

        [Test]
        public void DualReturnModeFromDifopTagsSecondEcho()
        {
            var decoder = new AiryDecoder(FlatConfig());
            Assert.IsTrue(decoder.ProcessPacket(AiryPacketBuilder.Difop(AiryReturnMode.Dual)));

            var azimuths = new ushort[] { 100, 100, 100, 100, 140, 140, 140, 140 };
            var packet = AiryPacketBuilder.Msop(1, 100.0, azimuths,
                (block, channel) => channel % 48 == 0 ? ((ushort)(200 + block), (byte)1) : ((ushort)0, (byte)0));

            var points = DecodeOne(decoder, packet);

            Assert.AreEqual(8, points.Count);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 1, 0, 0, 1, 1 }, points.ConvertAll(p => p.ReturnIndex));
        }

        [Test]
        public void RejectsForeignPackets()
        {
            var decoder = new AiryDecoder();
            Assert.IsFalse(decoder.ProcessPacket(new byte[AiryProtocol.PacketSize]));
            Assert.IsFalse(decoder.ProcessPacket(new byte[] { 0x55, 0xAA, 0x05, 0x5A }));
            Assert.AreEqual(2, decoder.Stats.RejectedPackets);
        }

        [Test]
        public void Timestamp_RoundTripsAndAcceptsNanoseconds()
        {
            var bytes = new byte[10];
            AiryTimestamp.Write(bytes, 1700000000.123456);
            Assert.AreEqual(1700000000.123456, AiryTimestamp.Read(bytes), 1e-6);

            // 500,000,000 can only be nanoseconds.
            bytes[6] = 0x1D; bytes[7] = 0xCD; bytes[8] = 0x65; bytes[9] = 0x00;
            Assert.AreEqual(1700000000.5, AiryTimestamp.Read(bytes), 1e-6);
        }

        [Test]
        public void Difop_ParsesAppendixAFields()
        {
            Assert.IsTrue(AiryDifopInfo.TryParse(AiryPacketBuilder.Difop(), out var info));

            Assert.AreEqual(600, info.MotorSpeedRpm);
            Assert.AreEqual("192.168.1.200", info.LidarAddress);
            Assert.AreEqual("192.168.1.102", info.DestinationAddress);
            Assert.AreEqual("40:2C:76:08:4A:CC", info.MacAddress);
            Assert.AreEqual(6699, info.MsopPort);
            Assert.AreEqual(7788, info.DifopPort);
            Assert.AreEqual("00 03 04 01 50", info.MainboardFirmware);
            Assert.AreEqual("00 23 03 08 02", info.AppFirmware);
            Assert.AreEqual("123456789ABC", info.SerialNumber);
            Assert.AreEqual(AiryReturnMode.Strongest, info.ReturnMode);
            Assert.AreEqual(AiryTimeSyncMode.Gptp, info.TimeSyncMode);
            Assert.IsTrue(info.TimeSynchronized);
            Assert.AreEqual(1700000000.25, info.DeviceTime, 1e-6);
            Assert.AreEqual(12.05f, info.MainboardVoltage, Tolerance);
            Assert.AreEqual(11.98f, info.MachineVoltage, Tolerance);
            Assert.AreEqual(12.01f, info.BottomBoardVoltage, Tolerance);
            Assert.AreEqual(42.5f, info.MainboardEmitTemperature, Tolerance);
            Assert.AreEqual(28, info.ImuCalibrationRaw.Length);
        }

        [Test]
        public void AngleTable_ParsesCsvWithHeaderAndComments()
        {
            var lines = new List<string> { "# exported from RSView", "vertical,horizontal" };
            for (int i = 0; i < 96; i++)
                lines.Add($"{i * 0.9f:0.###},{(i % 2 == 0 ? 0.1 : -0.1)}");

            var table = AiryAngleTable.ParseCsv(string.Join("\n", lines));

            Assert.AreEqual(0f, table.Vertical[0], Tolerance);
            Assert.AreEqual(85.5f, table.Vertical[95], Tolerance);
            Assert.AreEqual(-0.1f, table.Horizontal[1], Tolerance);
        }

        [Test]
        public void AngleTable_RejectsWrongRowCount()
        {
            Assert.Throws<FormatException>(() => AiryAngleTable.ParseCsv("1,0\n2,0\n"));
        }

        [Test]
        public void AngleTable_NominalDescendingStartsAtZenith()
        {
            var table = AiryAngleTable.CreateNominal(AiryChannelOrder.Descending);
            Assert.AreEqual(90f, table.Vertical[0], Tolerance);
            Assert.AreEqual(0f, table.Vertical[95], Tolerance);
            Assert.AreEqual(0.947f, table.Vertical[0] - table.Vertical[1], 1e-3f);
        }

        [Test]
        public void Pcap_ReadsUdpPayloadsAndFeedsDecoder()
        {
            var msop = AiryPacketBuilder.Msop(1, 100.0, AiryPacketBuilder.SingleReturnAzimuths(0, 40),
                (block, channel) => ((ushort)400, (byte)1));
            var difop = AiryPacketBuilder.Difop();
            byte[] file = AiryPacketBuilder.Pcap((1.5, 6699, msop), (1.75, 7788, difop));

            var decoder = new AiryDecoder();
            using (var reader = new PcapReader(new MemoryStream(file)))
            {
                Assert.IsTrue(reader.TryReadNext(out var first));
                Assert.AreEqual(1.5, first.Timestamp, 1e-6);
                Assert.AreEqual(6699, first.DestinationPort);
                Assert.AreEqual(0xC0A801C8u, first.SourceAddress);
                Assert.AreEqual(AiryProtocol.PacketSize, first.Payload.Count);
                Assert.IsTrue(decoder.ProcessPacket(first.Payload.AsSpan()));

                Assert.IsTrue(reader.TryReadNext(out var second));
                Assert.AreEqual(7788, second.DestinationPort);
                Assert.IsTrue(decoder.ProcessPacket(second.Payload.AsSpan()));

                Assert.IsFalse(reader.TryReadNext(out _));
            }

            Assert.AreEqual(1, decoder.Stats.MsopPackets);
            Assert.AreEqual(1, decoder.Stats.DifopPackets);
            Assert.AreEqual("123456789ABC", decoder.LatestDifop.SerialNumber);
        }

        [Test]
        public void Pcap_RejectsPcapng()
        {
            var pcapng = new byte[] { 0x0A, 0x0D, 0x0D, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Throws<NotSupportedException>(() => new PcapReader(new MemoryStream(pcapng)));
        }

        static void AssertPoint(AiryPoint p, float x, float y, float z)
        {
            Assert.AreEqual(x, p.X, Tolerance, "X");
            Assert.AreEqual(y, p.Y, Tolerance, "Y");
            Assert.AreEqual(z, p.Z, Tolerance, "Z");
        }
    }
}
