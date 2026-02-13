using UnityEngine;

namespace CardGame.Gameplay.Cards
{
    /// <summary>
    /// 统一的卡牌生成入口（收口点）：
    /// - 运行时只用 CardDefinition
    /// - Instantiate 通用 cardPrefab
    /// - 必须调用 CardWorldView.Setup(def) 完成身份安装/图像/层等
    /// </summary>
    public sealed class CardFactory : MonoBehaviour, IResettable
    {
        public static CardFactory Instance { get; private set; }

        [Header("通用卡牌 Prefab（带 CardWorldView + CardBehaviorInstaller 等）")]
        [SerializeField] private CardWorldView cardPrefab;

        [Header("可选：用 string id 生成时需要 CardDatabase")]
        [SerializeField] private CardDatabase database;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 生成一张“正常卡”（会 Setup，会装配身份/逻辑组件）
        /// </summary>
        public CardWorldView Spawn(CardDefinition def, Vector3 worldPos, Transform parent = null)
        {
            if (cardPrefab == null)
            {
                Debug.LogError("[CardFactory] cardPrefab 未设置。", this);
                return null;
            }
            if (def == null)
            {
                Debug.LogError("[CardFactory] def 为 null，无法生成。", this);
                return null;
            }

            var view = Instantiate(cardPrefab, worldPos, Quaternion.identity, parent);
            view.Setup(def); // 关键：收口（安装角色/敌人/耐久等）
            return view;
        }

        /// <summary>
        /// 通过 id 生成（需要 database）
        /// </summary>
        public CardWorldView SpawnById(string id, Vector3 worldPos, Transform parent = null)
        {
            if (database == null)
            {
                Debug.LogError("[CardFactory] database 未设置，无法通过 id 生成。", this);
                return null;
            }

            var def = database.GetCardById(id);
            if (def == null)
            {
                Debug.LogError($"[CardFactory] GetCardById 找不到 id={id}", this);
                return null;
            }

            return Spawn(def, worldPos, parent);
        }

        /// <summary>
        /// 生成“遭遇卡背”（不调用 Setup，不装身份逻辑；只做展示用）
        /// 供 ExplorationZone 生成移动中的卡背使用。
        /// </summary>
        public CardWorldView SpawnBack(Sprite backSprite, Vector3 worldPos, Transform parent = null)
        {
            if (cardPrefab == null)
            {
                Debug.LogError("[CardFactory] cardPrefab 未设置。", this);
                return null;
            }
            if (backSprite == null)
            {
                Debug.LogError("[CardFactory] backSprite 为 null。", this);
                return null;
            }

            var view = Instantiate(cardPrefab, worldPos, Quaternion.identity, parent);

            // 只换图，不 Setup（避免装身份/AI/耐久等）
            if (view.artworkRenderer != null)
                view.artworkRenderer.sprite = backSprite;

            return view;
        }

        public void ResetState()
        {
            // 1) 结束本对象上的协程/延迟任务（安全兜底）
            StopAllCoroutines();

            // 2) 清掉单例引用（避免下一局拿到旧实例）
            if (Instance == this)
                Instance = null;

            // 3) 如有运行时缓存/对象池，在这里清
            // _pool.Clear();
        }
    }
}
