using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using CardGame.Gameplay.Units;
using System.Linq;
using CardGame.Gameplay.Cards;
using CardGame.Core.Pause;

/// <summary>
/// 合成区域（多实例版 + 缓存注册表）：
/// - OnEnable/OnDisable 维护静态注册表，避免 FindObjectsOfType
/// - 不再使用物理 Trigger 记录进出
/// - 只用 BoxCollider2D 的 bounds 判断某个位置是否在合成区
/// - 只有点击“合成”按钮时才尝试做合成
/// - 点击“合成”按钮时，动态扫描场景中在合成区域里的卡牌并尝试合成
/// - 堆（stack）不参与合成，只能用单卡做材料
/// </summary>
public class FusionZone : MonoBehaviour, IPausable, IFusionZoneCollapseHandler
{
    // ===== 静态注册表（缓存）=====
    public static readonly List<FusionZone> FusionZoneRegistry = new List<FusionZone>();
    [SerializeField] private FusionResolver resolver;

    [Header("用于定义合成区域范围的 BoxCollider2D（可以禁用，只用它的 bounds）")]
    [SerializeField] private Collider2D fusionAreaCollider;

    private IFusionButtonStateSink buttonSink;

    [Header("合成产物掉落（参考探索区）")]
    [SerializeField] private float dropRadius = 1.6f;

    [Header("合成VFX Pro（黏稠融合）")]
    [SerializeField] private FusionFuseVfxPro fuseVfxPro;

    // pause
    private bool _paused;

    // 合成进行中
    private bool _isFusing;
    private Coroutine _fuseRoutine;
    private float _fuseRemaining;

    // 合成进行中缓存材料/结果
    private List<CardWorldView> _fuseMaterials;
    private CardDefinition _fuseResult;

    // tween group：SetTarget(this) + 按 id 兜底
    private readonly HashSet<string> _activeTweenIds = new HashSet<string>();

    private void OnEnable()
    {
        if (!FusionZoneRegistry.Contains(this))
            FusionZoneRegistry.Add(this);

        RefreshFusionButtonState();
    }

    private void OnDisable()
    {
        FusionZoneRegistry.Remove(this);
    }

    private void Awake()
    {
        // 运行时自动给 fusionAreaCollider 赋值
        if (fusionAreaCollider == null)
            fusionAreaCollider = GetComponentInChildren<Collider2D>(true);

        if (fusionAreaCollider == null)
            Debug.LogError("[FusionZone] 未找到 fusionAreaCollider，请检查合成区 prefab！", this);

        if (resolver == null)
            resolver = FindObjectOfType<FusionResolver>();

        // 自动找 VFX（不强制拖引用）
        if (fuseVfxPro == null)
            fuseVfxPro = GetComponentInChildren<FusionFuseVfxPro>(true);

        // 找子物体上任意实现了 IFusionButtonStateSink 的组件（UI 那边的 FusionAreaFollower）
        var monos = GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < monos.Length; i++)
        {
            if (monos[i] is IFusionButtonStateSink sink)
            {
                buttonSink = sink;
                break;
            }
        }
    }

    private Vector3 GetFusionCenterWorld()
    {
        if (fusionAreaCollider != null) return fusionAreaCollider.bounds.center;

        var col = GetComponentInChildren<Collider2D>(true);
        if (col != null) return col.bounds.center;

        return transform.position;
    }

    /// <summary>
    /// 判断一个世界坐标是否在合成区域内
    /// </summary>
    public bool IsInZone(Vector3 worldPos)
    {
        if (fusionAreaCollider == null)
        {
            //Debug.LogWarning($"[FusionZone IsInZone] FusionCollider=null, worldPos={worldPos}");
            return false;
        }

        bool result = fusionAreaCollider.bounds.Contains(worldPos);
        //Debug.Log($"[FusionZone IsInZone] worldPos={worldPos}, result={result}, " +
        //          $"center={fusionAreaCollider.bounds.center}, size={fusionAreaCollider.bounds.size}");

        return result;
    }

    /// <summary>
    /// 给 UI Button 用的回调。
    /// 在 Inspector 里把 Button.onClick 绑定到这个函数。
    /// </summary>
    public void OnFusionButtonClicked() => TryFusionAll();

    /// <summary>
    /// 尝试在区域内做合成：
    /// - 动态扫描场景中所有 CardWorldView
    /// - 只使用“单卡”的堆根（有 stackMembers>1 的根视为堆，直接跳过）
    /// - 按照配表 CardDatabase.GetFusionResult 找到任意一对可以合成的组合
    /// - 一次点击可以连续做多次合成，直到再也找不到组合
    /// </summary>
    public void TryFusionAll()
    {
        if (resolver == null)
        {
            Debug.LogWarning("FusionZone: FusionResolver 未找到。请在 GameSystems 挂载 FusionResolver。");
            return;
        }

        if (_paused) return;     // 暂停时按合成按钮无效（恢复后需要再按）
        if (_isFusing) return;   // 合成中不重复触发

        var db = CardDatabase.Instance;
        if (db == null)
        {
            Debug.LogWarning("FusionZone: CardDatabase.Instance 未找到，无法合成。");
            return;
        }

        // 收集当前合成区域内的“可用于合成的单卡堆根”
        List<CardWorldView> materials = new List<CardWorldView>();
        HashSet<CardWorldView> rootSet = new HashSet<CardWorldView>();

        var allCards = CardWorldView.CardWorldViewRegistry;
        for (int i = 0; i < allCards.Count; i++)
        {
            var card = allCards[i];
            if (card == null || card.definition == null) continue;

            var root = card.GetStackRoot();
            if (root == null || root.definition == null) continue;

            // 只看根是否在合成区域内
            if (root.currentZone != this) continue;

            // 二次校验：必须真的还在本区（防止状态滞后）
            if (!root.IsFullyInsideAnyZone(out var realZone) || realZone != this)
                continue;

            // 堆不参与合成
            bool isStack = root.stackMembers != null && root.stackMembers.Count > 1;
            if (isStack) continue;

            // 去重
            if (!rootSet.Add(root)) continue;

            materials.Add(root);
        }

        if (materials.Count < 2)
        {
            Debug.Log("FusionZone: 区域内可用于合成的单卡数量不足 2，无法合成。");
            return;
        }

        // 把所有素材的标签 OR 起来，得到一个“总标签”
        CardTag combinedTags = CardTag.None;
        foreach (var m in materials)
        {
            combinedTags |= m.definition.tags;
        }

        // 根据“总标签”在 CardDatabase 里查一条合成规则
        //var resultDef = db.GetFusionResultByTags(combinedTags);
        //if (resultDef == null)
        //{
        //    Debug.Log("FusionZone: 当前区域内的标签组合找不到任何合成规则。");
        //    return;
        //}

        // 一次性执行涌现式合成：所有素材 -> 一张结果卡
        //DoFusionEmergent(materials, resultDef);

        if (!resolver.TryResolve(
                materials.Select(m => m.definition).ToList(),
                out var resultDef,
                out var seconds))
        {
            Debug.Log("FusionZone: 当前组合无合成结果");
            return;
        }

        StartFusion(materials, resultDef, seconds);

        StartCoroutine(RefreshNextFrame());
    }

    /// <summary>
    /// 具体执行一次合成：
    /// - mainRoot 变成 resultDef
    /// - consumedRoot 被销毁并从区域列表中移除
    /// </summary>
    private void DoFusion(CardWorldView mainRoot, CardWorldView consumedRoot, CardDefinition resultDef)
    {
        if (mainRoot == null || consumedRoot == null || resultDef == null)
            return;

        Debug.Log($"融合成功: {mainRoot.definition.displayName} + {consumedRoot.definition.displayName} -> {resultDef.displayName}");

        // mainRoot 变成结果牌
        mainRoot.Setup(resultDef);

        // 消耗的材料从区域中移除并销毁
        Destroy(consumedRoot.gameObject);
    }

    /// <summary>
    /// 涌现式合成：
    /// - materials：这次合成用到的所有单卡（已经保证都在合成区内、且是堆根）
    /// - resultDef：这次涌现出的结果卡
    /// 这里根据材料的数量比例，决定攻击力、元素属性等等。
    /// </summary>
    private void DoFusionEmergent(List<CardWorldView> materials, CardDefinition resultDef)
    {
        if (materials == null || materials.Count == 0 || resultDef == null)
            return;

        // 选列表里的第一张卡作为“结果卡的容器”
        var mainRoot = materials[0];

        // ====== 这里统计“各类素材的数量” ======
        int woodCount = 0;
        int stoneCount = 0;
        // 如果以后有 Metal / Food / Fire 等，也可以在这里一起数
        foreach (var m in materials)
        {
            if (m == null || m.definition == null) continue;

            var t = m.definition.tags;

            if ((t & CardTag.Wood) != 0) woodCount++;
            if ((t & CardTag.Stone) != 0) stoneCount++;
        }

        // ====== 这里做“结果变体 + 数值计算”的逻辑 ======
        // 举例：
        // - 最少需要 1 木 + 1 石才允许合成
        // - 如果木 > 石 → 做成“木斧” / 偏重木的加成
        // - 如果石 > 木 → 做成“石斧” / 偏重石的加成
        //
        // 现在 CardDefinition 里还没有攻击力字段，
        // 可以：
        //   1) 给 CardWorldView 增加一个 runtime 的 attack 字段，
        //   2) 或者挂一个 CardStats 组件，专门存攻防等数值，
        // 在这里根据 woodCount / stoneCount 去设定。
        //
        // 下面先简单 Log 一下，方便你调试：
        Debug.Log($"涌现合成：木={woodCount}，石={stoneCount}，结果卡={resultDef.displayName}");

        // 1) mainRoot 变成结果牌（图片、名字来自 ScriptableObject）
        mainRoot.Setup(resultDef);
        mainRoot.transform.rotation = Quaternion.identity;
        mainRoot.transform.localScale = Vector3.one; // 可选，防止带着缩放

        // TODO: 这里可以根据 woodCount / stoneCount 设置 mainRoot 的攻击力：
        // var stats = mainRoot.GetComponent<CardStats>();
        // if (stats != null)
        // {
        //     float woodFactor  = 1.0f;   // 木对攻击力的权重
        //     float stoneFactor = 2.0f;   // 石对攻击力的权重
        //     stats.attack = Mathf.RoundToInt(woodCount * woodFactor + stoneCount * stoneFactor);
        //     stats.type   = (woodCount >= stoneCount) ? AxeType.WoodAxe : AxeType.StoneAxe;
        // }

        // 2) 其他材料销毁
        for (int i = 1; i < materials.Count; i++)
        {
            var m = materials[i];
            if (m == null) continue;

            Destroy(m.gameObject);
        }

        // 3) 合成完成后：空投到普通区中心范围（不截停）
        var center = CardAirdropUtility.GetNormalAreaCenterFallback();
        var dropPos = CardAirdropUtility.GetRandomDropPos(center, dropRadius, mainRoot.transform.position.z);
        CardAirdropUtility.DropSingle(mainRoot, dropPos);

        //// 3) 参考探索区：空投到普通区中心范围
        //mainRoot.transform.DOKill(false);
        //SetAirDropMode(mainRoot, true);

        //var dropCenter = FindNormalAreaCenterFallback();
        //Vector2 rnd = Random.insideUnitCircle * dropRadius;
        //var tMain = mainRoot.transform;
        //Vector3 dropPos = new Vector3(dropCenter.x + rnd.x, dropCenter.y + rnd.y, tMain.position.z);

        //Sequence seq = DOTween.Sequence();
        //seq.Append(tMain.DOMoveY(tMain.position.y + 0.6f, 0.12f).SetEase(Ease.OutQuad));
        //seq.Append(tMain.DOMove(dropPos, 0.28f).SetEase(Ease.OutQuad));
        //seq.OnComplete(() =>
        //{
        //    if (mainRoot == null) return;

        //    SetAirDropMode(mainRoot, false);

        //    // 落地后：夹回普通区、刷新 zone / UI
        //    mainRoot.ClampStackToNormalArea();
        //    mainRoot.RaiseWorldPositionChanged();
        //});
    }

    // FusionZone.cs
    public bool CanFuseNow()
    {
        var db = CardDatabase.Instance;
        if (db == null) return false;

        List<CardWorldView> materials = new List<CardWorldView>();
        HashSet<CardWorldView> rootSet = new HashSet<CardWorldView>();

        var allCards = CardWorldView.CardWorldViewRegistry;
        for (int i = 0; i < allCards.Count; i++)
        {
            var card = allCards[i];
            if (card == null || card.definition == null) continue;

            var root = card.GetStackRoot();
            if (root == null || root.definition == null) continue;

            if (root.currentZone != this) continue;

            if (!root.IsFullyInsideAnyZone(out var realZone) || realZone != this)
                continue;

            // 堆不参与
            bool isStack = root.stackMembers != null && root.stackMembers.Count > 1;
            if (isStack) continue;

            if (!rootSet.Add(root)) continue;

            materials.Add(root);
        }

        if (materials.Count < 2)
            return false;

        //CardTag combinedTags = CardTag.None;
        //foreach (var m in materials)
        //    combinedTags |= m.definition.tags;

        //return db.GetFusionResultByTags(combinedTags) != null;

        if (_paused) return false;
        if (_isFusing) return false;

        if (resolver == null)
        {
            resolver = FindObjectOfType<FusionResolver>();
            if (resolver == null) return false;
        }

        // 不再用 combinedTags 去查 db（旧逻辑），直接走统一 resolver
        return resolver.TryResolve(
            materials.Select(m => m.definition).ToList(),
            out _,
            out _
        );
    }

    public void RefreshFusionButtonState()
    {
        if (buttonSink == null) return;
        buttonSink.SetCanFuse(CanFuseNow());
    }

    // ===== 合成计时流程（可暂停 / 可取消）=====

    private void StartFusion(List<CardWorldView> materials, CardDefinition resultDef, float seconds)
    {
        if (materials == null || materials.Count < 2 || resultDef == null) return;
        if (_isFusing) return;

        _isFusing = true;
        _fuseMaterials = materials;
        _fuseResult = resultDef;

        _fuseRemaining = Mathf.Max(0f, seconds);

        if (_fuseRoutine != null)
            StopCoroutine(_fuseRoutine);

        // 合成开始：播放黏稠融合演出（计时期间）
        if (fuseVfxPro != null)
        {
            fuseVfxPro.Play(GetFusionCenterWorld(), _fuseMaterials, _fuseRemaining);
        }

        _fuseRoutine = StartCoroutine(CoFuse());
    }

    private IEnumerator CoFuse()
    {
        // 计时（暂停时冻结，不会偷跑，也不会补帧连发）
        while (_isFusing && _fuseRemaining > 0f)
        {
            if (_paused)
            {
                // 暂停冻结
                fuseVfxPro?.Pause();
                yield return null;
                continue;
            }
            else
            {
                // 恢复
                fuseVfxPro?.Resume();
            }

            // 每帧驱动：搅动 + 末段 0.2s smash
            fuseVfxPro?.Tick(_fuseRemaining, GetFusionCenterWorld());

            _fuseRemaining -= Time.deltaTime;
            yield return null;
        }

        if (!_isFusing) yield break;

        // 最终校验：材料是否仍有效且仍在本区
        if (_fuseMaterials == null || _fuseMaterials.Count < 2 || _fuseResult == null)
        {
            CancelFusion(evacuateMaterials: false);
            yield break;
        }

        for (int i = 0; i < _fuseMaterials.Count; i++)
        {
            var m = _fuseMaterials[i];
            if (m == null || m.definition == null)
            {
                CancelFusion(evacuateMaterials: false);
                yield break;
            }

            // 必须仍属于本 FusionZone（防止合成中被拖走）
            if (m.currentZone != this)
            {
                CancelFusion(evacuateMaterials: false);
                yield break;
            }
        }

        // 合成成功：不需要还原（材料即将销毁）
        fuseVfxPro?.Stop(restore: false);

        // 复位结果卡姿态（避免歪）
        if (_fuseMaterials != null && _fuseMaterials.Count > 0 && _fuseMaterials[0] != null)
        {
            _fuseMaterials[0].transform.rotation = Quaternion.identity;
        }

        // 执行涌现式合成（你现有逻辑）
        DoFusionEmergent(_fuseMaterials, _fuseResult);

        // 收尾
        _isFusing = false;
        _fuseRoutine = null;
        _fuseRemaining = 0f;
        _fuseMaterials = null;
        _fuseResult = null;

        StartCoroutine(RefreshNextFrame());
    }

    private void CancelFusion(bool evacuateMaterials)
    {
        _isFusing = false;

        if (_fuseRoutine != null)
        {
            StopCoroutine(_fuseRoutine);
            _fuseRoutine = null;
        }

        // 合成取消：还原位置/缩放/旋转/颜色，并撤销置顶
        fuseVfxPro?.Stop(restore: true);

        _fuseRemaining = 0f;
        _fuseResult = null;

        if (evacuateMaterials && _fuseMaterials != null)
        {
            // 合成取消：材料不销毁，走你已有的撤离逻辑（整堆空投）
            // 先把 zone 归属清掉，避免撤离时还被当作在 FusionZone
            //for (int i = 0; i < _fuseMaterials.Count; i++)
            //{
            //    var m = _fuseMaterials[i];
            //    if (m == null) continue;
            //    m.currentZone = null;
            //}

            EvacuateCardsToNormalArea();
        }

        _fuseMaterials = null;

        RefreshFusionButtonState();
    }


    // ========== 收起合成区时：把区域内卡牌挪回普通区 ==========
    public void EvacuateCardsToNormalArea()
    {
        // 找普通区 Collider（与 CardWorldView 的 tag 方案一致）
        var normalObj = GameObject.FindWithTag("NormalArea");
        if (normalObj == null) return;

        var normalCol = normalObj.GetComponent<Collider2D>();
        if (normalCol == null) return;

        Vector3 dropCenter = normalCol.bounds.center;

        // 收集本 FusionZone 内的“堆根”（去重）
        var allCards = CardWorldView.CardWorldViewRegistry;
        HashSet<CardWorldView> roots = new HashSet<CardWorldView>();

        for (int i = 0; i < allCards.Count; i++)
        {
            var card = allCards[i];
            if (card == null) continue;

            var root = card.GetStackRoot();
            if (root == null) continue;

            // 只处理当前确实属于本 zone 的堆根
            if (root.currentZone != this) continue;

            // 二次校验：仍在本区（避免状态滞后）
            //if (!root.IsFullyInsideAnyZone(out var realZone) || realZone != this)
            //    continue;

            roots.Add(root);
        }

        // 对每个堆根：如果它的 bounds 有部分在普通区外，就整体平移回去
        foreach (var root in roots)
        {
            if (root == null) continue;

            // 收起合成区：先把 zone 归属清掉，避免移动途中仍被当作在 FusionZone
            root.currentZone = null;

            var center = CardAirdropUtility.GetNormalAreaCenterFallback();
            var targetRootPos = CardAirdropUtility.GetRandomDropPos(center, dropRadius, root.transform.position.z);

            // 整堆空投（保持堆形状，且不截停）
            CardAirdropUtility.DropStack(root, targetRootPos);
        }
    }

    // =========================
    // Collapse hook (IFusionZoneCollapseHandler)
    // =========================
    public bool OnBeforeCollapse()
    {
        // 收起前确保停演出（如果正在播放）
        // 1) 如果正在合成：直接取消，并把材料撤离回普通区
        if (_isFusing)
        {
            CancelFusion(evacuateMaterials: true);
        }
        else
        {
            // 2) 就算没在合成，也要把当前区内卡牌清回普通区（避免“收起后还算在区里”的脏状态）
            //  （此时 fuseVfxPro 通常不在播放，但 Stop(true) 也安全）
            fuseVfxPro?.Stop(restore: true);
            EvacuateCardsToNormalArea();
        }

        // 3) 收起允许继续
        return true;
    }

    private IEnumerator RefreshNextFrame()
    {
        yield return null; // 等 Destroy 真正生效
        RefreshFusionButtonState();
    }

    // =========================
    // Pause (IPausable)
    // =========================
    public void Pause()
    {
        _paused = true;

        // 暂停时：按钮立刻刷新为不可合成（防止暂停瞬间还能点）
        RefreshFusionButtonState();
        fuseVfxPro?.Pause();
    }

    public void Resume()
    {
        _paused = false;

        // 恢复时：按钮立刻刷新
        RefreshFusionButtonState();
        fuseVfxPro?.Resume();
    }
}
