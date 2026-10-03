using System;

namespace Xrat.Airy
{
    public static class AiryTimestamp
    {
        // 10-byte big-endian timestamp: 6 bytes of seconds followed by a 4-byte sub-second field.
        // Table 9 calls the sub-second field nanoseconds while its note and Appendix A.9 say
        // microseconds (0..999,999). Values above 999,999 can only be nanoseconds, so both are accepted.
        public static double Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 10)
                throw new ArgumentException("Timestamp needs 10 bytes.", nameof(data));

            ulong seconds = 0;
            for (int i = 0; i < 6; i++)
                seconds = (seconds << 8) | data[i];

            uint fraction = (uint)(data[6] << 24 | data[7] << 16 | data[8] << 8 | data[9]);
            double subSeconds = fraction < 1_000_000 ? fraction * 1e-6 : fraction * 1e-9;
            return seconds + subSeconds;
        }

        public static void Write(Span<byte> destination, double timestamp)
        {
            if (destination.Length < 10)
                throw new ArgumentException("Timestamp needs 10 bytes.", nameof(destination));

            ulong seconds = (ulong)Math.Floor(timestamp);
            uint micros = (uint)Math.Min(999_999, Math.Round((timestamp - seconds) * 1e6));
            for (int i = 5; i >= 0; i--)
            {
                destination[i] = (byte)seconds;
                seconds >>= 8;
            }
            destination[6] = (byte)(micros >> 24);
            destination[7] = (byte)(micros >> 16);
            destination[8] = (byte)(micros >> 8);
            destination[9] = (byte)micros;
        }

        public static DateTime ToUtcDateTime(double timestamp)
        {
            return DateTime.UnixEpoch.AddTicks((long)(timestamp * TimeSpan.TicksPerSecond));
        }
    }
}
