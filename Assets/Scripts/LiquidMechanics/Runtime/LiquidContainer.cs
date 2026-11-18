using System.Collections.Generic;
using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>Stores liquid volume and ingredient proportions for one vessel.</summary>
    public class LiquidContainer : MonoBehaviour
    {
        [Header("Capacity (ml)")]
        [Min(1f)] [SerializeField] private float capacity = 750f;
        [Min(0f)] [SerializeField] private float startingVolume = 600f;
        [SerializeField] private string ingredientId = "juice";
        [SerializeField] private Color ingredientColor = new Color(1f, 0.32f, 0.04f, 1f);

        [Header("Liquid geometry")]
        [SerializeField] private Transform liquidVisual;
        [SerializeField] private Renderer liquidRenderer;
        [SerializeField] private LiquidVolumeMesh liquidVolumeMesh;
        [Tooltip("If enabled, the bottom of the liquid mesh stays fixed while its height changes.")]
        [SerializeField] private bool anchorAtBottom = true;

        private readonly Dictionary<string, float> ingredients = new Dictionary<string, float>();
        private readonly Dictionary<string, Color> ingredientColors = new Dictionary<string, Color>();
        private Vector3 fullScale;
        private Vector3 fullPosition;
        private float bottomLocalY;
        private float currentVolume;
        private Color mixtureColor;
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

        private void CacheVisualGeometry()
        {
            if (liquidVisual != null)
            {
                fullScale = liquidVisual.localScale;
                fullPosition = liquidVisual.localPosition;
                bottomLocalY = fullPosition.y - fullScale.y;
                liquidRenderer = liquidVisual.GetComponent<Renderer>();
                liquidVolumeMesh = liquidVisual.GetComponent<LiquidVolumeMesh>();
            }
            if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
        }

        private void InitializeContents()
        {
            currentVolume = Mathf.Clamp(startingVolume, 0f, capacity);
            ingredients.Clear();
            ingredientColors.Clear();
            if (currentVolume > 0f)
            {
                ingredients[ingredientId] = currentVolume;
                ingredientColors[ingredientId] = ingredientColor;
            }
            initialized = true;
            RecalculateColor();
            RefreshVisuals();
        }

        /// <summary>Called by the Editor setup utility and safe to call before Play Mode.</summary>
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

        public float AddColoredLiquid(string id, float requestedAmount, Color color)
        {
            float accepted = Mathf.Min(Mathf.Max(0f, requestedAmount), RemainingSpace);
            if (accepted <= 0.0001f) return 0f;
            if (string.IsNullOrWhiteSpace(id)) id = "unknown";
            if (!ingredients.ContainsKey(id)) ingredients[id] = 0f;
            ingredients[id] += accepted;
            ingredientColors[id] = color;
            currentVolume = Mathf.Min(capacity, currentVolume + accepted);
            RecalculateColor();
            RefreshVisuals();
            return accepted;
        }

        public float AddLiquid(string id, float requestedAmount, Color color)
        {
            return AddColoredLiquid(id, requestedAmount, color);
        }

        /// <summary>Removes a proportional share of every ingredient in a mixed vessel.</summary>
        public float RemoveLiquid(float requestedAmount)
        {
            float removed = Mathf.Min(Mathf.Max(0f, requestedAmount), currentVolume);
            if (removed <= 0.0001f) return 0f;

            float before = currentVolume;
            float ratio = removed / before;
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

        /// <summary>Moves a proportional share of every ingredient to another vessel.</summary>
        public float TransferTo(LiquidContainer target, float requestedAmount)
        {
            if (target == null || target == this) return 0f;
            float amount = Mathf.Min(Mathf.Max(0f, requestedAmount), currentVolume, target.RemainingSpace);
            if (amount <= 0.0001f) return 0f;

            float before = currentVolume;
            float ratio = amount / before;
            var keys = new List<string>(ingredients.Keys);
            var moved = new List<KeyValuePair<string, float>>(keys.Count);
            foreach (string key in keys)
            {
                float part = ingredients[key] * ratio;
                Color color = ingredientColors.TryGetValue(key, out Color stored) ? stored : ingredientColor;
                moved.Add(new KeyValuePair<string, float>(key, part));
                ingredientColors[key] = color;
            }

            float removed = RemoveLiquid(amount);
            if (removed <= 0.0001f) return 0f;
            foreach (KeyValuePair<string, float> part in moved)
            {
                Color color = ingredientColors.TryGetValue(part.Key, out Color stored) ? stored : ingredientColor;
                target.AddColoredLiquid(part.Key, part.Value, color);
            }
            return removed;
        }

        public Color GetMixtureColor() => mixtureColor;

        private void RecalculateColor()
        {
            if (currentVolume <= 0.0001f || ingredients.Count == 0)
            {
                mixtureColor = new Color(0.2f, 0.6f, 1f, 1f);
                return;
            }

            Color sum = Color.black;
            foreach (KeyValuePair<string, float> item in ingredients)
            {
                Color c = ingredientColors.TryGetValue(item.Key, out Color stored) ? stored : ingredientColor;
                sum += c * item.Value;
            }
            mixtureColor = sum / Mathf.Max(0.0001f, currentVolume);
            mixtureColor.a = 1f;
        }

        private void RefreshVisuals()
        {
            if (liquidVisual != null)
            {
                float fill = Fill01;
                if (liquidVolumeMesh != null)
                {
                    // The volumetric mesh builds its own closed side walls and top/bottom caps.
                    // Do not scale the mesh Transform: its geometry is already generated in vessel space.
                    liquidVisual.localPosition = Vector3.zero;
                    liquidVisual.localRotation = Quaternion.identity;
                    liquidVisual.localScale = Vector3.one;
                    liquidVisual.gameObject.SetActive(true);
                    liquidVolumeMesh.SetFill(fill);
                }
                else
                {
                    // Legacy cylinder fallback for vessels that do not use LiquidVolumeMesh.
                    if (anchorAtBottom)
                    {
                        liquidVisual.localScale = new Vector3(fullScale.x, Mathf.Max(0.0001f, fullScale.y * fill), fullScale.z);
                        Vector3 p = fullPosition;
                        p.y = bottomLocalY + fullScale.y * fill;
                        liquidVisual.localPosition = p;
                    }
                    else
                    {
                        liquidVisual.localScale = new Vector3(fullScale.x, Mathf.Max(0.0001f, fullScale.y * fill), fullScale.z);
                    }
                    liquidVisual.gameObject.SetActive(fill > 0.002f);
                }
            }

            if (liquidRenderer != null)
            {
                if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
                liquidRenderer.GetPropertyBlock(propertyBlock);
                if (liquidRenderer.sharedMaterial != null)
                {
                    if (liquidRenderer.sharedMaterial.HasProperty("_BaseColor")) propertyBlock.SetColor("_BaseColor", mixtureColor);
                    if (liquidRenderer.sharedMaterial.HasProperty("_Color")) propertyBlock.SetColor("_Color", mixtureColor);
                }
                liquidRenderer.SetPropertyBlock(propertyBlock);
            }
        }
    }
}
