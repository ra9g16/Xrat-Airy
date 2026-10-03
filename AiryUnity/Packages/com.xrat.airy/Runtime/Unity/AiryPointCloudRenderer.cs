using UnityEngine;
using UnityEngine.Rendering;

namespace Xrat.Airy
{
    /// <summary>
    /// Draws the latest <see cref="AiryLidar"/> frame as screen-space point sprites with one procedural draw call.
    /// Works with the Built-in Render Pipeline and URP (Graphics.RenderPrimitives + an unlit shader).
    /// </summary>
    [AddComponentMenu("Xrat/Airy Point Cloud Renderer")]
    [RequireComponent(typeof(AiryLidar))]
    public sealed class AiryPointCloudRenderer : MonoBehaviour
    {
        public enum ColorMode
        {
            Intensity = 0,
            Height = 1,
            Distance = 2,
            Channel = 3,
            Solid = 4,
        }

        const string ShaderName = "Xrat/Airy/PointCloud";

        public ColorMode colorMode = ColorMode.Intensity;
        public Color solidColor = Color.white;

        [Range(1f, 16f)]
        [Tooltip("Point diameter in pixels.")]
        public float pointSize = 3f;

        public bool roundPoints = true;

        [Tooltip("Reflectivity mapped onto the colour ramp (calibrated range is 1..255).")]
        public Vector2 intensityRange = new Vector2(0f, 150f);

        [Tooltip("World-space height (m) mapped onto the colour ramp.")]
        public Vector2 heightRange = new Vector2(-0.5f, 3f);

        [Tooltip("Distance from the sensor (m) mapped onto the colour ramp.")]
        public Vector2 distanceRange = new Vector2(0f, 20f);

        [Tooltip("Leave empty to use the package's " + ShaderName + " shader.")]
        public Shader shader;

        static readonly int PointsId = Shader.PropertyToID("_Points");
        static readonly int LocalToWorldId = Shader.PropertyToID("_LocalToWorld");
        static readonly int PointSizeId = Shader.PropertyToID("_PointSize");
        static readonly int ColorModeId = Shader.PropertyToID("_ColorMode");
        static readonly int SolidColorId = Shader.PropertyToID("_SolidColor");
        static readonly int RangesId = Shader.PropertyToID("_Ranges");
        static readonly int IntensityRangeId = Shader.PropertyToID("_IntensityRange");
        static readonly int RoundPointsId = Shader.PropertyToID("_RoundPoints");

        AiryLidar _lidar;
        Material _material;
        MaterialPropertyBlock _properties;
        GraphicsBuffer _buffer;
        Vector4[] _upload = new Vector4[0];
        int _count;

        public int RenderedPointCount => _count;

        void OnEnable()
        {
            _lidar = GetComponent<AiryLidar>();
            _lidar.FrameReceived += OnFrame;
            _properties = new MaterialPropertyBlock();

            Shader s = shader != null ? shader : Shader.Find(ShaderName);
            if (s == null)
            {
                Debug.LogError($"[Airy] Shader '{ShaderName}' not found.", this);
                enabled = false;
                return;
            }
            _material = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
        }

        void OnDisable()
        {
            if (_lidar != null)
                _lidar.FrameReceived -= OnFrame;
            _buffer?.Release();
            _buffer = null;
            _count = 0;
            if (_material != null)
                Destroy(_material);
            _material = null;
        }

        void OnFrame(AiryLidar lidar)
        {
            int count = lidar.PointCount;
            AiryPoint[] points = lidar.Points;

            if (_upload.Length < count)
                _upload = new Vector4[Mathf.NextPowerOfTwo(count)];

            // xyz: Unity local space. w packs intensity (low byte) and channel (high byte); exact in a float.
            for (int i = 0; i < count; i++)
            {
                ref readonly AiryPoint p = ref points[i];
                _upload[i] = new Vector4(-p.Y, p.Z, p.X, p.Intensity + p.Channel * 256f);
            }

            if (_buffer == null || _buffer.count < count)
            {
                _buffer?.Release();
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1024, _upload.Length), sizeof(float) * 4);
            }
            _buffer.SetData(_upload, 0, 0, count);
            _count = count;
        }

        void LateUpdate()
        {
            if (_count == 0 || _material == null || _buffer == null)
                return;

            _properties.SetBuffer(PointsId, _buffer);
            _properties.SetMatrix(LocalToWorldId, transform.localToWorldMatrix);
            _properties.SetFloat(PointSizeId, pointSize);
            _properties.SetFloat(ColorModeId, (int)colorMode);
            _properties.SetColor(SolidColorId, solidColor);
            _properties.SetVector(RangesId, new Vector4(heightRange.x, heightRange.y, distanceRange.x, distanceRange.y));
            _properties.SetVector(IntensityRangeId, new Vector4(intensityRange.x, intensityRange.y, 0f, 0f));
            _properties.SetFloat(RoundPointsId, roundPoints ? 1f : 0f);

            float extent = Mathf.Max(1f, _lidar.maxRange) * 2f * Mathf.Max(1f, transform.lossyScale.magnitude);
            var renderParams = new RenderParams(_material)
            {
                worldBounds = new Bounds(transform.position, Vector3.one * extent),
                matProps = _properties,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, _count * 6);
        }
    }
}
