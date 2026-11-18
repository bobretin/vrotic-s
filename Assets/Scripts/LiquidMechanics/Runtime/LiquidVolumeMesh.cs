using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>Closed cylindrical liquid volume for upright vessels.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class LiquidVolumeMesh : MonoBehaviour
    {
        [SerializeField] private Transform body;
        [SerializeField, Range(16, 96)] private int radialSegments = 48;
        [SerializeField, Range(0.65f, 0.95f)] private float radiusFactor = 0.82f;
        [SerializeField, Min(0.005f)] private float bottomMargin = 0.025f;
        [SerializeField, Min(0.005f)] private float topMargin = 0.045f;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private float fill01;

        public void Configure(Transform vesselBody, float radiusScale = 0.82f)
        {
            body = vesselBody;
            radiusFactor = Mathf.Clamp(radiusScale, 0.65f, 0.95f);
            EnsureComponents();
            Rebuild();
        }

        public void SetFill(float value)
        {
            fill01 = Mathf.Clamp01(value);
            EnsureComponents();
            Rebuild();
        }

        private void OnEnable()
        {
            if (body == null && transform.parent != null)
                body = transform.parent.Find("Body");
            EnsureComponents();
            Rebuild();
        }

        private void OnValidate()
        {
            radialSegments = Mathf.Clamp(radialSegments, 16, 96);
            if (isActiveAndEnabled)
            {
                EnsureComponents();
                Rebuild();
            }
        }

        private void EnsureComponents()
        {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

            if (generatedMesh == null && meshFilter != null && meshFilter.sharedMesh != null &&
                meshFilter.sharedMesh.name == "LiquidVolume_Runtime")
                generatedMesh = meshFilter.sharedMesh;

            if (generatedMesh == null)
            {
                generatedMesh = new Mesh { name = "LiquidVolume_Runtime" };
                generatedMesh.MarkDynamic();
            }

            if (meshFilter != null && meshFilter.sharedMesh != generatedMesh)
                meshFilter.sharedMesh = generatedMesh;
        }

        private void Rebuild()
        {
            if (meshFilter == null || meshRenderer == null) return;

            // Empty containers must not show a leftover flat disc in the Scene view.
            if (body == null || fill01 <= 0.001f)
            {
                if (generatedMesh != null) generatedMesh.Clear();
                // An empty mesh renders nothing. Keep the Renderer enabled: changing
                // Renderer.enabled from OnValidate can trigger OnBecameInvisible while
                // Unity is validating the object, producing SendMessage warnings.
                meshRenderer.enabled = true;
                return;
            }

            Vector3 bodyScale = body.localScale;
            float radius = Mathf.Min(Mathf.Abs(bodyScale.x), Mathf.Abs(bodyScale.z)) * 0.5f * radiusFactor;
            float bottom = -Mathf.Abs(bodyScale.y) + bottomMargin;
            float top = Mathf.Abs(bodyScale.y) - topMargin;
            float fullHeight = Mathf.Max(0.01f, top - bottom);
            float liquidTop = bottom + fullHeight * fill01;

            int segments = Mathf.Clamp(radialSegments, 16, 96);
            int sideCount = (segments + 1) * 2;
            int capStart = sideCount;
            int topRing = capStart;
            int bottomRing = topRing + segments + 1;
            int topCenter = bottomRing + segments + 1;
            int bottomCenter = topCenter + 1;
            int vertexCount = bottomCenter + 1;

            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];
            int[] triangles = new int[segments * 12];

            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                int b = i * 2;
                vertices[b] = new Vector3(x, bottom, z);
                vertices[b + 1] = new Vector3(x, liquidTop, z);
                uv[b] = new Vector2(i / (float)segments, 0f);
                uv[b + 1] = new Vector2(i / (float)segments, 1f);
                vertices[topRing + i] = new Vector3(x, liquidTop, z);
                vertices[bottomRing + i] = new Vector3(x, bottom, z);
                uv[topRing + i] = uv[bottomRing + i] = new Vector2(0.5f + x / (2f * radius), 0.5f + z / (2f * radius));
            }

            vertices[topCenter] = new Vector3(0f, liquidTop, 0f);
            vertices[bottomCenter] = new Vector3(0f, bottom, 0f);
            uv[topCenter] = uv[bottomCenter] = new Vector2(0.5f, 0.5f);

            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                int b = a + 2;
                triangles[t++] = a; triangles[t++] = a + 1; triangles[t++] = b + 1;
                triangles[t++] = a; triangles[t++] = b + 1; triangles[t++] = b;

                int t0 = topRing + i;
                int t1 = t0 + 1;
                // Top cap must face +Y. The previous winding faced -Y, so
                // back-face culling made the liquid surface disappear when viewed above.
                triangles[t++] = topCenter; triangles[t++] = t1; triangles[t++] = t0;

                int b0 = bottomRing + i;
                int b1 = b0 + 1;
                // Bottom cap faces -Y.
                triangles[t++] = bottomCenter; triangles[t++] = b0; triangles[t++] = b1;
            }

            generatedMesh.Clear();
            generatedMesh.vertices = vertices;
            generatedMesh.uv = uv;
            generatedMesh.triangles = triangles;
            generatedMesh.RecalculateNormals();
            generatedMesh.RecalculateBounds();
            meshFilter.sharedMesh = generatedMesh;
            meshRenderer.enabled = true;
        }

        private void OnDestroy()
        {
            if (generatedMesh == null) return;
            if (Application.isPlaying) Destroy(generatedMesh);
            else DestroyImmediate(generatedMesh);
        }
    }
}
