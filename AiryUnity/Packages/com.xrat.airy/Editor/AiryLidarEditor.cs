using UnityEditor;
using UnityEngine;

namespace Xrat.Airy.Editor
{
    [CustomEditor(typeof(AiryLidar))]
    public sealed class AiryLidarEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var lidar = (AiryLidar)target;

            DrawDefaultInspector();
            bool changed = false;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (lidar.source == AiryLidar.SourceMode.PcapFile && GUILayout.Button("Browse pcap…"))
                {
                    string path = EditorUtility.OpenFilePanel("Airy capture", "", "pcap");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Undo.RecordObject(lidar, "Select pcap");
                        lidar.pcapPath = path;
                        changed = true;
                    }
                }

                string address = lidar.Difop?.LidarAddress ?? AiryProtocol.DefaultLidarAddress;
                if (GUILayout.Button($"Open Web UI ({address})"))
                    Application.OpenURL($"http://{address}");
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to start receiving. Live UDP needs this machine on 192.168.1.x/24 "
                    + "(factory host IP 192.168.1.102) and inbound UDP 6699/7788 allowed for the Unity Editor.",
                    MessageType.Info);
                return;
            }

            // Settings are read when the source starts; apply edits explicitly so typing a path doesn't restart per keystroke.
            if (changed || GUILayout.Button("Apply settings (restart source)"))
                lidar.Restart();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(AiryStatusText.Build(lidar, rich: false),
                string.IsNullOrEmpty(lidar.LastError) ? MessageType.None : MessageType.Warning);
        }
    }
}
