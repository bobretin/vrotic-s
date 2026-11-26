using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>
    /// Emits ingredient payloads from a wet mouth and delivers them after flight.
    /// The stream follows gravity; the receiver does not gain volume instantly.
    /// </summary>
    [DisallowMultipleComponent]
    public class PouringSource : MonoBehaviour
    {
        [Header("Pouring")]
        [SerializeField] private LiquidContainer container;
        [SerializeField] private Transform pourPoint;
        [SerializeField, Range(0f, 180f)] private float pourAngle = 45f;
        [SerializeField, Min(0.1f)] private float flowRate = 25f;
        [SerializeField, Min(0.1f)] private float rayDistance = 2.5f;
        [SerializeField] private LayerMask targetLayers = ~0;
        [SerializeField, Min(0.02f)] private float batchInterval = 0.06f;
        [SerializeField, Min(0.1f)] private float streamSpeed = 2.5f;

        [Header("Stream appearance")]
        [SerializeField] private LineRenderer stream;
        [SerializeField, Min(0.002f)] private float streamWidth = 0.022f;
        [SerializeField, Range(4, 20)] private int streamSegments = 12;
        [SerializeField, Min(0f)] private float streamWobble = 0.006f;
        [SerializeField] private ParticleSystem droplets;

        private readonly Vector3[] points = new Vector3[20];
        private readonly RaycastHit[] rayHits = new RaycastHit[32];
        private readonly PouringFlightQueue flightQueue = new PouringFlightQueue();
        private float animationTime;
        private float batchTime;
        private float lastFlightTime = 0.08f;
        private bool pouring;
        private Material runtimeStreamMaterial;

        public float FlowRate => flowRate;
        public bool IsPouring => pouring;
        public int InFlightBatchCount => flightQueue.Count;
        public void SetFlowRate(float value) => flowRate = Mathf.Max(0.1f, value);

        private void Reset() => container = GetComponent<LiquidContainer>();

        private void Awake()
        {
            if (container == null) container = GetComponent<LiquidContainer>();
            PrepareStreamRenderer();
            if (droplets != null)
                droplets.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void PrepareStreamRenderer()
        {
            if (stream == null) return;

            stream.useWorldSpace = true;
            stream.positionCount = streamSegments;
            stream.startWidth = streamWidth;
            stream.endWidth = streamWidth * 0.65f;
            stream.numCornerVertices = 4;
            stream.numCapVertices = 4;
            stream.alignment = LineAlignment.View;
            stream.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            stream.receiveShadows = false;

            Material sourceMaterial = stream.sharedMaterial;
            if (sourceMaterial != null)
                runtimeStreamMaterial = new Material(sourceMaterial);
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null) runtimeStreamMaterial = new Material(shader);
            }

            if (runtimeStreamMaterial != null)
            {
                runtimeStreamMaterial.name = "LM_Stream_Runtime_" + GetInstanceID();
                SetMaterialColor(runtimeStreamMaterial, Color.white);
                stream.sharedMaterial = runtimeStreamMaterial;
            }

            stream.startColor = Color.white;
            stream.endColor = Color.white;
            stream.enabled = false;
        }

        private void Update()
        {
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            flightQueue.Advance(deltaTime);

            if (container == null || pourPoint == null || container.CurrentVolume <= 0.001f)
            {
                StopPour();
                return;
            }

            float tilt = Vector3.Angle(transform.up, Vector3.up);
            if (tilt <= pourAngle)
            {
                StopPour();
                return;
            }

            if (!container.TryGetWetOpening(pourPoint.position, out Vector3 origin))
            {
                StopPour();
                return;
            }

            PouringTarget target = FindTarget(origin, Vector3.down, out Vector3 hitPoint);
            if (target == null || target.Container == null || target.Container == container)
            {
                StopPour();
                return;
            }

            float availableSpace = target.Container.RemainingSpace - flightQueue.PendingVolumeFor(target.Container);
            if (availableSpace <= 0.001f)
            {
                StopPour();
                return;
            }

            animationTime += deltaTime;
            lastFlightTime = CalculateFlightTime(origin, hitPoint);
            Color sourceMixtureColor = container.MixtureColor;
            UpdateStream(origin, hitPoint, sourceMixtureColor);
            UpdateDropletColor(sourceMixtureColor);

            if (!pouring && droplets != null) droplets.Play();
            pouring = true;
            batchTime += deltaTime;

            if (batchTime >= batchInterval)
            {
                float batchDuration = batchTime;
                batchTime = 0f;
                float amount = Mathf.Min(flowRate * batchDuration, container.CurrentVolume, availableSpace);
                if (amount > 0.0001f)
                {
                    LiquidPayload payload = container.ExtractLiquid(amount);
                    flightQueue.Enqueue(container, target.Container, payload, lastFlightTime);
                }
            }

            if (container.CurrentVolume <= 0.001f) StopPour();
        }

        private PouringTarget FindTarget(Vector3 origin, Vector3 direction, out Vector3 endPoint)
        {
            endPoint = origin + direction * rayDistance;
            int hitCount = Physics.RaycastNonAlloc(origin, direction, rayHits, rayDistance,
                targetLayers, QueryTriggerInteraction.Ignore);

            int nearestIndex = -1;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                Transform hitTransform = rayHits[i].collider.transform;
                if (hitTransform == transform || hitTransform.IsChildOf(transform)) continue;
                if (rayHits[i].distance < nearestDistance)
                {
                    nearestDistance = rayHits[i].distance;
                    nearestIndex = i;
                }
            }

            if (nearestIndex < 0) return null;
            RaycastHit nearestHit = rayHits[nearestIndex];
            endPoint = nearestHit.point;
            return nearestHit.collider.GetComponentInParent<PouringTarget>();
        }

        private float CalculateFlightTime(Vector3 start, Vector3 end)
        {
            return Mathf.Clamp(Vector3.Distance(start, end) / Mathf.Max(0.1f, streamSpeed), 0.08f, 0.45f);
        }

        private void UpdateStream(Vector3 start, Vector3 end, Color liquidColor)
        {
            if (stream == null) return;
            stream.enabled = true;
            stream.positionCount = streamSegments;
            float widthScale = Mathf.Clamp(Mathf.Sqrt(flowRate / 25f), 0.45f, 1.5f);
            stream.startWidth = streamWidth * widthScale;
            stream.endWidth = streamWidth * 0.65f * widthScale;

            Color tint = liquidColor;
            tint.a = 0.96f;
            SetMaterialColor(runtimeStreamMaterial, tint);
            stream.startColor = Color.white;
            stream.endColor = new Color(1f, 1f, 1f, 0.82f);

            float flightTime = CalculateFlightTime(start, end);
            Vector3 gravity = Physics.gravity;
            Vector3 velocity = (end - start - 0.5f * gravity * flightTime * flightTime) / flightTime;
            Vector3 axis = end - start;
            Vector3 side = Vector3.Cross(axis.normalized, Vector3.forward);
            if (side.sqrMagnitude < 0.01f) side = Vector3.Cross(axis.normalized, Vector3.right);
            side.Normalize();

            for (int i = 0; i < streamSegments; i++)
            {
                float t = i / (float)(streamSegments - 1);
                float time = t * flightTime;
                float envelope = Mathf.Sin(t * Mathf.PI);
                float wobble = Mathf.Sin(animationTime * 13f + t * 11f) * streamWobble * envelope;
                points[i] = start + velocity * time + 0.5f * gravity * time * time + side * wobble;
            }
            for (int i = 0; i < streamSegments; i++) stream.SetPosition(i, points[i]);
        }

        private void UpdateDropletColor(Color liquidColor)
        {
            if (droplets == null) return;
            ParticleSystem.MainModule main = droplets.main;
            main.startColor = new ParticleSystem.MinMaxGradient(liquidColor);
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        private void StopPour()
        {
            pouring = false;
            batchTime = 0f;
            if (stream != null) stream.enabled = false;
            if (droplets != null && droplets.isPlaying)
                droplets.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        private void OnDisable()
        {
            StopPour();
            flightQueue.Flush();
        }

        private void OnDestroy()
        {
            flightQueue.Flush();
            if (runtimeStreamMaterial == null) return;
            if (Application.isPlaying) Destroy(runtimeStreamMaterial);
            else DestroyImmediate(runtimeStreamMaterial);
        }
    }
}
