using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>
    /// Transfers liquid from this vessel to the first valid vessel below the pour point.
    /// Stream and optional droplets are tinted from the source's current mixture colour.
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

        [Header("Stream appearance")]
        [SerializeField] private LineRenderer stream;
        [SerializeField, Min(0.002f)] private float streamWidth = 0.022f;
        [SerializeField, Range(4, 20)] private int streamSegments = 12;
        [SerializeField, Min(0f)] private float streamWobble = 0.006f;
        [SerializeField] private ParticleSystem droplets;

        private readonly Vector3[] points = new Vector3[20];
        private readonly RaycastHit[] rayHits = new RaycastHit[32];
        private float animationTime;
        private bool pouring;
        private Material runtimeStreamMaterial;

        public float FlowRate => flowRate;
        public bool IsPouring => pouring;
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

            // Clone the assigned material so each bottle/shaker can tint its own stream.
            // The material itself receives the mixture colour; line vertex colours stay white
            // to avoid a shader multiplying two different tints or ignoring vertex colours.
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

            Vector3 origin = pourPoint.position;
            PouringTarget target = FindTarget(origin, Vector3.down, out Vector3 hitPoint);
            if (target == null || target.Container == null || target.Container == container ||
                target.Container.RemainingSpace <= 0.001f)
            {
                StopPour();
                return;
            }

            animationTime += Time.deltaTime;
            Color sourceMixtureColor = container.MixtureColor;
            UpdateStream(origin, hitPoint, sourceMixtureColor);
            UpdateDropletColor(sourceMixtureColor);

            if (!pouring && droplets != null) droplets.Play();
            pouring = true;

            // Never consume more than the receiver can accept.
            float transferable = Mathf.Min(flowRate * Time.deltaTime,
                container.CurrentVolume, target.Container.RemainingSpace);
            if (transferable > 0.0001f)
                container.TransferTo(target.Container, transferable);

            if (target.Container.RemainingSpace <= 0.001f || container.CurrentVolume <= 0.001f)
                StopPour();
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

        private void UpdateStream(Vector3 start, Vector3 end, Color liquidColor)
        {
            if (stream == null) return;
            stream.enabled = true;
            stream.positionCount = streamSegments;

            Color tint = liquidColor;
            tint.a = 0.96f;
            SetMaterialColor(runtimeStreamMaterial, tint);
            // Keep vertex tint white: the runtime material is the single source of colour.
            stream.startColor = Color.white;
            stream.endColor = new Color(1f, 1f, 1f, 0.82f);

            Vector3 axis = end - start;
            Vector3 side = Vector3.Cross(axis.normalized, Vector3.forward);
            if (side.sqrMagnitude < 0.01f)
                side = Vector3.Cross(axis.normalized, Vector3.right);
            side.Normalize();

            for (int i = 0; i < streamSegments; i++)
            {
                float t = i / (float)(streamSegments - 1);
                float envelope = Mathf.Sin(t * Mathf.PI);
                float wobble = Mathf.Sin(animationTime * 13f + t * 11f) * streamWobble * envelope;
                points[i] = Vector3.Lerp(start, end, t) + side * wobble;
            }
            for (int i = 0; i < streamSegments; i++)
                stream.SetPosition(i, points[i]);
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
            if (stream != null) stream.enabled = false;
            if (droplets != null && droplets.isPlaying)
                droplets.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        private void OnDestroy()
        {
            if (runtimeStreamMaterial == null) return;
            if (Application.isPlaying) Destroy(runtimeStreamMaterial);
            else DestroyImmediate(runtimeStreamMaterial);
        }
    }
}
