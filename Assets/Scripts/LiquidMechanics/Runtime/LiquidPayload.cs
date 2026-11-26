using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>Immutable ingredient amounts extracted from a vessel.</summary>
    public sealed class LiquidPayload
    {
        public static readonly LiquidPayload Empty = new LiquidPayload(
            new string[0], new float[0], new Color[0]);

        private readonly string[] ids;
        private readonly float[] amounts;
        private readonly Color[] colors;

        public float Volume { get; }

        internal LiquidPayload(string[] ids, float[] amounts, Color[] colors)
        {
            this.ids = ids;
            this.amounts = amounts;
            this.colors = colors;
            float volume = 0f;
            for (int i = 0; i < amounts.Length; i++) volume += Mathf.Max(0f, amounts[i]);
            Volume = volume;
        }

        public float GetAmount(string id)
        {
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == id) return amounts[i];
            return 0f;
        }

        public Color GetColor(string id)
        {
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == id) return colors[i];
            return Color.clear;
        }

        /// <summary>
        /// Delivers a proportional share that fits the target and returns the remainder.
        /// Neither a null nor a full target can destroy the payload.
        /// </summary>
        public LiquidPayload AddTo(LiquidContainer target)
        {
            if (Volume <= 0.0001f) return Empty;
            if (target == null || target.RemainingSpace <= 0.0001f) return this;

            float acceptedVolume = Mathf.Min(Volume, target.RemainingSpace);
            float acceptedRatio = acceptedVolume / Volume;
            string[] remainingIds = new string[ids.Length];
            float[] remainingAmounts = new float[ids.Length];
            Color[] remainingColors = new Color[ids.Length];
            int remainingCount = 0;

            for (int i = 0; i < ids.Length; i++)
            {
                float accepted = amounts[i] * acceptedRatio;
                float remaining = amounts[i] - accepted;
                if (accepted > 0f) target.AddColoredLiquid(ids[i], accepted, colors[i]);
                if (remaining <= 0.0001f) continue;
                remainingIds[remainingCount] = ids[i];
                remainingAmounts[remainingCount] = remaining;
                remainingColors[remainingCount] = colors[i];
                remainingCount++;
            }

            if (remainingCount == 0) return Empty;
            if (remainingCount != ids.Length)
            {
                System.Array.Resize(ref remainingIds, remainingCount);
                System.Array.Resize(ref remainingAmounts, remainingCount);
                System.Array.Resize(ref remainingColors, remainingCount);
            }
            return new LiquidPayload(remainingIds, remainingAmounts, remainingColors);
        }
    }
}
