using UnityEngine;
using CardGame.Core.Cards;

namespace CardGame.Gameplay.Zones
{
    [RequireComponent(typeof(Collider2D))]
    public sealed class ExplorationLaneAreaTrigger2D : MonoBehaviour
    {
        [SerializeField] private ExplorationZone zone; // 父物体上的 Zone

        private void Reset()
        {
            if (zone == null) zone = GetComponentInParent<ExplorationZone>();
            var col = GetComponent<Collider2D>();
            if (col != null) col.isTrigger = true;
        }

        private void Awake()
        {
            if (zone == null) zone = GetComponentInParent<ExplorationZone>();
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (zone == null) return;
            var view = other.GetComponentInParent<CardWorldView>();
            if (view == null) return;

            zone.Enter(view); // ZoneBase 的 Enter 会调用 CanEnter
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (zone == null) return;
            var view = other.GetComponentInParent<CardWorldView>();
            if (view == null) return;

            zone.Exit(view);
        }
    }
}
