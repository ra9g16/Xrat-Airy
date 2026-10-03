using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Xrat.Airy
{
    public enum AiryChannelOrder
    {
        // Channel 1 fires at 0 deg (horizon), channel 96 at 90 deg (zenith).
        Ascending,
        // Channel 1 fires at 90 deg (zenith), channel 96 at 0 deg (horizon).
        Descending,
    }

    /// <summary>
    /// Per-channel vertical angle and horizontal offset, in degrees.
    /// The manual only states that the 96 beams are spread uniformly over 0..90 deg (2.3), so the
    /// nominal table is an approximation. For accurate geometry load the unit's calibration as CSV
    /// (one "vertical,horizontal" pair per channel, the same layout as rs_driver's angle.csv).
    /// </summary>
    public sealed class AiryAngleTable
    {
        public readonly float[] Vertical = new float[AiryProtocol.ChannelCount];
        public readonly float[] Horizontal = new float[AiryProtocol.ChannelCount];

        public static AiryAngleTable CreateNominal(AiryChannelOrder order = AiryChannelOrder.Ascending)
        {
            var table = new AiryAngleTable();
            for (int i = 0; i < AiryProtocol.ChannelCount; i++)
            {
                int step = order == AiryChannelOrder.Ascending ? i : AiryProtocol.ChannelCount - 1 - i;
                table.Vertical[i] = step * AiryProtocol.NominalVerticalStepDegrees;
            }
            return table;
        }

        public static AiryAngleTable ParseCsv(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var rows = new List<(float vertical, float horizontal)>();
            using (var reader = new StringReader(text))
            {
                string line;
                int lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;

                    string[] cells = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!TryParse(cells[0], out float vertical))
                    {
                        if (rows.Count == 0)
                            continue; // header row
                        throw new FormatException($"Angle CSV line {lineNumber}: '{cells[0]}' is not a number.");
                    }

                    float horizontal = 0f;
                    if (cells.Length > 1 && !TryParse(cells[1], out horizontal))
                        throw new FormatException($"Angle CSV line {lineNumber}: '{cells[1]}' is not a number.");

                    rows.Add((vertical, horizontal));
                }
            }

            if (rows.Count != AiryProtocol.ChannelCount)
                throw new FormatException($"Angle CSV must have {AiryProtocol.ChannelCount} rows, found {rows.Count}.");

            var table = new AiryAngleTable();
            for (int i = 0; i < rows.Count; i++)
            {
                table.Vertical[i] = rows[i].vertical;
                table.Horizontal[i] = rows[i].horizontal;
            }
            return table;
        }

        static bool TryParse(string cell, out float value)
        {
            return float.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
