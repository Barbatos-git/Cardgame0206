using UnityEngine;
using CardGame.Core.Cards;

namespace CardGame.Gameplay.Common
{
    /// <summary>
    /// すべてのカード実体の共通基底（MonoBehaviour）
    /// </summary>
    public abstract class CardEntityBase : MonoBehaviour, ICardEntity
    {
        private static int _nextId = 1;

        [SerializeField] private string debugName = "Card";

        public int InstanceId { get; private set; }
        public string DebugName => debugName;
        public Transform Transform => transform;

        protected virtual void Awake()
        {
            InstanceId = _nextId++;
        }

        public virtual void Despawn()
        {
            Destroy(gameObject);
        }

        protected void SetDebugName(string name)
        {
            debugName = name;
        }
    }
}
