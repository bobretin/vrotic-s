using System.Collections.Generic;
using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>
    /// Stores the exact volume of every ingredient in this vessel.
    /// The visible colour is a smoothed, volume-weighted mixture of those ingredients.
    /// </summary>
    [DisallowMultipleComponent]
    public class LiquidContainer : MonoBehaviour
    {
        [Header("Capacity (ml)")]
        [SerializeField, Min(1f)] private float capacity = 750f;
        [SerializeField, Min(0f)] private float startingVolume = 600f;
        [SerializeField] private string ingredientId = "juice";
        [SerializeField] private Color ingredientColor = new Color(1f, 0.32f, 0.04f, 1f);

        [Header("Liquid geometry")]
        [SerializeField] private Transform liquidVisual;
        [SerializeField] private Renderer liquidRenderer;
        [SerializeField] private LiquidVolumeMesh liquidVolumeMesh;
        [SerializeField] private bool anchorAtBottom = true;

        [Header("Mixture appearance")]
        [Tooltip("How quickly the visible liquid colour blends into the new mixture colour.")]
        [SerializeField, Min(0.1f)] private float colourBlendSpeed = 5f;
        [SerializeField, Min(0.1f)] private float mixingDuration = 1.8f;

        private readonly Dictionary<string, float> ingredients = new Dictionary<string, float>();
        private readonly Dictionary<string, Color> ingredientColors = new Dictionary<string, Color>();
        private Vector3 fullScale;
        private Vector3 fullPosition;
        private float bottomLocalY;
        private float currentVolume;
        private Color mixtureColor = new Color(0.2f, 0.6f, 1f, 1f);
        private Color visualMixtureColor = new Color(0.2f, 0.6f, 1f, 1f);
        private Color incomingMixtureColor = Color.white;
        private float mixingStrength;
        private MaterialPropertyBlock propertyBlock;
        private bool initialized;

        public float Capacity => capacity;
        public float CurrentVolume => currentVolume;
        public float Fill01 => capacity <= 0f ? 0f : Mathf.Clamp01(currentVolume / capacity);
        public float RemainingSpace => Mathf.Max(0f, capacity - currentVolume);
        public IReadOnlyDictionary<string, float> Ingredients => ingredients;
        public string IngredientId => ingredientId;
        public Color IngredientColor => ingredientColor;
        public Color MixtureColor => mixtureColor;
        public Color VisualMixtureColor => visualMixtureColor;
        public Color IncomingMixtureColor => incomingMixtureColor;
        public float MixingStrength => mixingStrength;
        public LiquidVolumeMesh VolumeMesh => liquidVolumeMesh;

        #if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying || liquidVisual == null) return;
            if (liquidRenderer == null) liquidRenderer = liquidVisual.GetComponent<Renderer>();
            if (liquidRenderer == null) return;
            mixtureColor = ingredientColor;
            visualMixtureColor = ingredientColor;
            ApplyVisualColor();
        }
        #endif

        private void Awake()
        {
            CacheVisualGeometry();
            InitializeContents();
        }

        private void OnEnable()
        {
            if (!initialized && Application.isPlaying)
            {
                CacheVisualGeometry();
                InitializeContents();
            }
        }

        private void Update()
        {
            if (!initialized) return;
            float blend = 1f - Mathf.Exp(-colourBlendSpeed * Time.deltaTime);
            visualMixtureColor = Color.Lerp(visualMixtureColor, mixtureColor, blend);
            mixingStrength = Mathf.Max(0f, mixingStrength - Time.deltaTime / Mathf.Max(0.1f, mixingDuration));
            ApplyVisualColor();
        }

        private void CacheVisualGeometry()
        {
            if (liquidVisual != null)
            {
                fullScale = liquidVisual.localScale;
                fullPosition = liquidVisual.localPosition;
                bottomLocalY = fullPosition.y - fullScale.y;
                if (liquidVolumeMesh == null) liquidVolumeMesh = liquidVisual.GetComponent<LiquidVolumeMesh>();
                if (liquidVolumeMesh == null) liquidVolumeMesh = liquidVisual.gameObject.AddComponent<LiquidVolumeMesh>();
                Transform vesselBody = liquidVisual.parent != null ? liquidVisual.parent.Find("Body") : null;
                if (vesselBody != null) liquidVolumeMesh.Configure(vesselBody);
                if (liquidRenderer == null) liquidRenderer = liquidVisual.GetComponent<Renderer>();
                LiquidMixingVisual mixingVisual = liquidVisual.GetComponent<LiquidMixingVisual>();
                if (mixingVisual == null) mixingVisual = liquidVisual.gameObject.AddComponent<LiquidMixingVisual>();
                mixingVisual.Configure(this);
            }
            if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
        }

        private void InitializeContents()
        {
            capacity = Mathf.Max(1f, capacity);
            currentVolume = Mathf.Clamp(startingVolume, 0f, capacity);
            ingredients.Clear();
            ingredientColors.Clear();
            mixingStrength = 0f;
            if (currentVolume > 0f)
            {
                string id = string.IsNullOrWhiteSpace(ingredientId) ? "ingredient" : ingredientId;
                ingredients[id] = currentVolume;
                ingredientColors[id] = ingredientColor;
            }
            initialized = true;
            RecalculateColor();
            visualMixtureColor = mixtureColor;
            RefreshVisuals();
        }

        /// <summary>Used by setup tools; retained for compatibility with existing scene objects.</summary>
        public void Configure(float newCapacity, float initialVolume, string id, Color color, Transform visual)
        {
            capacity = Mathf.Max(1f, newCapacity);
            startingVolume = Mathf.Clamp(initialVolume, 0f, capacity);
            ingredientId = string.IsNullOrWhiteSpace(id) ? "ingredient" : id;
            ingredientColor = color;
            liquidVisual = visual;
            liquidRenderer = visual != null ? visual.GetComponent<Renderer>() : null;
            liquidVolumeMesh = visual != null ? visual.GetComponent<LiquidVolumeMesh>() : null;
            CacheVisualGeometry();
            InitializeContents();
        }

        /// <summary>Adds one ingredient and returns the volume that was actually accepted.</summary>
        public float AddColoredLiquid(string id, float requestedAmount, Color color)
        {
            float accepted = Mathf.Min(Mathf.Max(0f, requestedAmount), RemainingSpace);
            if (accepted <= 0.0001f) return 0f;
            if (string.IsNullOrWhiteSpace(id)) id = "unknown";

            float previousVolume = currentVolume;
            if (!ingredients.ContainsKey(id)) ingredients[id] = 0f;
            ingredients[id] += accepted;
            ingredientColors[id] = color;
            currentVolume = Mathf.Min(capacity, currentVolume + accepted);
            if (previousVolume > 0.0001f)
            {
                incomingMixtureColor = color;
                NotifyIncomingLiquid(accepted);
            }
            RecalculateColor();
            RefreshVisuals();
            return accepted;
        }

        public float AddLiquid(string id, float requestedAmount, Color color)
        {
            return AddColoredLiquid(id, requestedAmount, color);
        }

        public float GetIngredientAmount(string id)
        {
            return !string.IsNullOrEmpty(id) && ingredients.TryGetValue(id, out float amount) ? amount : 0f;
        }

        public float GetIngredientFraction(string id)
        {
            return currentVolume <= 0.0001f ? 0f : GetIngredientAmount(id) / currentVolume;
        }

        /// <summary>Extracts a proportional payload while reducing this vessel immediately.</summary>
        public LiquidPayload ExtractLiquid(float requestedAmount)
        {
            float removed = Mathf.Min(Mathf.Max(0f, requestedAmount), currentVolume);
            if (removed <= 0.0001f) return LiquidPayload.Empty;

            float ratio = removed / currentVolume;
            int count = ingredients.Count;
            string[] ids = new string[count];
            float[] amounts = new float[count];
            Color[] colors = new Color[count];
            int index = 0;
            foreach (KeyValuePair<string, float> item in ingredients)
            {
                ids[index] = item.Key;
                amounts[index] = item.Value * ratio;
                colors[index] = ingredientColors.TryGetValue(item.Key, out Color stored) ? stored : ingredientColor;
                index++;
            }

            RemoveLiquid(removed);
            return new LiquidPayload(ids, amounts, colors);
        }

        /// <summary>Removes a proportional share of every ingredient in this vessel.</summary>
        public float RemoveLiquid(float requestedAmount)
        {
            float removed = Mathf.Min(Mathf.Max(0f, requestedAmount), currentVolume);
            if (removed <= 0.0001f) return 0f;

            float ratio = removed / currentVolume;
            var keys = new List<string>(ingredients.Keys);
            foreach (string key in keys)
            {
                ingredients[key] -= ingredients[key] * ratio;
                if (ingredients[key] <= 0.0001f) ingredients.Remove(key);
            }

            currentVolume = Mathf.Max(0f, currentVolume - removed);
            RecalculateColor();
            RefreshVisuals();
            return removed;
        }

        public float TransferTo(LiquidContainer target, float requestedAmount)
        {
            if (target == null || target == this) return 0f;
            float amount = Mathf.Min(Mathf.Max(0f, requestedAmount), currentVolume, target.RemainingSpace);
            if (amount <= 0.0001f) return 0f;

            LiquidPayload payload = ExtractLiquid(amount);
            LiquidPayload remainder = payload.AddTo(target);
            if (remainder.Volume > 0.0001f) remainder = remainder.AddTo(this);
            return amount - remainder.Volume;
        }

        public bool HasLiquidAtWorldPoint(Vector3 point)
        {
            return liquidVolumeMesh != null
                ? liquidVolumeMesh.ContainsWorldPoint(point)
                : currentVolume > 0.001f;
        }

        public bool HasLiquidAtOpening(Vector3 opening)
        {
            return TryGetWetOpening(opening, out _);
        }

        public bool TryGetWetOpening(Vector3 opening, out Vector3 flowPoint)
        {
            if (liquidVolumeMesh != null)
                return liquidVolumeMesh.TryGetWetOpening(opening, out flowPoint);
            flowPoint = opening;
            return currentVolume > 0.001f;
        }

        public void NotifyIncomingLiquid(float amount)
        {
            if (amount <= 0f) return;
            float impulse = Mathf.Clamp01(amount / Mathf.Max(1f, capacity * 0.025f));
            mixingStrength = Mathf.Clamp01(Mathf.Max(mixingStrength, 0.35f + impulse * 0.65f));
            if (liquidVolumeMesh != null) liquidVolumeMesh.AddSlosh(0.35f + impulse * 0.65f);
        }

        public Color GetMixtureColor() => mixtureColor;

        private void RecalculateColor()
        {
            if (currentVolume <= 0.0001f || ingredients.Count == 0)
            {
                mixtureColor = new Color(0.2f, 0.6f, 1f, 1f);
                return;
            }

            Color linearSum = Color.black;
            foreach (KeyValuePair<string, float> item in ingredients)
            {
                Color source = ingredientColors.TryGetValue(item.Key, out Color stored) ? stored : ingredientColor;
                Color linear = source.linear;
                linearSum += new Color(linear.r, linear.g, linear.b, 1f) * item.Value;
            }

            Color mixedLinear = linearSum / Mathf.Max(0.0001f, currentVolume);
            mixtureColor = mixedLinear.gamma;
            mixtureColor.a = 1f;
        }

        private void RefreshVisuals()
        {
            if (liquidVisual != null)
            {
                float fill = Fill01;
                if (liquidVolumeMesh != null)
                {
                    liquidVisual.localPosition = Vector3.zero;
                    liquidVisual.localRotation = Quaternion.identity;
                    liquidVisual.localScale = Vector3.one;
                    liquidVisual.gameObject.SetActive(true);
                    liquidVolumeMesh.SetFill(fill);
                }
                else
                {
                    if (anchorAtBottom)
                    {
                        liquidVisual.localScale = new Vector3(fullScale.x, Mathf.Max(0.0001f, fullScale.y * fill), fullScale.z);
                        Vector3 position = fullPosition;
                        position.y = bottomLocalY + fullScale.y * fill;
                        liquidVisual.localPosition = position;
                    }
                    else
                    {
                        liquidVisual.localScale = new Vector3(fullScale.x, Mathf.Max(0.0001f, fullScale.y * fill), fullScale.z);
                    }
                    liquidVisual.gameObject.SetActive(fill > 0.002f);
                }
            }

            ApplyVisualColor();
        }

        private void ApplyVisualColor()
        {
            if (liquidRenderer == null) return;
            if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
            liquidRenderer.GetPropertyBlock(propertyBlock);
            Material material = liquidRenderer.sharedMaterial;
            if (material != null)
            {
                if (material.HasProperty("_BaseColor")) propertyBlock.SetColor("_BaseColor", visualMixtureColor);
                if (material.HasProperty("_Color")) propertyBlock.SetColor("_Color", visualMixtureColor);
            }
            liquidRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
