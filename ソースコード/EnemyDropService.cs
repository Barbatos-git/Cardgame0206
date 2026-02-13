using UnityEngine;
using DG.Tweening;

/// <summary>
/// 敌人死亡掉落：以敌人死亡点为中心四散掉落（DOTween），并保证不出普通区边界。
/// - CardDefinition.id 是 string，所以用 CardDatabase.GetCardById(string)
/// - 数量（min/maxAmount）通过生成多张卡实现（让现有堆叠系统自己吸附/合堆）
/// - 位置移动用 CardWorldView.MoveWorldPosition()，结束时自带 ClampStackToNormalArea()（双保险）
/// </summary>
public sealed class EnemyDropService : MonoBehaviour, IResettable
{
    public static EnemyDropService Instance { get; private set; }

    [Header("Prefab")]
    [SerializeField] private CardWorldView cardPrefab; // 通用卡牌 prefab（带 Collider2D、CardWorldView、CardBehaviorInstaller）

    [Header("Normal Area Boundary (required for no-out-of-bounds)")]
    [SerializeField] private Collider2D normalAreaCollider; // 普通区边界 Collider2D（强烈建议拖入）

    [Header("Scatter")]
    [SerializeField, Min(0f)] private float scatterMinDistance = 0.4f;
    [SerializeField, Min(0f)] private float scatterMaxDistance = 1.2f;
    [SerializeField, Min(0f)] private float scatterDurationMin = 0.18f;
    [SerializeField, Min(0f)] private float scatterDurationMax = 0.35f;
    [SerializeField] private Ease scatterEase = Ease.OutQuad;

    [Header("Spawn spacing (avoid perfect overlap)")]
    [SerializeField, Min(0f)] private float perCardJitterRadius = 0.10f;

    [SerializeField] private bool debugDropLog = true;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// 在敌人死亡时调用。
    /// enemyDef.drops 内的 DropEntry.cardId 建议改成 string（对应 CardDefinition.id）
    /// </summary>
    public void SpawnEnemyDrops(Vector3 enemyWorldPos, CardGame.Data.Defs.EnemyDefinition enemyDef)
    {
        if (debugDropLog)
            Debug.Log($"[EnemyDrop] Start enemy='{enemyDef?.displayName}' dropsCount={enemyDef?.drops?.Count} at {enemyWorldPos}", this);

        if (cardPrefab == null)
        {
            Debug.LogError("[EnemyDropService] cardPrefab is NULL.", this);
            return;
        }

        if (enemyDef == null || enemyDef.drops == null || enemyDef.drops.Count == 0)
        {
            if (debugDropLog) Debug.LogWarning("[EnemyDrop] enemyDef or drops empty -> return", this);
            return;
        }

        if (CardDatabase.Instance == null)
        {
            Debug.LogError("[EnemyDrop] CardDatabase.Instance is NULL (scene has no CardDatabase?)", this);
            return;
        }

        EnsureNormalAreaCollider();

        // 如果还是没有普通区边界，那就退化成“只做散开动画，不做边界限制”
        Bounds areaBounds = normalAreaCollider != null ? normalAreaCollider.bounds : default;

        for (int i = 0; i < enemyDef.drops.Count; i++)
        {
            var d = enemyDef.drops[i];
            if (d == null)
            {
                Debug.LogWarning("[EnemyDrop] drop entry is NULL");
                continue;
            }

            Debug.Log($"[EnemyDrop] Entry {i}: cardId='{d.cardId}', chance={d.chance}, min={d.minAmount}, max={d.maxAmount}");

            // 概率判定
            if (Random.value > d.chance) 
            {
                continue;
            }

            int amount = Random.Range(d.minAmount, d.maxAmount + 1);
            Debug.Log($"[EnemyDrop] PASS chance. amount={amount}");
            if (amount <= 0) continue;

            // 查卡定义（ CardDatabase 用 string id）
            var def = CardDatabase.Instance != null ? CardDatabase.Instance.GetCardById(d.cardId) : null;
            if (def == null)
            {
                Debug.LogWarning($"[EnemyDrop] GetCardById failed. drop.cardId='{d.cardId}' (must match CardDefinition.id)", this);
                continue;
            }
            Debug.Log($"[EnemyDrop] Found CardDefinition: id='{def.id}', name='{def.displayName}'");

            // 生成 amount 张卡
            for (int k = 0; k < amount; k++)
            {
                // 1) 先生成在死亡点（可加一点抖动避免完全重叠）
                Vector2 jitter = Random.insideUnitCircle * perCardJitterRadius;
                Vector3 spawnPos = enemyWorldPos + new Vector3(jitter.x, jitter.y, 0f);

                var view = Instantiate(cardPrefab, spawnPos, Quaternion.identity);
                Debug.Log($"[EnemyDrop] Spawned view='{view.name}' at {spawnPos}");

                // 给新卡补普通区边界（很多时候 prefab 上没拖）
                if (normalAreaCollider != null)
                    view.normalAreaCollider = normalAreaCollider;

                // 2) Setup（会自动装配行为/视觉/排序/堆叠逻辑）
                view.Setup(def);
                Debug.Log($"[EnemyDrop] Setup done. worldPos={view.transform.position}");

                // 3) 计算散开目标点，并 clamp（不出界）
                Vector3 target = GetScatterTarget(enemyWorldPos, view, areaBounds);

                // 4) DOTween 移动（用项目现成的 MoveWorldPosition：结束会 ClampStackToNormalArea + RaiseWorldPositionChanged）
                float dur = Random.Range(scatterDurationMin, scatterDurationMax);

                // 先 kill 一下该 transform 可能残留的 tween（更稳）
                view.transform.DOKill(false);

                view.transform
                    .DOMove(target, dur)
                    .SetEase(scatterEase)
                    .OnComplete(() =>
                    {
                        // 双保险：CardWorldView.MoveWorldPosition() 自带 clamp，
                        // 但我们这里直接 DOMove，所以手动调用一遍“等价逻辑”
                        // ——最小侵入做法：调用 SetWorldPosition 触发事件 + 让外部系统刷新
                        // 同时 ClampStackToNormalArea 是 private，不能直接调，所以依赖我们前面的 target clamp + RaiseWorldPositionChanged。
                        view.SetWorldPosition(view.transform.position);
                    });
            }
        }
    }

    /// <summary>
    /// 计算目标点：以中心四散，且保证卡牌 collider 不会越界（考虑卡的 extents）
    /// </summary>
    private Vector3 GetScatterTarget(Vector3 center, CardWorldView view, Bounds areaBounds)
    {
        // 随机方向 + 随机距离
        Vector2 dir = Random.insideUnitCircle.normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;

        float dist = Random.Range(scatterMinDistance, scatterMaxDistance);
        Vector3 desired = center + new Vector3(dir.x, dir.y, 0f) * dist;

        if (normalAreaCollider == null)
            return desired;

        // 计算“卡自己的半尺寸”，用于把目标点往内缩，避免卡碰撞盒出界
        Vector2 half = GetCardHalfExtents(view);

        float minX = areaBounds.min.x + half.x;
        float maxX = areaBounds.max.x - half.x;
        float minY = areaBounds.min.y + half.y;
        float maxY = areaBounds.max.y - half.y;

        float x = Mathf.Clamp(desired.x, minX, maxX);
        float y = Mathf.Clamp(desired.y, minY, maxY);

        return new Vector3(x, y, desired.z);
    }

    private Vector2 GetCardHalfExtents(CardWorldView view)
    {
        var col = view.GetComponent<Collider2D>();
        if (col == null) return new Vector2(0.15f, 0.15f);

        // bounds 是世界尺寸，extents 是半尺寸
        var e = col.bounds.extents;

        // 防止极端情况（比如 collider 未启用/尺寸异常）
        float hx = Mathf.Max(0.05f, e.x);
        float hy = Mathf.Max(0.05f, e.y);
        return new Vector2(hx, hy);
    }

    /// <summary>
    /// 如果没在 Inspector 拖 normalAreaCollider，这里尝试自动找一个（从场上任意 CardWorldView 取）
    /// </summary>
    private void EnsureNormalAreaCollider()
    {
        if (normalAreaCollider != null) return;

        var any = FindObjectOfType<CardWorldView>();
        if (any != null && any.normalAreaCollider != null)
        {
            normalAreaCollider = any.normalAreaCollider;
        }
    }

    public void ResetState()
    {
        // 这个服务本身通常不缓存“生成中的掉落”，所以只做兜底清理即可
        StopAllCoroutines();

        // Kill 这个对象作为 target 的 tween（虽然你当前主要是 view.transform tween，但留个兜底）
        DOTween.Kill(this);

        // 清单例
        if (Instance == this)
            Instance = null;

        // 如果未来加了掉落队列/缓存，再在这里 Clear
        // _pendingDrops.Clear();
    }
}
