using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>
    /// Closed cylindrical liquid volume clipped by a gravity-aligned surface.
    /// The plane offset is solved from the requested fill, so tilting the vessel
    /// moves liquid towards its lower side without changing the logical volume.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class LiquidVolumeMesh : MonoBehaviour
    {
        private const int VolumeSlices = 24;
        private const int VolumeSearchIterations = 12;
        private const float PlaneEpsilon = 0.00001f;

        [SerializeField] private Transform body;
        [SerializeField, Range(16, 96)] private int radialSegments = 48;
        [SerializeField, Range(0.65f, 0.95f)] private float radiusFactor = 0.82f;
        [SerializeField, Min(0.005f)] private float bottomMargin = 0.025f;
        [SerializeField, Min(0.005f)] private float topMargin = 0.045f;
        [SerializeField, Range(15f, 60f)] private float rebuildsPerSecond = 30f;
        [SerializeField, Min(1f)] private float surfaceResponse = 11f;
        [SerializeField, Min(1f)] private float fillResponse = 18f;
        [SerializeField, Range(0f, 8f)] private float maximumSloshAngle = 3.5f;
        [SerializeField, Min(0.1f)] private float sloshDecay = 3.5f;

        private readonly List<Vector3> baseVertices = new List<Vector3>(256);
        private readonly List<int> baseTriangles = new List<int>(768);
        private readonly List<Vector3> outputVertices = new List<Vector3>(768);
        private readonly List<int> outputTriangles = new List<int>(1152);
        private readonly List<Vector3> clipInput = new List<Vector3>(4);
        private readonly List<Vector3> clipOutput = new List<Vector3>(4);
        private readonly List<Vector3> surfacePoints = new List<Vector3>(96);
        private readonly List<Vector3> capPoints = new List<Vector3>(96);

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private float fill01;
        private float displayedFill01;
        private bool displayFillInitialized;
        private float radius;
        private float bottom;
        private float top;
        private float planeOffset;
        private Vector3 bodyCenterLocal;
        private Vector3 localSurfaceNormal = Vector3.up;
        private Vector3 renderSurfaceNormal = Vector3.up;
        private Vector3 surfaceCenterLocal;
        private float sloshEnergy;
        private float animationTime;
        private float nextRebuildTime;
        private bool dirty = true;
        private bool surfaceInitialized;
        private Matrix4x4 previousLocalToWorld;

        public float DisplayedFill01 => displayedFill01;

        public void Configure(Transform vesselBody, float radiusScale = 0.82f)
        {
            body = vesselBody;
            radiusFactor = Mathf.Clamp(radiusScale, 0.65f, 0.95f);
            displayFillInitialized = false;
            displayedFill01 = fill01;
            EnsureComponents();
            dirty = true;
            Rebuild();
        }

        public void SetFill(float value)
        {
            float nextFill = Mathf.Clamp01(value);
            bool snap = !displayFillInitialized || !Application.isPlaying || nextFill < displayedFill01;
            fill01 = nextFill;
            if (snap)
            {
                displayedFill01 = fill01;
                displayFillInitialized = true;
            }
            EnsureComponents();
            dirty = true;
            Rebuild();
        }

        public bool ContainsWorldPoint(Vector3 worldPoint)
        {
            return ContainsLocalPoint(transform.InverseTransformPoint(worldPoint), false);
        }

        /// <summary>Finds the lowered lip that water actually reaches before it spills.</summary>
        public bool TryGetWetOpening(Vector3 worldOpening, out Vector3 flowPoint)
        {
            Vector3 point = transform.InverseTransformPoint(worldOpening);
            point.y = Mathf.Clamp(point.y, bottom, top);
            Vector3 horizontal = new Vector3(renderSurfaceNormal.x, 0f, renderSurfaceNormal.z);
            if (horizontal.sqrMagnitude > 0.000001f)
            {
                Vector3 loweredLip = -horizontal.normalized * radius;
                point.x = bodyCenterLocal.x + loweredLip.x;
                point.z = bodyCenterLocal.z + loweredLip.z;
            }
            else
            {
                point.x = bodyCenterLocal.x;
                point.z = bodyCenterLocal.z;
            }

            flowPoint = transform.TransformPoint(point);
            return ContainsLocalPoint(point, false);
        }

        public bool IsOpeningWet(Vector3 worldOpening)
        {
            return TryGetWetOpening(worldOpening, out _);
        }

        public Vector3 SurfaceNormalWorld => transform.TransformDirection(renderSurfaceNormal).normalized;

        public Vector3 GetSurfaceCenterWorld()
        {
            return transform.TransformPoint(surfaceCenterLocal);
        }

        public float SurfaceRadiusWorld
        {
            get
            {
                Vector3 scale = transform.lossyScale;
                return radius * (Mathf.Abs(scale.x) + Mathf.Abs(scale.z)) * 0.5f;
            }
        }

        public void AddSlosh(float impulse)
        {
            sloshEnergy = Mathf.Clamp01(Mathf.Max(sloshEnergy, impulse));
            dirty = true;
        }

        private bool ContainsLocalPoint(Vector3 point, bool clampToOpening)
        {
            if (displayedFill01 <= 0.001f || body == null) return false;
            if (clampToOpening) point.y = Mathf.Clamp(point.y, bottom, top);
            if (point.y < bottom - PlaneEpsilon || point.y > top + PlaneEpsilon) return false;
            Vector2 radial = new Vector2(point.x - bodyCenterLocal.x, point.z - bodyCenterLocal.z);
            if (radial.sqrMagnitude > (radius + PlaneEpsilon) * (radius + PlaneEpsilon)) return false;
            return Vector3.Dot(renderSurfaceNormal, point) <= planeOffset + PlaneEpsilon;
        }

        private void OnEnable()
        {
            if (body == null && transform.parent != null)
                body = transform.parent.Find("Body");
            EnsureComponents();
            previousLocalToWorld = transform.localToWorldMatrix;
            dirty = true;
            Rebuild();
        }

        private void Update()
        {
            if (body == null || meshFilter == null) return;

            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            if (Application.isPlaying)
            {
                AdvanceDisplayedFill(deltaTime);
            }
            else if (displayedFill01 != fill01)
            {
                displayedFill01 = fill01;
                displayFillInitialized = true;
                dirty = true;
            }
            animationTime += deltaTime;
            Vector3 targetNormal = GetGravitySurfaceNormal();
            if (!surfaceInitialized)
            {
                localSurfaceNormal = targetNormal;
                surfaceInitialized = true;
                dirty = true;
            }

            float remainingAngle = Vector3.Angle(localSurfaceNormal, targetNormal);
            if (remainingAngle > 0.01f)
            {
                float blend = 1f - Mathf.Exp(-surfaceResponse * deltaTime);
                localSurfaceNormal = Vector3.Slerp(localSurfaceNormal, targetNormal, blend).normalized;
                dirty = true;
            }

            if (sloshEnergy > 0.0001f)
            {
                sloshEnergy = Mathf.Max(0f, sloshEnergy - sloshDecay * deltaTime * sloshEnergy);
                dirty = true;
            }

            Matrix4x4 currentMatrix = transform.localToWorldMatrix;
            if (currentMatrix != previousLocalToWorld)
            {
                previousLocalToWorld = currentMatrix;
                if (Application.isPlaying && remainingAngle > 0.5f)
                    AddSlosh(Mathf.Clamp01(remainingAngle / 45f) * 0.55f);
                dirty = true;
            }

            float interval = 1f / Mathf.Max(15f, rebuildsPerSecond);
            if (dirty && Time.realtimeSinceStartup >= nextRebuildTime)
            {
                nextRebuildTime = Time.realtimeSinceStartup + interval;
                Rebuild();
            }
        }

        private void AdvanceDisplayedFill(float deltaTime)
        {
            if (!displayFillInitialized || displayedFill01 > fill01)
            {
                displayedFill01 = fill01;
                displayFillInitialized = true;
                dirty = true;
                return;
            }

            float blend = 1f - Mathf.Exp(-fillResponse * Mathf.Max(0f, deltaTime));
            float nextFill = Mathf.Lerp(displayedFill01, fill01, blend);
            if (Mathf.Abs(fill01 - nextFill) < 0.0001f) nextFill = fill01;
            if (Mathf.Abs(nextFill - displayedFill01) < 0.0000001f) return;
            displayedFill01 = nextFill;
            dirty = true;
        }

        private Vector3 GetGravitySurfaceNormal()
        {
            Vector3 normal = transform.InverseTransformDirection(Vector3.up).normalized;
            return normal.sqrMagnitude < 0.5f ? Vector3.up : normal;
        }

        private void OnValidate()
        {
            radialSegments = Mathf.Clamp(radialSegments, 16, 96);
            rebuildsPerSecond = Mathf.Clamp(rebuildsPerSecond, 15f, 60f);
            if (isActiveAndEnabled)
            {
                EnsureComponents();
                dirty = true;
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
            dirty = false;
            if (meshFilter == null || meshRenderer == null || generatedMesh == null) return;

            if (body == null || displayedFill01 <= 0.001f)
            {
                generatedMesh.Clear();
                meshRenderer.enabled = true;
                return;
            }

            Vector3 bodyScale = body.localScale;
            bodyCenterLocal = transform.InverseTransformPoint(body.position);
            radius = Mathf.Min(Mathf.Abs(bodyScale.x), Mathf.Abs(bodyScale.z)) * 0.5f * radiusFactor;
            bottom = bodyCenterLocal.y - Mathf.Abs(bodyScale.y) + bottomMargin;
            top = bodyCenterLocal.y + Mathf.Abs(bodyScale.y) - topMargin;
            if (radius <= 0.0001f || top <= bottom)
            {
                generatedMesh.Clear();
                return;
            }

            Vector3 targetNormal = GetGravitySurfaceNormal();
            if (!surfaceInitialized || !Application.isPlaying)
            {
                localSurfaceNormal = targetNormal;
                surfaceInitialized = true;
            }
            renderSurfaceNormal = localSurfaceNormal;

            if (sloshEnergy > 0.0001f && maximumSloshAngle > 0f)
            {
                float angle = Mathf.Sin(animationTime * 10f) * maximumSloshAngle * sloshEnergy;
                Vector3 axis = Vector3.Cross(localSurfaceNormal, targetNormal);
                if (axis.sqrMagnitude < 0.01f) axis = Vector3.Cross(localSurfaceNormal, Vector3.right);
                if (axis.sqrMagnitude < 0.01f) axis = Vector3.Cross(localSurfaceNormal, Vector3.forward);
                renderSurfaceNormal = Quaternion.AngleAxis(angle, axis.normalized) * localSurfaceNormal;
            }

            planeOffset = SolvePlaneOffset(renderSurfaceNormal, displayedFill01);
            BuildBaseCylinder();
            ClipCylinderToSurface();
            BuildSurfaceCap();

            generatedMesh.Clear();
            generatedMesh.SetVertices(outputVertices);
            generatedMesh.SetTriangles(outputTriangles, 0);
            generatedMesh.RecalculateNormals();
            generatedMesh.RecalculateBounds();
            meshFilter.sharedMesh = generatedMesh;
            meshRenderer.enabled = true;
        }

        private float SolvePlaneOffset(Vector3 normal, float targetFill)
        {
            float horizontal = new Vector2(normal.x, normal.z).magnitude;
            if (horizontal < 0.00001f)
                return normal.y >= 0f
                    ? Mathf.Lerp(bottom, top, targetFill)
                    : Mathf.Lerp(top, bottom, targetFill);

            float centerOffset = normal.x * bodyCenterLocal.x + normal.z * bodyCenterLocal.z;
            float minimum = centerOffset + Mathf.Min(normal.y * bottom, normal.y * top) - horizontal * radius;
            float maximum = centerOffset + Mathf.Max(normal.y * bottom, normal.y * top) + horizontal * radius;

            for (int iteration = 0; iteration < VolumeSearchIterations; iteration++)
            {
                float middle = (minimum + maximum) * 0.5f;
                float volume = EstimateVolumeBelow(normal, middle);
                if (volume < targetFill) minimum = middle;
                else maximum = middle;
            }

            return (minimum + maximum) * 0.5f;
        }

        private float EstimateVolumeBelow(Vector3 normal, float offset)
        {
            float horizontal = new Vector2(normal.x, normal.z).magnitude;
            float centerOffset = normal.x * bodyCenterLocal.x + normal.z * bodyCenterLocal.z;
            float areaSum = 0f;

            for (int slice = 0; slice < VolumeSlices; slice++)
            {
                float y = Mathf.Lerp(bottom, top, (slice + 0.5f) / VolumeSlices);
                float threshold = offset - centerOffset - normal.y * y;
                if (horizontal < 0.00001f)
                {
                    areaSum += threshold >= 0f ? 1f : 0f;
                    continue;
                }

                float q = threshold / (horizontal * radius);
                if (q >= 1f) areaSum += 1f;
                else if (q <= -1f) continue;
                else areaSum += (Mathf.Acos(-q) + q * Mathf.Sqrt(Mathf.Max(0f, 1f - q * q))) / Mathf.PI;
            }

            return areaSum / VolumeSlices;
        }

        private void BuildBaseCylinder()
        {
            baseVertices.Clear();
            baseTriangles.Clear();
            int segments = Mathf.Clamp(radialSegments, 16, 96);

            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                float x = bodyCenterLocal.x + Mathf.Cos(angle) * radius;
                float z = bodyCenterLocal.z + Mathf.Sin(angle) * radius;
                baseVertices.Add(new Vector3(x, bottom, z));
                baseVertices.Add(new Vector3(x, top, z));
            }

            int topCenter = baseVertices.Count;
            baseVertices.Add(new Vector3(bodyCenterLocal.x, top, bodyCenterLocal.z));
            int bottomCenter = baseVertices.Count;
            baseVertices.Add(new Vector3(bodyCenterLocal.x, bottom, bodyCenterLocal.z));

            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                int b = a + 2;
                baseTriangles.Add(a); baseTriangles.Add(a + 1); baseTriangles.Add(b + 1);
                baseTriangles.Add(a); baseTriangles.Add(b + 1); baseTriangles.Add(b);
                baseTriangles.Add(topCenter); baseTriangles.Add(b + 1); baseTriangles.Add(a + 1);
                baseTriangles.Add(bottomCenter); baseTriangles.Add(a); baseTriangles.Add(b);
            }
        }

        private void ClipCylinderToSurface()
        {
            outputVertices.Clear();
            outputTriangles.Clear();
            surfacePoints.Clear();
            capPoints.Clear();
            surfaceCenterLocal = renderSurfaceNormal * planeOffset;

            for (int triangle = 0; triangle < baseTriangles.Count; triangle += 3)
            {
                clipInput.Clear();
                clipInput.Add(baseVertices[baseTriangles[triangle]]);
                clipInput.Add(baseVertices[baseTriangles[triangle + 1]]);
                clipInput.Add(baseVertices[baseTriangles[triangle + 2]]);
                ClipPolygon(clipInput, clipOutput);

                for (int i = 1; i < clipOutput.Count - 1; i++)
                    AddOutputTriangle(clipOutput[0], clipOutput[i], clipOutput[i + 1]);
            }
        }

        private void ClipPolygon(List<Vector3> input, List<Vector3> output)
        {
            output.Clear();
            for (int i = 0; i < input.Count; i++)
            {
                Vector3 current = input[i];
                Vector3 next = input[(i + 1) % input.Count];
                float currentDistance = Vector3.Dot(renderSurfaceNormal, current) - planeOffset;
                float nextDistance = Vector3.Dot(renderSurfaceNormal, next) - planeOffset;
                bool currentInside = currentDistance <= PlaneEpsilon;
                bool nextInside = nextDistance <= PlaneEpsilon;

                if (currentInside) output.Add(current);
                if (currentInside == nextInside) continue;

                float denominator = currentDistance - nextDistance;
                float t = Mathf.Abs(denominator) < 0.000001f ? 0.5f : currentDistance / denominator;
                Vector3 intersection = Vector3.Lerp(current, next, Mathf.Clamp01(t));
                output.Add(intersection);
                surfacePoints.Add(intersection);
            }
        }

        private void BuildSurfaceCap()
        {
            for (int i = 0; i < surfacePoints.Count; i++)
            {
                Vector3 candidate = surfacePoints[i];
                bool duplicate = false;
                for (int j = 0; j < capPoints.Count; j++)
                {
                    if ((capPoints[j] - candidate).sqrMagnitude > 0.00000001f) continue;
                    duplicate = true;
                    break;
                }
                if (!duplicate) capPoints.Add(candidate);
            }

            if (capPoints.Count < 3) return;

            Vector3 center = Vector3.zero;
            for (int i = 0; i < capPoints.Count; i++) center += capPoints[i];
            center /= capPoints.Count;
            surfaceCenterLocal = center;

            Vector3 helper = Mathf.Abs(Vector3.Dot(renderSurfaceNormal, Vector3.up)) < 0.9f
                ? Vector3.up : Vector3.right;
            Vector3 u = Vector3.Cross(renderSurfaceNormal, helper).normalized;
            Vector3 v = Vector3.Cross(renderSurfaceNormal, u).normalized;
            capPoints.Sort((a, b) =>
            {
                Vector3 deltaA = a - center;
                Vector3 deltaB = b - center;
                float angleA = Mathf.Atan2(Vector3.Dot(deltaA, v), Vector3.Dot(deltaA, u));
                float angleB = Mathf.Atan2(Vector3.Dot(deltaB, v), Vector3.Dot(deltaB, u));
                return angleA.CompareTo(angleB);
            });

            for (int i = 0; i < capPoints.Count; i++)
                AddOutputTriangle(center, capPoints[i], capPoints[(i + 1) % capPoints.Count]);
        }

        private void AddOutputTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            int start = outputVertices.Count;
            outputVertices.Add(a);
            outputVertices.Add(b);
            outputVertices.Add(c);
            outputTriangles.Add(start);
            outputTriangles.Add(start + 1);
            outputTriangles.Add(start + 2);
        }

        private void OnDestroy()
        {
            if (generatedMesh == null) return;
            if (Application.isPlaying) Destroy(generatedMesh);
            else DestroyImmediate(generatedMesh);
        }
    }
}
