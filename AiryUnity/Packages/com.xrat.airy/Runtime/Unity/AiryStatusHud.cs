using UnityEngine;

namespace Xrat.Airy
{
    /// <summary>Small Game-view overlay with stream health and DIFOP device info, for bring-up and debugging.</summary>
    [AddComponentMenu("Xrat/Airy Status HUD")]
    public sealed class AiryStatusHud : MonoBehaviour
    {
        public AiryLidar lidar;
        public Vector2 position = new Vector2(10f, 10f);
        public int fontSize = 13;

        GUIStyle _style;

        void Reset()
        {
            lidar = GetComponent<AiryLidar>();
        }

        void OnGUI()
        {
            if (lidar == null)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = fontSize,
                    richText = true,
                    padding = new RectOffset(8, 8, 6, 6),
                };
            }

            string text = AiryStatusText.Build(lidar, rich: true);
            Vector2 size = _style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(position.x, position.y, size.x, size.y), text, _style);
        }
    }

    public static class AiryStatusText
    {
        public static string Build(AiryLidar lidar, bool rich)
        {
            var sb = new System.Text.StringBuilder(512);
            string warn = rich ? "<color=#ffb300>" : "";
            string end = rich ? "</color>" : "";

            sb.AppendLine($"Airy {lidar.source}  {(lidar.IsRunning ? "running" : "stopped")}");
            if (!string.IsNullOrEmpty(lidar.LastError))
                sb.AppendLine($"{warn}Error: {lidar.LastError}{end}");

            AiryDecoderStats stats = lidar.Stats;
            sb.AppendLine($"MSOP {lidar.PacketsPerSecond:0} pkt/s   frames {lidar.FramesPerSecond:0.0} Hz   points {lidar.PointCount:N0}");
            sb.Append($"packets {stats.MsopPackets:N0}  lost {stats.LostPackets:N0}  DIFOP {stats.DifopPackets:N0}");
            if (stats.RejectedPackets > 0)
                sb.Append($"  rejected {stats.RejectedPackets:N0}");
            sb.AppendLine();
            if (stats.WrongModelPackets > 0)
                sb.AppendLine($"{warn}{stats.WrongModelPackets:N0} packets are not Airy/96-beam (type 0x10, model 0x02){end}");

            AiryDifopInfo d = lidar.Difop;
            if (d == null)
            {
                if (lidar.source == AiryLidar.SourceMode.LiveUdp && stats.MsopPackets == 0)
                    sb.AppendLine($"{warn}No data yet: check host IP {AiryProtocol.DefaultHostAddress}/24, cable/power and firewall (UDP {lidar.msopPort}/{lidar.difopPort}){end}");
                else
                    sb.AppendLine("No DIFOP yet");
            }
            else
            {
                sb.AppendLine($"SN {d.SerialNumber}  {d.LidarAddress} -> {d.DestinationAddress}  MAC {d.MacAddress}");
                sb.AppendLine($"{d.MotorSpeedRpm} rpm  {d.ReturnMode} return  sync {d.TimeSyncMode} {(d.TimeSynchronized ? "locked" : "free-running")}");
                sb.AppendLine($"input {d.MachineVoltage:0.00} V  mainboard {d.MainboardVoltage:0.00} V  temp {d.MainboardEmitTemperature:0.0} C");
                sb.Append($"firmware app {d.AppFirmware}  main {d.MainboardFirmware}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
