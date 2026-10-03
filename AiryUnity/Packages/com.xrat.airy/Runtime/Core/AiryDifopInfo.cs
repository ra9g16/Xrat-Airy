using System;
using System.Buffers.Binary;
using System.Text;

namespace Xrat.Airy
{
    /// <summary>Device information decoded from a DIFOP packet (manual 4.4.3 and Appendix A).</summary>
    public sealed class AiryDifopInfo
    {
        public int MotorSpeedRpm;
        public string LidarAddress;
        public string DestinationAddress;
        public string MacAddress;
        public int MsopPort;
        public int DifopPort;
        public string MainboardFirmware;
        public string BaseboardFirmware;
        public string AppFirmware;
        public string MotorFirmware;
        public string SerialNumber;
        public AiryReturnMode ReturnMode;
        public AiryTimeSyncMode TimeSyncMode;
        public bool TimeSynchronized;
        public double DeviceTime;
        public float MainboardVoltage;
        public float MachineVoltage;
        public float BottomBoardVoltage;
        public float MainboardEmitTemperature;

        // Appendix A.14 lists the fields (q_x, q_y, q_z, q_w, x, y, z) but not their encoding, so the raw bytes are kept.
        public byte[] ImuCalibrationRaw;

        public static bool TryParse(ReadOnlySpan<byte> data, out AiryDifopInfo info)
        {
            info = null;
            if (!AiryProtocol.IsDifop(data))
                return false;

            info = new AiryDifopInfo
            {
                MotorSpeedRpm = U16(data, AiryProtocol.DifopMotorSpeedOffset),
                LidarAddress = Ip(data, AiryProtocol.DifopLidarIpOffset),
                DestinationAddress = Ip(data, AiryProtocol.DifopDestinationIpOffset),
                MacAddress = Hex(data.Slice(AiryProtocol.DifopMacOffset, 6), ":"),
                MsopPort = U16(data, AiryProtocol.DifopMsopPortOffset),
                DifopPort = U16(data, AiryProtocol.DifopDifopPortOffset),
                MainboardFirmware = Hex(data.Slice(AiryProtocol.DifopMainboardFirmwareOffset, 5), " "),
                BaseboardFirmware = Hex(data.Slice(AiryProtocol.DifopBaseboardFirmwareOffset, 5), " "),
                AppFirmware = Hex(data.Slice(AiryProtocol.DifopAppFirmwareOffset, 5), " "),
                MotorFirmware = Hex(data.Slice(AiryProtocol.DifopMotorFirmwareOffset, 5), " "),
                SerialNumber = Hex(data.Slice(AiryProtocol.DifopSerialNumberOffset, 6), ""),
                ReturnMode = ToReturnMode(data[AiryProtocol.DifopReturnModeOffset]),
                TimeSyncMode = ToTimeSyncMode(data[AiryProtocol.DifopTimeSyncModeOffset]),
                TimeSynchronized = data[AiryProtocol.DifopTimeSyncStatusOffset] == 0x01,
                DeviceTime = AiryTimestamp.Read(data.Slice(AiryProtocol.DifopTimeOffset, 10)),
                MainboardVoltage = U16(data, AiryProtocol.DifopMainboardVoltageOffset) / 100f,
                MachineVoltage = U16(data, AiryProtocol.DifopMachineVoltageOffset) / 100f,
                BottomBoardVoltage = U16(data, AiryProtocol.DifopBottomBoardVoltageOffset) / 100f,
                MainboardEmitTemperature = BinaryPrimitives.ReadInt16BigEndian(data.Slice(AiryProtocol.DifopMainboardTemperatureOffset)) / 100f,
                ImuCalibrationRaw = data.Length >= AiryProtocol.DifopImuCalibrationOffset + AiryProtocol.DifopImuCalibrationSize
                    ? data.Slice(AiryProtocol.DifopImuCalibrationOffset, AiryProtocol.DifopImuCalibrationSize).ToArray()
                    : Array.Empty<byte>(),
            };
            return true;
        }

        public static AiryReturnMode ToReturnMode(byte value)
        {
            switch (value)
            {
                case 0x00: return AiryReturnMode.Dual;
                case 0x04: return AiryReturnMode.Strongest;
                case 0x05: return AiryReturnMode.Last;
                case 0x06: return AiryReturnMode.First;
                default: return AiryReturnMode.Unknown;
            }
        }

        public static AiryTimeSyncMode ToTimeSyncMode(byte value)
        {
            return value <= 0x04 ? (AiryTimeSyncMode)value : AiryTimeSyncMode.Unknown;
        }

        public override string ToString()
        {
            return $"Airy SN {SerialNumber} @ {LidarAddress} -> {DestinationAddress} (MSOP {MsopPort}, DIFOP {DifopPort}), "
                + $"{MotorSpeedRpm} rpm, {ReturnMode} return, sync {TimeSyncMode} ({(TimeSynchronized ? "locked" : "not locked")}), "
                + $"{MachineVoltage:0.00} V, {MainboardEmitTemperature:0.0} C, firmware app {AppFirmware}";
        }

        static int U16(ReadOnlySpan<byte> data, int offset)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        }

        static string Ip(ReadOnlySpan<byte> data, int offset)
        {
            return $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";
        }

        static string Hex(ReadOnlySpan<byte> bytes, string separator)
        {
            var sb = new StringBuilder(bytes.Length * (2 + separator.Length));
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                    sb.Append(separator);
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
