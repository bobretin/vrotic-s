using UnityEngine;
using UnityEngine.Rendering;

namespace MultiBar.Liquids
{
    /// <summary>Shows the incoming colour on the surface, with mixing spirals and impact ripples.</summary>
    [DisallowMultipleComponent]
    public sealed class LiquidMixingVisual : MonoBehaviour
    {
        private const int SpiralCount = 2;
        private const int RippleCount = 3;

        [SerializeField, Range(16, 48)] private int segments = 28;
        [SerializeField, Range(24, 64)] private int rippleSegments = 40;
        [SerializeField, Min(0.001f)] private float lineWidth = 0.008f;
        [SerializeField, Min(0.001f)] private float rippleWidth = 0.005f;
        [SerializeField, Min(0.2f)] private float rippleDuration = 0.9f;

        private LiquidContainer owner;
        private LineRenderer[] lines;
        private LineRenderer[] ripples;
        private MaterialPropertyBlock[] rippleProperties;
        private Material runtimeMaterial;
        private GameObject surfaceObject;
        private Mesh surfaceMesh;
        private MeshRenderer surfaceRenderer;
        private Material surfaceMaterial;
        private Vector3[] surfaceVertices;
        private float animationTime;

        public void Configure(LiquidContainer container)
        {
            owner = container;
            EnsureVisuals();
        }

        private void EnsureVisuals()
        {
            if (lines != null && ripples != null) return;

            if (runtimeMaterial == null)
                runtimeMaterial = CreateTransparentMaterial("LM_Mixing_Runtime_" + GetInstanceID());

            if (lines == null)
            {
                lines = new LineRenderer[SpiralCount];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = CreateLine("MixingSwirl_" + i, segments, lineWidth, lineWidth * 0.25f);
            }

            if (ripples == null)
            {
                ripples = new LineRenderer[RippleCount];
                rippleProperties = new MaterialPropertyBlock[RippleCount];
                for (int i = 0; i < ripples.Length; i++)
                {
                    ripples[i] = CreateLine("ImpactRipple_" + i, rippleSegments + 1, rippleWidth, rippleWidth);
                    rippleProperties[i] = new MaterialPropertyBlock();
                }
            }

            EnsureSurfaceLayer();
        }

        private LineRenderer CreateLine(string itemName, int pointCount, float startWidth, float endWidth)
        {
            GameObject item = new GameObject(itemName);
            item.transform.SetParent(transform, false);
            LineRenderer line = item.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = pointCount;
            line.startWidth = startWidth;
            line.endWidth = endWidth;
            line.numCornerVertices = 3;
            line.numCapVertices = 2;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = Color.white;
            line.endColor = Color.white;
            if (runtimeMaterial != null) line.sharedMaterial = runtimeMaterial;
            line.enabled = false;
            return line;
        }

        private void EnsureSurfaceLayer()
        {
            if (surfaceObject != null) return;

            surfaceObject = new GameObject("IncomingSurfaceLayer_" + GetInstanceID());
            surfaceObject.hideFlags = HideFlags.DontSave;
            MeshFilter filter = surfaceObject.AddComponent<MeshFilter>();
            surfaceRenderer = surfaceObject.AddComponent<MeshRenderer>();
            surfaceRenderer.shadowCastingMode = ShadowCastingMode.Off;
            surfaceRenderer.receiveShadows = false;

            surfaceMaterial = CreateTransparentMaterial("LM_IncomingSurface_" + GetInstanceID());
            if (surfaceMaterial != null) surfaceRenderer.sharedMaterial = surfaceMaterial;

            int ringSegments = Mathf.Clamp(rippleSegments, 24, 64);
            surfaceVertices = new Vector3[ringSegments + 1];
            int[] triangles = new int[ringSegments * 3];
            for (int i = 0; i < ringSegments; i++)
            {
                int triangle = i * 3;
                triangles[triangle] = 0;
                triangles[triangle + 1] = (i + 1) % ringSegments + 1;
                triangles[triangle + 2] = i + 1;
            }

            surfaceMesh = new Mesh { name = "LM_IncomingSurfaceMesh_" + GetInstanceID() };
            surfaceMesh.MarkDynamic();
            surfaceMesh.SetVertices(surfaceVertices);
            surfaceMesh.SetTriangles(triangles, 0);
            surfaceMesh.RecalculateNormals();
            surfaceMesh.RecalculateBounds();
            filter.sharedMesh = surfaceMesh;
            surfaceRenderer.enabled = false;
        }

        private static Material CreateTransparentMaterial(string materialName)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            Material material = new Material(shader) { name = materialName };
            ConfigureTransparency(material);
            return material;
        }

        private static void ConfigureTransparency(Material material)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private void Update()
        {
            if (owner == null) owner = GetComponentInParent<LiquidContainer>();
            EnsureVisuals();
            if (owner == null) return;

            animationTime += Mathf.Max(0f, Time.deltaTime);
            LiquidVolumeMesh mesh = owner.VolumeMesh;
            float strength = Mathf.Clamp01(owner.MixingStrength);
            bool visible = mesh != null && mesh.DisplayedFill01 > 0.001f && strength > 0.01f;
            for (int i = 0; i < lines.Length; i++) lines[i].enabled = visible;
            for (int i = 0; i < ripples.Length; i++) ripples[i].enabled = visible;
            if (surfaceRenderer != null) surfaceRenderer.enabled = visible;
            if (!visible) return;

            Color incomingColor = owner.IncomingMixtureColor;
            Color tint = incomingColor;
            tint.a = strength * 0.85f;
            SetMaterialColor(runtimeMaterial, tint);

            Vector3 center = mesh.GetSurfaceCenterWorld() + Vector3.up * 0.004f;
            UpdateSurfaceLayer(center, mesh.SurfaceRadiusWorld, incomingColor, strength);
            UpdateSwirls(center, mesh.SurfaceRadiusWorld, strength);
            UpdateRipples(center, mesh.SurfaceRadiusWorld, tint);
        }

        private void UpdateSurfaceLayer(Vector3 center, float surfaceRadius, Color incomingColor, float strength)
        {
            if (surfaceObject == null || surfaceMesh == null) return;

            surfaceObject.transform.position = center + Vector3.up * 0.001f;
            surfaceObject.transform.rotation = Quaternion.identity;
            float radius = surfaceRadius * 0.93f;
            int ringSegments = surfaceVertices.Length - 1;
            surfaceVertices[0] = Vector3.zero;
            for (int i = 0; i < ringSegments; i++)
            {
                float angle = i / (float)ringSegments * Mathf.PI * 2f;
                surfaceVertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            }
            surfaceMesh.SetVertices(surfaceVertices);
            surfaceMesh.RecalculateNormals();
            surfaceMesh.RecalculateBounds();

            Color surfaceColor = incomingColor;
            surfaceColor.a = Mathf.Clamp01(strength * 2.2f) * 0.82f;
            SetMaterialColor(surfaceMaterial, surfaceColor);
        }

        private void UpdateSwirls(Vector3 center, float surfaceRadius, float strength)
        {
            float radius = surfaceRadius * Mathf.Lerp(1.0f, 0.4f, strength);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                LineRenderer line = lines[lineIndex];
                line.positionCount = segments;
                line.startWidth = lineWidth;
                line.endWidth = lineWidth * 0.25f;
                for (int i = 0; i < segments; i++)
                {
                    float t = i / (float)(segments - 1);
                    float direction = lineIndex == 0 ? 1f : -1f;
                    float angle = direction * (t * Mathf.PI * 3.2f + animationTime * 3f) + lineIndex * Mathf.PI;
                    float pointRadius = radius * (0.12f + t * 0.78f);
                    Vector3 point = center + Vector3.right * (Mathf.Cos(angle) * pointRadius) +
                        Vector3.forward * (Mathf.Sin(angle) * pointRadius);
                    line.SetPosition(i, point);
                }
            }
        }

        private void UpdateRipples(Vector3 center, float surfaceRadius, Color tint)
        {
            int pointCount = Mathf.Clamp(rippleSegments, 24, 64) + 1;
            for (int rippleIndex = 0; rippleIndex < ripples.Length; rippleIndex++)
            {
                float phase = Mathf.Repeat(animationTime / rippleDuration + rippleIndex / (float)RippleCount, 1f);
                float radius = surfaceRadius * Mathf.Lerp(0.08f, 0.94f, phase);
                float fade = (1f - phase) * (1f - phase);
                Color color = tint;
                color.a = tint.a * fade * 0.7f;

                LineRenderer ripple = ripples[rippleIndex];
                ripple.positionCount = pointCount;
                ripple.startWidth = rippleWidth * Mathf.Lerp(1f, 0.25f, phase);
                ripple.endWidth = ripple.startWidth;
                for (int i = 0; i < pointCount; i++)
                {
                    float angle = i / (float)(pointCount - 1) * Mathf.PI * 2f;
                    ripple.SetPosition(i, center + Vector3.right * (Mathf.Cos(angle) * radius) +
                        Vector3.forward * (Mathf.Sin(angle) * radius));
                }

                MaterialPropertyBlock properties = rippleProperties[rippleIndex];
                properties.Clear();
                properties.SetColor("_BaseColor", color);
                properties.SetColor("_Color", color);
                ripple.SetPropertyBlock(properties);
            }
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        private void OnDisable()
        {
            if (lines != null)
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i] != null) lines[i].enabled = false;
            if (ripples != null)
                for (int i = 0; i < ripples.Length; i++)
                    if (ripples[i] != null) ripples[i].enabled = false;
            if (surfaceRenderer != null) surfaceRenderer.enabled = false;
        }

        private void OnDestroy()
        {
            DestroyRuntimeObject(surfaceObject);
            DestroyRuntimeObject(surfaceMesh);
            DestroyRuntimeObject(surfaceMaterial);
            DestroyRuntimeObject(runtimeMaterial);
        }

        private static void DestroyRuntimeObject(Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) Destroy(item);
            else DestroyImmediate(item);
        }
    }
}
