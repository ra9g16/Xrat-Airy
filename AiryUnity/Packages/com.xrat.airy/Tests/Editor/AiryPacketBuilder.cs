using System;
using System.Buffers.Binary;
using System.IO;

namespace Xrat.Airy.Tests
{
    /// <summary>Builds MSOP/DIFOP payloads and pcap files byte-for-byte as described in the manual.</summary>
    static class AiryPacketBuilder
    {
        public delegate (ushort distance, byte intensity) ChannelFunc(int block, int channel);

        public static byte[] Msop(uint packetCount, double timestamp, ushort[] blockAzimuths, ChannelFunc channels)
        {
            var data = new byte[AiryProtocol.PacketSize];
            data[0] = 0x55; data[1] = 0xAA; data[2] = 0x05; data[3] = 0x5A;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(AiryProtocol.MsopPacketCountOffset), packetCount);
            AiryTimestamp.Write(data.AsSpan(AiryProtocol.MsopTimestampOffset, 10), timestamp);
            data[AiryProtocol.MsopLidarTypeOffset] = AiryProtocol.LidarTypeAiry;
            data[AiryProtocol.MsopLidarModelOffset] = AiryProtocol.LidarModel96Beams;

            for (int block = 0; block < AiryProtocol.BlocksPerPacket; block++)
            {
                int offset = AiryProtocol.MsopHeaderSize + block * AiryProtocol.BlockSize;
                data[offset] = 0xFF;
                data[offset + 1] = 0xEE;
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), blockAzimuths[block]);
                for (int i = 0; i < AiryProtocol.ChannelsPerBlock; i++)
                {
                    int channel = (block & 1) * AiryProtocol.ChannelsPerBlock + i;
                    var (distance, intensity) = channels(block, channel);
                    int o = offset + 4 + i * 3;
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(o), distance);
                    data[o + 2] = intensity;
                }
            }

            data[AiryProtocol.MsopTailOffset + 4] = 0x00;
            data[AiryProtocol.MsopTailOffset + 5] = 0xFF;
            return data;
        }

        /// <summary>Four firings per packet: blocks (1,2), (3,4), (5,6), (7,8) share an azimuth.</summary>
        public static ushort[] SingleReturnAzimuths(int first, int step)
        {
            var result = new ushort[8];
            for (int b = 0; b < 8; b++)
                result[b] = (ushort)((first + (b / 2) * step) % 36000);
            return result;
        }

        public static byte[] Difop(AiryReturnMode returnMode = AiryReturnMode.Strongest)
        {
            var data = new byte[AiryProtocol.PacketSize];
            byte[] header = { 0xA5, 0xFF, 0x00, 0x5A, 0x11, 0x11, 0x55, 0x55 };
            header.CopyTo(data, 0);
            data[8] = 0x02; data[9] = 0x58; // 600 rpm, Appendix A.1 example
            new byte[] { 192, 168, 1, 200 }.CopyTo(data, AiryProtocol.DifopLidarIpOffset);
            new byte[] { 192, 168, 1, 102 }.CopyTo(data, AiryProtocol.DifopDestinationIpOffset);
            new byte[] { 0x40, 0x2C, 0x76, 0x08, 0x4A, 0xCC }.CopyTo(data, AiryProtocol.DifopMacOffset);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(AiryProtocol.DifopMsopPortOffset), 6699);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(AiryProtocol.DifopDifopPortOffset), 7788);
            new byte[] { 0x00, 0x03, 0x04, 0x01, 0x50 }.CopyTo(data, AiryProtocol.DifopMainboardFirmwareOffset);
            new byte[] { 0x00, 0x23, 0x03, 0x08, 0x02 }.CopyTo(data, AiryProtocol.DifopAppFirmwareOffset);
            new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC }.CopyTo(data, AiryProtocol.DifopSerialNumberOffset);
            data[AiryProtocol.DifopReturnModeOffset] = (byte)returnMode;
            data[AiryProtocol.DifopTimeSyncModeOffset] = 0x03;
            data[AiryProtocol.DifopTimeSyncStatusOffset] = 0x01;
            AiryTimestamp.Write(data.AsSpan(AiryProtocol.DifopTimeOffset, 10), 1700000000.25);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(AiryProtocol.DifopMainboardVoltageOffset), 1205);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(AiryProtocol.DifopMachineVoltageOffset), 1198);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(AiryProtocol.DifopBottomBoardVoltageOffset), 1201);
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(AiryProtocol.DifopMainboardTemperatureOffset), 4250);
            data[AiryProtocol.DifopTailOffset] = 0x0F;
            data[AiryProtocol.DifopTailOffset + 1] = 0xF0;
            return data;
        }

        /// <summary>Wraps payloads in Ethernet/IPv4/UDP inside a little-endian microsecond pcap.</summary>
        public static byte[] Pcap(params (double timestamp, int port, byte[] payload)[] packets)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0xA1B2C3D4u);
                writer.Write((ushort)2);
                writer.Write((ushort)4);
                writer.Write(0);
                writer.Write(0u);
                writer.Write(65535u);
                writer.Write(1u); // Ethernet

                foreach (var (timestamp, port, payload) in packets)
                {
                    var frame = new byte[14 + 20 + 8 + payload.Length];
                    frame[12] = 0x08; frame[13] = 0x00;
                    frame[14] = 0x45;
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)(20 + 8 + payload.Length));
                    frame[22] = 64;
                    frame[23] = 17;
                    new byte[] { 192, 168, 1, 200 }.CopyTo(frame, 26);
                    new byte[] { 192, 168, 1, 102 }.CopyTo(frame, 30);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), (ushort)port);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), (ushort)port);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38), (ushort)(8 + payload.Length));
                    payload.CopyTo(frame, 42);

                    uint seconds = (uint)Math.Floor(timestamp);
                    writer.Write(seconds);
                    writer.Write((uint)Math.Round((timestamp - seconds) * 1e6));
                    writer.Write((uint)frame.Length);
                    writer.Write((uint)frame.Length);
                    writer.Write(frame);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
