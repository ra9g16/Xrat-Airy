using UnityEditor;
using UnityEngine;

namespace Xrat.Airy.Editor
{
    static class AiryMenu
    {
        [MenuItem("GameObject/Xrat/Airy LiDAR", false, 10)]
        static void CreateAiry(MenuCommand command)
        {
            var go = new GameObject("Airy LiDAR");
            go.AddComponent<AiryLidar>();
            go.AddComponent<AiryPointCloudRenderer>();
            go.AddComponent<AiryStatusHud>().lidar = go.GetComponent<AiryLidar>();

            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Airy LiDAR");
            Selection.activeObject = go;
        }

        [MenuItem("Tools/Xrat Airy/Open Airy Web UI (192.168.1.200)")]
        static void OpenWebUi()
        {
            Application.OpenURL($"http://{AiryProtocol.DefaultLidarAddress}");
        }

        [MenuItem("Tools/Xrat Airy/Enable Run In Background")]
        static void EnableRunInBackground()
        {
            PlayerSettings.runInBackground = true;
            Debug.Log("[Airy] Player Settings > Resolution and Presentation > Run In Background enabled.");
        }
    }
}
