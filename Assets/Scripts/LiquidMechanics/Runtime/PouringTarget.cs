using UnityEngine;

namespace MultiBar.Liquids
{
    [RequireComponent(typeof(LiquidContainer))]
    public class PouringTarget : MonoBehaviour
    {
        [SerializeField] private Transform openingPoint;
        public LiquidContainer Container { get; private set; }
        public Vector3 OpeningPosition => openingPoint != null ? openingPoint.position : transform.position;

        private void Awake() => Container = GetComponent<LiquidContainer>();
        private void Reset() => Container = GetComponent<LiquidContainer>();
    }
}
