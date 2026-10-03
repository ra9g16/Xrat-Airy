using System;
using System.Buffers.Binary;
using System.IO;

namespace Xrat.Airy
{
    public struct PcapUdpPacket
    {
        public double Timestamp;
        public uint SourceAddress;
        public uint DestinationAddress;
        public int SourcePort;
        public int DestinationPort;
        /// <summary>UDP payload. Valid until the next call to <see cref="PcapReader.TryReadNext"/>.</summary>
        public ArraySegment<byte> Payload;
    }

    /// <summary>
    /// Minimal reader for classic libpcap files (what RSView and Wireshark's "pcap" format write; manual 4.3.1).
    /// Yields IPv4/UDP payloads; everything else is skipped. pcapng is not supported, same as RSView.
    /// </summary>
    public sealed class PcapReader : IDisposable
    {
        const int LinkTypeNull = 0;
        const int LinkTypeEthernet = 1;
        const int LinkTypeRaw = 101;
        const int LinkTypeRawAlt = 12;
        const int LinkTypeLinuxSll = 113;
        const int LinkTypeLinuxSll2 = 276;

        readonly Stream _stream;
        readonly bool _ownsStream;
        readonly bool _swap;
        readonly bool _nanosecond;
        readonly int _linkType;
        readonly byte[] _recordHeader = new byte[16];
        byte[] _buffer = new byte[65536];

        public PcapReader(string path)
            : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), true)
        {
        }

        public PcapReader(Stream stream, bool ownsStream = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _ownsStream = ownsStream;

            var header = new byte[24];
            if (!ReadExactly(header, 24))
                throw new InvalidDataException("File is too short to be a pcap capture.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            switch (magic)
            {
                case 0xA1B2C3D4: _swap = false; _nanosecond = false; break;
                case 0xD4C3B2A1: _swap = true; _nanosecond = false; break;
                case 0xA1B23C4D: _swap = false; _nanosecond = true; break;
                case 0x4D3CB2A1: _swap = true; _nanosecond = true; break;
                case 0x0A0D0D0A:
                    throw new NotSupportedException(
                        "pcapng is not supported. In Wireshark use File > Save As > \"Wireshark/tcpdump/... - pcap\".");
                default:
                    throw new InvalidDataException($"Not a pcap file (magic 0x{magic:X8}).");
            }

            _linkType = (int)(U32(header, 20) & 0x0FFFFFFF);
        }

        public int LinkType => _linkType;

        public bool TryReadNext(out PcapUdpPacket packet)
        {
            packet = default;
            while (true)
            {
                if (!ReadExactly(_recordHeader, 16))
                    return false;

                uint seconds = U32(_recordHeader, 0);
                uint fraction = U32(_recordHeader, 4);
                int capturedLength = (int)U32(_recordHeader, 8);
                if (capturedLength < 0 || capturedLength > 16 * 1024 * 1024)
                    throw new InvalidDataException($"Corrupt pcap record length {capturedLength}.");

                if (_buffer.Length < capturedLength)
                    _buffer = new byte[capturedLength];
                if (!ReadExactly(_buffer, capturedLength))
                    return false;

                if (TryExtractUdp(_buffer, capturedLength, ref packet))
                {
                    packet.Timestamp = seconds + fraction * (_nanosecond ? 1e-9 : 1e-6);
                    return true;
                }
            }
        }

        bool TryExtractUdp(byte[] frame, int length, ref PcapUdpPacket packet)
        {
            int offset;
            switch (_linkType)
            {
                case LinkTypeEthernet:
                {
                    if (length < 14)
                        return false;
                    int etherType = frame[12] << 8 | frame[13];
                    offset = 14;
                    while (etherType == 0x8100 || etherType == 0x88A8) // VLAN tags
                    {
                        if (length < offset + 4)
                            return false;
                        etherType = frame[offset + 2] << 8 | frame[offset + 3];
                        offset += 4;
                    }
                    if (etherType != 0x0800)
                        return false;
                    break;
                }
                case LinkTypeLinuxSll:
                    if (length < 16 || (frame[14] << 8 | frame[15]) != 0x0800)
                        return false;
                    offset = 16;
                    break;
                case LinkTypeLinuxSll2:
                    if (length < 20 || (frame[0] << 8 | frame[1]) != 0x0800)
                        return false;
                    offset = 20;
                    break;
                case LinkTypeNull:
                    offset = 4;
                    break;
                case LinkTypeRaw:
                case LinkTypeRawAlt:
                    offset = 0;
                    break;
                default:
                    return false;
            }

            // IPv4
            if (length < offset + 20 || frame[offset] >> 4 != 4)
                return false;
            int ipHeaderLength = (frame[offset] & 0x0F) * 4;
            int ipTotalLength = frame[offset + 2] << 8 | frame[offset + 3];
            bool fragmented = (frame[offset + 6] & 0x20) != 0 || ((frame[offset + 6] & 0x1F) << 8 | frame[offset + 7]) != 0;
            if (frame[offset + 9] != 17 || fragmented || ipHeaderLength < 20)
                return false;

            int udp = offset + ipHeaderLength;
            if (length < udp + 8)
                return false;

            int udpLength = frame[udp + 4] << 8 | frame[udp + 5];
            int payloadLength = Math.Min(udpLength - 8, Math.Min(length, offset + ipTotalLength) - udp - 8);
            if (payloadLength <= 0)
                return false;

            packet.SourceAddress = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(frame, offset + 12, 4));
            packet.DestinationAddress = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(frame, offset + 16, 4));
            packet.SourcePort = frame[udp] << 8 | frame[udp + 1];
            packet.DestinationPort = frame[udp + 2] << 8 | frame[udp + 3];
            packet.Payload = new ArraySegment<byte>(frame, udp + 8, payloadLength);
            return true;
        }

        uint U32(byte[] data, int offset)
        {
            var span = new ReadOnlySpan<byte>(data, offset, 4);
            return _swap ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
        }

        bool ReadExactly(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            if (_ownsStream)
                _stream.Dispose();
        }
    }
}
