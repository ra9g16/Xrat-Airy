// Constants for the RoboSense Airy UDP protocols (MSOP / DIFOP).
// Every value here comes from the Airy User Guide (v1.0); section numbers are noted inline.

namespace Xrat.Airy
{
    public static class AiryProtocol
    {
        // 4.4 Table 8: both protocols use 1248-byte UDP payloads.
        public const int PacketSize = 1248;

        // 3.4 Table 7: factory network configuration.
        public const string DefaultLidarAddress = "192.168.1.200";
        public const string DefaultHostAddress = "192.168.1.102";
        public const int DefaultMsopPort = 6699;
        public const int DefaultDifopPort = 7788;

        // 2.3 / 2.4: 96 channels spread uniformly over 0..90 deg of elevation.
        public const int ChannelCount = 96;
        public const float NominalVerticalStepDegrees = 90f / (ChannelCount - 1);

        // 4.4.2 MSOP layout: 42-byte header, 8 x 148-byte blocks, 22-byte tail.
        public const int MsopHeaderSize = 42;
        public const int BlocksPerPacket = 8;
        public const int ChannelsPerBlock = 48;
        public const int ChannelDataSize = 3;
        public const int BlockSize = 2 + 2 + ChannelsPerBlock * ChannelDataSize; // flag + azimuth + channels
        public const int MsopTailOffset = MsopHeaderSize + BlocksPerPacket * BlockSize; // 1226

        public const int MsopPacketCountOffset = 12;
        public const int MsopTimestampOffset = 20;
        public const int MsopLidarTypeOffset = 31;
        public const int MsopLidarModelOffset = 32;

        public const byte LidarTypeAiry = 0x10;
        public const byte LidarModel96Beams = 0x02;

        public const byte BlockFlag0 = 0xFF;
        public const byte BlockFlag1 = 0xEE;

        // Table 11 / 4.4.2.2: distance LSB = 0.5 cm, azimuth LSB = 0.01 deg.
        public const float DistanceResolution = 0.005f;
        public const float AzimuthResolution = 0.01f;
        public const int AzimuthUnitsPerRevolution = 36000;

        // Appendix B: optical centre sits 45.34 mm above the mounting face (the coordinate origin, 4.1).
        public const float OpticalCenterHeight = 0.04534f;

        // 2.4: blind zone 0.1 m, range 100 m @ NIST 10%.
        public const float MinimumRange = 0.1f;
        public const float MaximumRange = 100f;

        // 4.4.3 Table 12 DIFOP offsets.
        public const int DifopMotorSpeedOffset = 8;
        public const int DifopLidarIpOffset = 10;
        public const int DifopDestinationIpOffset = 14;
        public const int DifopMacOffset = 18;
        public const int DifopMsopPortOffset = 24;
        public const int DifopDifopPortOffset = 28;
        public const int DifopMainboardFirmwareOffset = 40;
        public const int DifopBaseboardFirmwareOffset = 45;
        public const int DifopAppFirmwareOffset = 50;
        public const int DifopMotorFirmwareOffset = 55;
        public const int DifopSerialNumberOffset = 292;
        public const int DifopReturnModeOffset = 300;
        public const int DifopTimeSyncModeOffset = 301;
        public const int DifopTimeSyncStatusOffset = 302;
        public const int DifopTimeOffset = 303;
        public const int DifopMainboardVoltageOffset = 1044;
        public const int DifopMachineVoltageOffset = 1066;
        public const int DifopBottomBoardVoltageOffset = 1068;
        public const int DifopMainboardTemperatureOffset = 1080;
        public const int DifopImuCalibrationOffset = 1092;
        public const int DifopImuCalibrationSize = 28; // q_x, q_y, q_z, q_w, x, y, z (Table 27)
        public const int DifopTailOffset = 1246;

        public static bool IsMsop(System.ReadOnlySpan<byte> data)
        {
            return data.Length >= MsopHeaderSize
                && data[0] == 0x55 && data[1] == 0xAA && data[2] == 0x05 && data[3] == 0x5A;
        }

        public static bool IsDifop(System.ReadOnlySpan<byte> data)
        {
            return data.Length >= DifopTailOffset
                && data[0] == 0xA5 && data[1] == 0xFF && data[2] == 0x00 && data[3] == 0x5A
                && data[4] == 0x11 && data[5] == 0x11 && data[6] == 0x55 && data[7] == 0x55;
        }
    }

    // 2.5.3.2 Table 2 / DIFOP byte 300.
    public enum AiryReturnMode : byte
    {
        Dual = 0x00,
        Strongest = 0x04,
        Last = 0x05,
        First = 0x06,
        Unknown = 0xFF,
    }

    // Appendix A.8.
    public enum AiryTimeSyncMode : byte
    {
        Gps = 0x00,
        PtpE2EL4 = 0x01,
        PtpP2P = 0x02,
        Gptp = 0x03,
        PtpE2EL2 = 0x04,
        Unknown = 0xFF,
    }
}
