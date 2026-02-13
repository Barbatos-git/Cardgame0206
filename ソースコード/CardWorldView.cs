using UnityEngine;
using DG.Tweening;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using System;
using CardGame.Gameplay.Units;
using CardGame.Core.Cards;
using TMPro;
using UnityEngine.Rendering;

[RequireComponent(typeof(SpriteRenderer))]
[RequireComponent(typeof(Collider2D))]
public class CardWorldView : MonoBehaviour, ICardEntity
{
    // ==============================
    // CardWorldView 注册表（缓存）
    // ==============================
    public static readonly List<CardWorldView> CardWorldViewRegistry = new List<CardWorldView>();

    // --- 渲染层级控制：保证拖动中的卡堆始终显示在最前面 ---
    private int _initialSortingOrder = 0;

    [Header("数据")]
    public CardDefinition definition;

    [Header("表现")]
    public SpriteRenderer artworkRenderer;
    public float dragZOffset = 0f; // 卡牌所处的 Z
    public float bounceDistance = 1.0f; // 弹开距离（世界坐标）
    public float bounceDuration = 0.2f; // 弹开时间
    private CardVisualController visual;
    // ===== 装备可视化绑定（人物卡专用） =====
    private CharacterCard _boundCharacter;

    [Header("区域标记")]
    [HideInInspector] public bool isInFusionZone = false;   // 当前是否在合成区域（由位置判断）
    [HideInInspector] public FusionZone currentZone;

    /// <summary>
    /// 世界位置变化事件：任何会“改变位置”的行为最终都要通过 SetWorldPosition / MoveWorldPosition
    /// </summary>
    public event Action<CardWorldView> OnWorldPositionChanged;

    [Header("普通区边界")]
    public Collider2D normalAreaCollider;

    [Header("探索区边界")]
    private static Collider2D s_ExplorationLaneCollider;

    // 记录“开始拖拽时”的位置，用于拖出边界后回弹
    [HideInInspector] public Vector3 originalPosition;

    // 判断是否在堆中 & 堆信息
    [HideInInspector] public CardWorldView stackRoot;          // 这张卡所在堆的“根”（通常是底牌）
    [HideInInspector] public List<CardWorldView> stackMembers; // 只有 root 上有效，成员列表
    [HideInInspector] public Vector3 offsetInStack;           // 在堆内相对 root 的偏移

    private Camera mainCam;
    private bool isDragging = false;
    private Vector3 dragOffset;

    [SerializeField] private bool refreshZoneWhileDragging = false;

    /// <summary>
    /// インスタンス固有ID
    /// </summary>
    public int InstanceId => GetInstanceID();

    /// <summary>
    /// デバッグ表示用名称
    /// </summary>
    public string DebugName
    {
        get
        {
            if (definition != null)
                return $"{definition.name} ({name})";
            return name;
        }
    }

    /// <summary>
    /// 戦闘・演出で使用される Transform
    /// </summary>
    Transform ICardEntity.Transform => this.transform;

    /// <summary>
    /// カード消滅（死亡・破壊）
    /// </summary>
    public void Despawn()
    {
        // 将来对象池可替换
        Destroy(gameObject);
    }

    private void Awake()
    {
        //debugId = ++debugIdCounter;

        if (artworkRenderer == null)
            artworkRenderer = GetComponent<SpriteRenderer>();

        mainCam = Camera.main;

        if (artworkRenderer != null)
            _initialSortingOrder = artworkRenderer.sortingOrder;

        visual = GetComponent<CardVisualController>();

        // 运行时自动给 normalAreaCollider 赋值（如果没手动拖）
        TryAutoAssignNormalAreaCollider();

        ApplyDefinitionVisualsIfReady();
    }

    private void OnEnable()
    {
        // ==============================
        // 注册表维护
        // ==============================
        if (!CardWorldViewRegistry.Contains(this))
            CardWorldViewRegistry.Add(this);

        // ==============================
        // 订阅位置变化事件
        // ==============================
        OnWorldPositionChanged += HandleWorldPosChanged;
    }

    private void OnDisable()
    {
        // 注册表维护
        CardWorldViewRegistry.Remove(this);

        // 反订阅
        OnWorldPositionChanged -= HandleWorldPosChanged;

        // 解除人物装备事件绑定（如果有）
        if (_boundCharacter != null)
            _boundCharacter.OnEquipmentChanged -= RefreshEquipmentVisuals;
        _boundCharacter = null;
    }

    /// <summary>
    /// 位置变化后自动刷新 currentZone / isInFusionZone
    /// </summary>
    private void HandleWorldPosChanged(CardWorldView root)
    {
        if (root == null) return;

        // 记录变化前的 zone
        var oldZone = root.currentZone;

        // 只更新 root（堆的状态以 root 为准）
        if (root.IsFullyInsideAnyZone(out var zone))
            root.currentZone = zone;
        else
            root.currentZone = null;

        root.isInFusionZone = (root.currentZone != null);

        // 同时刷新 old / new
        var newZone = root.currentZone;

        if (oldZone != null)
            oldZone.RefreshFusionButtonState();

        if (newZone != null && newZone != oldZone)
            newZone.RefreshFusionButtonState();
    }

    public void Setup(CardDefinition def)
    {
        definition = def;

        SyncLayerFromDefinition();

        if (definition == null) return;

        if (artworkRenderer != null)
            artworkRenderer.sprite = definition.artwork;

        // 关键：definition 就绪后立刻装身份（避免 Awake 时 definition 还没来）
        var installer = GetComponent<CardBehaviorInstaller>();
        if (installer != null) installer.Install();

        // 自动设置 Layer
        if (definition.tags.HasFlag(CardTag.Character))
        {
            gameObject.layer = LayerMask.NameToLayer("CharacterHit");
        }
        else
        {
            gameObject.layer = LayerMask.NameToLayer("Card");
        }

        ApplyDefinitionVisualsIfReady();

        BindCharacterEquipmentVisuals();

        // 以后要显示名字、描述，可以再挂 TextMeshPro（World Space）
        ApplyNameFromDefinition();
    }

    // 当场景里“预置卡牌”直接给了 definition 时，自动刷新图片/身份/Layer
    private void ApplyDefinitionVisualsIfReady()
    {
        if (definition == null) return;

        // Layer 先同步（防止鼠标交互层不对）
        SyncLayerFromDefinition();

        // Sprite 同步
        if (artworkRenderer != null && definition.artwork != null)
        {
            artworkRenderer.sprite = definition.artwork;
            artworkRenderer.enabled = true;
        }

        // 身份安装（人物/敌人/武器等）
        var installer = GetComponent<CardBehaviorInstaller>();
        if (installer != null) installer.Install();

        BindCharacterEquipmentVisuals();

        // 兜底：确保可点击
        var col = GetComponent<Collider2D>();
        if (col != null) col.enabled = true;

        // 以后要显示名字、描述，可以再挂 TextMeshPro（World Space）
        ApplyNameFromDefinition();
    }

    private void ApplyNameFromDefinition()
    {
        if (definition == null) return;

        var visual = GetComponent<CardVisualController>();
        if (visual == null) return;

        visual.SetNameText(definition.displayName);
    }


    private void SyncLayerFromDefinition()
    {
        if (definition == null) return;

        int charLayer = LayerMask.NameToLayer("CharacterHit");
        int cardLayer = LayerMask.NameToLayer("Card");

        if (definition.tags.HasFlag(CardTag.Character))
        {
            if (charLayer >= 0) gameObject.layer = charLayer;
        }
        else
        {
            if (cardLayer >= 0) gameObject.layer = cardLayer;
        }
    }

    // ------------ 拖拽逻辑（OnMouse 系列，简单好用） ------------

    private void OnMouseDown()
    {
        if (InputGuard.PointerOnUI()) return;

        var root = GetStackRoot();

        // 敌人卡：禁止玩家拖拽
        if (!IsDraggableByPlayer(root))
            return;

        // ==============================
        // Ctrl + Click => Split 1 card from stack and start drag
        // ==============================
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
        {
            // this = 被点击到的那张（可能是顶/底）
            if (TrySplitOneCardAndBeginDrag(this))
                return; // 阻止走“拖整堆”的逻辑
        }

        if (root.mainCam == null) root.mainCam = Camera.main;

        root.isDragging = true;
        Vector3 worldMouse = root.GetMouseWorldPos();
        root.dragOffset = root.transform.position - worldMouse;

        // 把整堆卡牌的 sortingOrder 提升到全局最前面
        root.PushStackOverlay("drag");

        // 记录整个堆在“开始拖拽”时的位置，用于边界回弹
        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            c.originalPosition = c.transform.position;
            // 停止堆内所有 tween
            c.transform.DOComplete();
        }
    }

    private void OnMouseDrag()
    {
        if (InputGuard.PointerOnUI()) return;

        var root = GetStackRoot();

        if (!root.isDragging || root.mainCam == null) return;

        Vector3 worldMouse = root.GetMouseWorldPos();
        Vector3 newRootPos = worldMouse + root.dragOffset;

        // 整堆移动：根的位置 + 各成员自己的 offsetInStack
        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            c.transform.position = newRootPos + c.offsetInStack;
        }

        // 拖拽时实时刷新 currentZone/isInFusionZone（默认关闭）
        if (refreshZoneWhileDragging)
        {
            root.RaiseWorldPositionChanged();
        }
    }

    private void OnMouseUp()
    {
        if (InputGuard.PointerOnUI()) return;

        var root = GetStackRoot();

        if (!root.isDragging)
        {
            return;
        }

        root.isDragging = false;

        // 松手立刻回退渲染层级（避免越拖越高）
        root.PopStackOverlay("drag");

        // 实时根据位置判断是否在合成区域（不再依赖 Trigger）
        // 松手时先刷新一次（保证 isInFusionZone/currentZone 使用的是最新位置）
        root.RaiseWorldPositionChanged();

        // 如果在合成区域：松手后什么都不做，等玩家点“合成”按钮
        if (root.isInFusionZone)
        {
            return;
        }

        // 探索区规则（在普通区检查之前）
        if (IsInsideExplorationLane(root))
        {
            if (!IsCharacterCard(root))
            {
                // 非人物卡：像移出区域一样回弹
                ReturnStackToOriginalPositions(root);
                return;
            }

            // 交给 ExplorationZone 判定是否超限
            var zone = CardGame.Gameplay.Zones.ExplorationZone.Instance;
            if (zone == null || !zone.TryAcceptCharacter(root))
            {
                // 超限或未找到探索区：回弹
                ReturnStackToOriginalPositions(root);
                return;
            }

            // 人物卡：允许放置，探索区内部会自动 Snap 排队
            return;
        }

        // 不在合成区 → 检查是否在普通区内
        if (!IsInsideNormalArea(root))
        {
            // 如果拖出了普通区边界，则整堆回到“拖拽开始时”的位置
            ReturnStackToOriginalPositions(root);
            return;
        }

        // 普通区域：做堆叠 / 弹开（不再做合成）
        root.TryFusionWithNearby(this);
    }

    private Vector3 GetMouseWorldPos()
    {
        Vector3 screenPos = Input.mousePosition;

        // z = 相机到卡牌的距离
        float z = Mathf.Abs(mainCam.transform.position.z - transform.position.z);
        screenPos.z = z;

        return mainCam.ScreenToWorldPoint(screenPos);
    }

    private void TryAutoAssignNormalAreaCollider()
    {
        if (normalAreaCollider != null) return;

        // 用 tag 找普通区 GameObject
        GameObject areaObj = GameObject.FindWithTag("NormalArea");
        if (areaObj != null)
        {
            normalAreaCollider = areaObj.GetComponent<Collider2D>();
        }
    }

    /// <summary>
    /// 判断 root 整堆是否完全位于某个 FusionZone 内（中心+四角采样）
    /// 若是，返回 true 并输出命中的 zone；否则 false 且 zone=null
    /// </summary>
    public bool IsFullyInsideAnyZone(out FusionZone hitZone)
    {
        hitZone = null;

        // 用缓存注册表，不用 FindObjectsOfType
        var zones = FusionZone.FusionZoneRegistry;
        if (zones == null || zones.Count == 0)
            return false;

        var root = GetStackRoot();
        var stackBounds = GetStackWorldBounds(root);

        Vector3[] samples =
        {
        stackBounds.center,
        new Vector3(stackBounds.min.x, stackBounds.min.y, stackBounds.center.z),
        new Vector3(stackBounds.min.x, stackBounds.max.y, stackBounds.center.z),
        new Vector3(stackBounds.max.x, stackBounds.min.y, stackBounds.center.z),
        new Vector3(stackBounds.max.x, stackBounds.max.y, stackBounds.center.z),
    };

        // 找到一个能包含所有采样点的 zone
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z == null) continue;

            bool inside = true;
            for (int s = 0; s < samples.Length; s++)
            {
                if (!z.IsInZone(samples[s]))
                {
                    inside = false;
                    break;
                }
            }

            if (inside)
            {
                hitZone = z;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 使用「整堆的 Bounds」判断是否在“普通区”边界内
    /// 用卡牌自身Collider的包围盒来判断
    /// </summary>
    private bool IsInsideNormalArea(CardWorldView root)
    {
        // 再尝试一次自动获取（比如普通区稍后才被生成）
        TryAutoAssignNormalAreaCollider();

        // 没有指定边界 collider，就视为“无限大区域”，不做限制
        if (normalAreaCollider == null)
            return true;

        var areaBounds = normalAreaCollider.bounds;
        var stackBounds = GetStackWorldBounds(root);

        // 只比较 X/Y 平面上的包围关系：整堆完全在区域矩形内才算 inside
        bool inside =
            stackBounds.min.x >= areaBounds.min.x &&
            stackBounds.max.x <= areaBounds.max.x &&
            stackBounds.min.y >= areaBounds.min.y &&
            stackBounds.max.y <= areaBounds.max.y;

        return inside;
    }

    // ============================
    // 统一的事件触发入口
    // ============================
    public void RaiseWorldPositionChanged()
    {
        // 统一以 root 为准
        var root = GetStackRoot();

        // 原有内部事件（内部调用）
        OnWorldPositionChanged?.Invoke(root);

        // Core 级事件（外部系统监听用）
        if (root != null)
            WorldEvents.RaiseStackRootWorldPositionChanged(root.gameObject.GetInstanceID(), root.transform.position);

        //OnWorldPositionChanged?.Invoke(GetStackRoot());
    }

    /// <summary>
    /// 唯一的“瞬移”入口（不带动画）
    /// </summary>
    public void SetWorldPosition(Vector3 pos)
    {
        transform.position = pos;
        RaiseWorldPositionChanged();
    }

    /// <summary>
    /// 如果需要动画移动（例如回弹/吸附），用这个入口
    /// 可以用协程/Lerp 或 DOTween，最终在动画结束时触发事件
    /// </summary>
    public void MoveWorldPosition(Vector3 pos, float duration)
    {
        // 直接给 tween
        transform.DOMove(pos, duration)
            .OnComplete(() =>
            {
                // 统一硬约束：任何“主动移动”结束后都夹回普通区
                // 说明：攻击动画是直接操作 Transform，不走 MoveWorldPosition，所以不会被这里影响
                ClampStackToNormalArea();

                // 夹回后刷新 zone / fusion button
                RaiseWorldPositionChanged();
            });
    }

    // 整堆回到原位置（使用 DOTween）
    private void ReturnStackToOriginalPositions(CardWorldView root)
    {
        // 动画结束后触发一次刷新（避免 currentZone 滞后）
        int alive = 0;
        Tween lastTween = null;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            alive++;

            c.transform.DOComplete();
            var t = c.transform.DOMove(c.originalPosition, bounceDuration)
                       .SetEase(Ease.OutQuad);
            lastTween = t;
        }

        // ==============================
        // 动画结束刷新一次，避免 currentZone 滞后
        // ==============================
        if (alive > 0 && lastTween != null)
        {
            lastTween.OnComplete(() =>
            {
                // 回弹结束也夹回普通区（防止边界抖动/堆偏移）
                root.ClampStackToNormalArea(); 
                // 以 root 为准刷新一次
                root.RaiseWorldPositionChanged();
            });
        }

    }

    public void ClampStackToNormalArea()
    {
        var root = GetStackRoot();
        if (root == null) return;

        var area = root.normalAreaCollider;
        if (area == null) return;

        Bounds areaBounds = area.bounds;

        // 计算整堆 bounds（用 Collider2D）
        bool has = false;
        Bounds stackBounds = new Bounds(root.transform.position, Vector3.zero);

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            var col = c.GetComponent<Collider2D>();
            if (col == null || !col.enabled) continue;

            if (!has) { stackBounds = col.bounds; has = true; }
            else stackBounds.Encapsulate(col.bounds);
        }

        if (!has) return;

        float dx = 0f, dy = 0f;

        if (stackBounds.min.x < areaBounds.min.x) dx = areaBounds.min.x - stackBounds.min.x;
        else if (stackBounds.max.x > areaBounds.max.x) dx = areaBounds.max.x - stackBounds.max.x;

        if (stackBounds.min.y < areaBounds.min.y) dy = areaBounds.min.y - stackBounds.min.y;
        else if (stackBounds.max.y > areaBounds.max.y) dy = areaBounds.max.y - stackBounds.max.y;

        Vector3 delta = new Vector3(dx, dy, 0f);
        if (delta.sqrMagnitude < 0.0000001f) return;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            c.transform.position += delta;
        }

        // 位置变了，刷新 zone 标记/排序等
        root.RaiseWorldPositionChanged();
    }


    // ------------ 堆叠 + 弹开逻辑 ------------

    private void TryFusionWithNearby(CardWorldView draggedCard)
    {
        // 以“堆根”为中心做检测
        CardWorldView thisRoot = GetStackRoot();
        Vector3 centerPos = thisRoot.transform.position;

        // 在当前位置附近做一个小范围的检测，看有没有其他卡牌
        float radius = 0.5f; // 检测半径，可调
        Collider2D[] hits = Physics2D.OverlapCircleAll(centerPos, radius);

        CardWorldView targetRoot = null;
        foreach (var hit in hits)
        {
            if (hit == null) continue;

            var other = hit.GetComponent<CardWorldView>();
            if (other == null) continue;

            // 把对方也提升到“堆根”
            var otherRoot = other.GetStackRoot();
            // 同一个堆里的卡全部跳过
            if (otherRoot == thisRoot) continue;

            targetRoot = otherRoot;
            break;
        }

        if (targetRoot == null)
        {
            // 附近没有卡牌，不做事
            return;
        }

        // ====== 装备交互：优先于堆叠判断（否则会被堆叠规则吞掉） ======
        if (TryHandleEquip(thisRoot, targetRoot))
        {
            thisRoot.RaiseWorldPositionChanged();
            return;
        }

        // 用堆根的 definition 来判断是否可以堆叠
        StackRulesManager.StackRuleEntry stackRule = null;
        bool canStack = false;
        if (StackRulesManager.Instance != null)
        {
            canStack = StackRulesManager.Instance.CanStack(
                thisRoot.definition,
                targetRoot.definition,
                out stackRule
            );
        }

        if (canStack)
        {
            // 用堆根做堆叠运算，这样往已有堆里加第三张时会把整块区域内
            // 同 definition 的卡全部重新整理成一个堆
            ApplyImmediateStack(thisRoot, targetRoot, stackRule, draggedCard);
            // 堆叠后，root 的位置/成员位置变化了，刷新一次
            thisRoot.RaiseWorldPositionChanged();
            return;
        }

        // ====== 新增：装备交互特判（不破坏原堆叠逻辑） ======
        if (TryHandleEquip(thisRoot, targetRoot))
        {
            // 装备成功后，位置/堆结构已经发生变化，刷新一次
            thisRoot.RaiseWorldPositionChanged();
            return;
        }


        // 既不能堆叠 → 弹开
        BounceApart(thisRoot, targetRoot);
    }

    /// <summary>
    /// 立即堆叠：同一小范围内所有相同 definition 的卡变成一堆
    /// - 只显示底牌 + 顶牌
    /// - 底牌右下角显示数量
    /// - 不改变网格（G 键只是整理位置）
    /// </summary>
    private void ApplyImmediateStack(CardWorldView a, CardWorldView b, StackRulesManager.StackRuleEntry rule, CardWorldView draggedCard)
    {
        if (a == null || b == null) return;
        if (a.definition == null || b.definition == null) return;

        // 统一以“堆根”进行合并
        var aRoot = a.GetStackRoot();
        var bRoot = b.GetStackRoot();
        if (aRoot == null || bRoot == null) return;

        // 只允许同 definition 堆叠
        if (aRoot.definition != bRoot.definition) return;

        // 以目标卡位置为堆的中心，你也可以用两者中点
        Vector3 center = b.transform.position;

        // ==============================
        // 1) 收集候选：优先用 stackMembers（不依赖 collider）
        // ==============================
        var stackSet = new HashSet<CardWorldView>();

        void AddWholeStack(CardWorldView root)
        {
            if (root == null) return;

            if (root.stackMembers != null && root.stackMembers.Count > 0)
            {
                for (int i = 0; i < root.stackMembers.Count; i++)
                {
                    var c = root.stackMembers[i];
                    if (c != null) stackSet.Add(c);
                }
            }
            else
            {
                stackSet.Add(root);
            }
        }

        // 确保把 a 和 b 加进去（防止极端情况）
        AddWholeStack(aRoot);
        AddWholeStack(bRoot);

        // 2) 再补充附近散卡（这一步才用物理检测）
        float searchRadius = 0.6f;
        var hits = Physics2D.OverlapCircleAll(center, searchRadius);
        foreach (var hit in hits)
        {
            if (hit == null) continue;
            var card = hit.GetComponent<CardWorldView>();
            if (card == null) continue;
            if (card.definition == null) continue;

            // 只收同 definition
            if (card.definition == aRoot.definition)
            {
                // 若它本身属于某个堆，把那个堆也整体加入（仍然不依赖 collider）
                AddWholeStack(card.GetStackRoot());
            }
        }

        // 少于 2 张就不算堆（理论上不会）
        if (stackSet.Count < 2) return;

        // ==============================
        // 堆叠上限（99）
        // ==============================
        int maxAllowed = (rule != null) ? Mathf.Max(1, rule.maxStackSize) : 99; // rule=null 时也给个兜底
        maxAllowed = Mathf.Min(maxAllowed, 99);

        // ==============================
        // 3) 选 root：优先用 bRoot（更符合“拖到谁身上谁当底”）
        // ==============================
        // 稳定排序，选一个“底牌”
        CardWorldView root = bRoot;

        // 选一个“顶牌”：优先使用本次被拖动的那张卡
        CardWorldView top = (draggedCard != null && stackSet.Contains(draggedCard)) ? draggedCard : null;

        if (top == null)
        {
            // 否则用 aRoot（拖动方）当顶牌更自然
            top = aRoot;
        }
        if (!stackSet.Contains(top)) top = root;

        // 重新排一个有序列表：底牌 -> 中间牌们 -> 顶牌
        var others = new List<CardWorldView>(stackSet);
        others.Sort((x, y) => x.InstanceId.CompareTo(y.InstanceId));

        // 移除 root/top 以便重排
        others.Remove(root);
        others.Remove(top);

        var ordered = new List<CardWorldView>(stackSet.Count);
        ordered.Add(root);
        ordered.AddRange(others);
        if (top != root) ordered.Add(top);

        // ==============================
        // 这里做“真正的上限截断”
        // - 显示数量和逻辑成员一致（最多 99）
        // - 超过上限的卡不并入该堆（仍留在场上）
        // ==============================
        if (ordered.Count > maxAllowed)
        {
            // 超出的部分保持散卡：解除堆结构并稍微弹开
            for (int i = maxAllowed; i < ordered.Count; i++)
            {
                var ex = ordered[i];
                if (ex == null) continue;

                ex.stackRoot = null;
                ex.stackMembers = null;
                ex.offsetInStack = Vector3.zero;

                Vector2 rnd = UnityEngine.Random.insideUnitCircle * 0.25f;
                ex.transform.position = center + (Vector3)rnd;

                var dispEx = ex.GetComponent<CardStackDisplay>();
                if (dispEx != null)
                {
                    dispEx.ShowAsBottom(ex.transform.position);
                    dispEx.SetCount(0);
                }

                // 确保 collider 恢复，避免“拖不动”
                var col = ex.GetComponent<Collider2D>();
                if (col != null) col.enabled = true;
            }

            ordered.RemoveRange(maxAllowed, ordered.Count - maxAllowed);
        }

        // 真实数量（本堆最终成员数量）用于显示：最多 99
        int actualCount = ordered.Count;

        // ==============================
        // 4) 写回堆结构
        // 根负责保存成员列表
        // ==============================
        root.stackMembers ??= new List<CardWorldView>();
        root.stackMembers.Clear();
        root.stackMembers.AddRange(ordered);
        // 自己是根
        root.stackRoot = root;
        root.offsetInStack = Vector3.zero;

        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            if (c == null) continue;

            c.stackRoot = root;

            if (c != root) c.stackMembers = null;

            // 确保中间牌隐藏时 collider 状态由显示逻辑控制
            // 这里先统一开，随后 Show/Hide 会再调整
            var col = c.GetComponent<Collider2D>();
            if (col != null) col.enabled = true;
        }

        // ==============================
        // 5) 统一刷新视觉：底牌、顶牌、中间牌
        // - 规则：数量显示在“顶牌”上
        // ==============================
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            if (c == null) continue;

            c.transform.DOComplete();

            var disp = c.GetComponent<CardStackDisplay>();
            if (disp == null) continue;

            bool isBottom = (i == 0);
            bool isTop = (i == ordered.Count - 1);

            if (isBottom)
            {
                // 底牌动画
                disp.AnimateToBottom(center);
                // 底牌：在中心，显示数量
                c.transform.position = center;
                c.offsetInStack = Vector3.zero;
                disp.ShowAsBottom(center);
                disp.SetCount(0);
            }
            else if (isTop)
            {
                // 顶牌动画
                disp.AnimateToTop(center);
                // 顶牌：右上偏一点，不显示数量
                c.offsetInStack = new Vector3(
                    disp.topOffset.x,
                    disp.topOffset.y,
                    0f
                );
                c.transform.position = center + c.offsetInStack;

                disp.ShowAsTop(center);
                disp.SetCount(actualCount);

                if (c == draggedCard)
                {
                    c.transform.DOPunchScale(Vector3.one * 0.08f, 0.15f, 8, 1f);
                }
            }
            else
            {
                // 中间牌动画（移到中间并隐藏）
                disp.AnimateToMiddle(center);
                // 中间牌：隐藏，只在逻辑上存在
                c.transform.position = center;
                c.offsetInStack = Vector3.zero;
                disp.HideInStack(center);
            }
        }

        // 堆叠完成后，立刻把静态排序固定住，避免回退/偶发顺序造成错乱
        ApplyStackSorting(root, root._initialSortingOrder);
    }

    private void BounceApart(CardWorldView a, CardWorldView b)
    {
        // 统一用“堆根”来计算 & 移动
        CardWorldView rootA = a.GetStackRoot();
        CardWorldView rootB = b.GetStackRoot();

        Vector3 posA = rootA.transform.position;
        Vector3 posB = rootB.transform.position;

        Vector3 dir = posA - posB;

        // 避免零向量
        if (dir.sqrMagnitude < 0.0001f)
            dir = UnityEngine.Random.insideUnitCircle;

        dir.Normalize();

        Vector3 targetA = posA + dir * bounceDistance;
        Vector3 targetB = posB - dir * bounceDistance;

        // 把弹开的目标位置 clamp 在普通区域内，防止被弹出区域
        targetA = ClampInsideNormalArea(rootA, targetA);
        targetB = ClampInsideNormalArea(rootB, targetB);

        // 整堆移动：每个堆都用同一个 delta
        MoveStackWithDelta(rootA, targetA - posA);
        MoveStackWithDelta(rootB, targetB - posB);
    }

    /// <summary>
    /// 计算 root 整堆在普通区域内的一个合法位置，
    /// 确保这张卡的 Collider 不会被弹到区域外。
    /// </summary>
    private Vector3 ClampInsideNormalArea(CardWorldView root, Vector3 desiredRootPos)
    {
        if (root == null) return desiredRootPos;

        TryAutoAssignNormalAreaCollider();
        if (normalAreaCollider == null)
            return desiredRootPos;

        var areaBounds = normalAreaCollider.bounds;
        var stackBounds = GetStackWorldBounds(root);

        // 当前 root 位置
        Vector3 currentRootPos = root.transform.position;
        // 想移动的 delta
        Vector3 delta = desiredRootPos - currentRootPos;

        // 移动后的整堆 bounds
        Bounds moved = new Bounds(stackBounds.center + (Vector3)delta, stackBounds.size);

        float fixX = 0f;
        float fixY = 0f;

        if (moved.min.x < areaBounds.min.x)
            fixX = areaBounds.min.x - moved.min.x;
        else if (moved.max.x > areaBounds.max.x)
            fixX = areaBounds.max.x - moved.max.x;

        if (moved.min.y < areaBounds.min.y)
            fixY = areaBounds.min.y - moved.min.y;
        else if (moved.max.y > areaBounds.max.y)
            fixY = areaBounds.max.y - moved.max.y;

        Vector3 finalDelta = delta + new Vector3(fixX, fixY, 0f);
        Vector3 finalPos = currentRootPos + finalDelta;
        finalPos.z = desiredRootPos.z;

        return finalPos;
    }

    /// <summary>
    /// 以 root 为堆根，整堆平移 delta（带 DOTween）
    /// </summary>
    private void MoveStackWithDelta(CardWorldView root, Vector3 delta)
    {
        if (root == null) return;

        int alive = 0;
        Tween lastTween = null;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;

            Vector3 current = c.transform.position;
            Vector3 target = current + delta;

            c.transform.DOComplete();
            var t = c.transform.DOMove(target, bounceDuration)
                       .SetEase(Ease.OutQuad);
            lastTween = t;
            alive++;
        }

        // 动画结束后刷新一次（关键）
        if (alive > 0 && lastTween != null)
        {
            lastTween.OnComplete(() =>
            {
                root.RaiseWorldPositionChanged();
            });
        }
    }

    // ===== 统一的“静态堆排序”规则：底 < 顶 =====
    private static void ApplyStackSorting(CardWorldView root, int baseOrder)
    {
        if (root == null) return;

        // 单卡
        if (root.stackMembers == null || root.stackMembers.Count == 0)
        {
            root.visual ??= root.GetComponent<CardVisualController>();
            root.visual?.SetStackOrder(baseOrder);
            return;
        }

        // 约定：stackMembers[0]=底，Last=顶
        for (int i = 0; i < root.stackMembers.Count; i++)
        {
            var c = root.stackMembers[i];
            if (c == null) continue;

            c.visual ??= c.GetComponent<CardVisualController>();
            c.visual?.SetStackOrder(baseOrder + i);
        }
    }

    private void PushStackOverlay(string key)
    {
        var root = GetStackRoot();
        if (root == null) return;

        int baseOrder = CardVisualController.NextFrontBase();

        int index = 0;
        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            c.visual ??= c.GetComponent<CardVisualController>();
            c.visual?.PushOverlay(key, baseOrder + index);
            index++;
        }
    }

    private void PopStackOverlay(string key)
    {
        var root = GetStackRoot();
        if (root == null) return;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            c.visual ??= c.GetComponent<CardVisualController>();
            c.visual?.PopOverlay(key);
        }
    }

    // =======================================================
    // Ctrl+Click Split
    // =======================================================

    /// <summary>
    /// Ctrl+点击堆：从堆中分离一张卡出来，并立刻开始拖拽这张单卡。
    /// 分离规则：
    /// - 永远分离顶牌
    /// </summary>
    private bool TrySplitOneCardAndBeginDrag(CardWorldView clicked)
    {
        if (clicked == null) return false;

        var root = clicked.GetStackRoot();
        if (root == null) return false;

        // 单卡/无堆结构 => 不需要分
        if (root.stackMembers == null || root.stackMembers.Count <= 1)
            return false;

        // 选要分离的那一张：优先点击到的成员，否则顶牌
        var extracted = ChooseExtractedCard(root, clicked);
        if (extracted == null) return false;

        // 安全移除（不销毁，仅拆堆）
        DetachFromStackWithoutDestroy(extracted);

        // 拆完后，刷新剩余堆（底/中/顶显示、数量、排序）
        RefreshStackVisualAfterSplit(root);

        // 被拆出来的单卡 => 清理为“单堆状态”
        extracted.stackRoot = null;
        extracted.stackMembers = null;
        extracted.offsetInStack = Vector3.zero;

        // 确保显示为“单卡”（避免它之前是 middle 被隐藏）
        var disp = extracted.GetComponent<CardStackDisplay>();
        if (disp != null)
        {
            // 单卡：当作底牌显示即可（你这里底牌就是正常显示）
            disp.ShowAsBottom(extracted.transform.position);
            disp.SetCount(0);
        }

        // ===== 抽出动画（很短），然后立即开始拖拽 =====
        var cam = extracted.mainCam != null ? extracted.mainCam : Camera.main;
        if (cam != null)
        {
            Vector3 mouse = extracted.GetMouseWorldPos();    // 鼠标世界坐标
            Vector3 start = root.transform.position;         // 从堆中心抽出
            Vector3 target = mouse;                          // 朝鼠标方向

            extracted.transform.position = start;            // 从堆里“弹出来”
            extracted.transform.DOComplete();
            extracted.transform.DOMove(target, 0.10f).SetEase(Ease.OutQuad);
        }

        // 立刻进入拖拽（拖这张单卡）
        BeginDragAsRoot(extracted);

        return true;
    }

    /// <summary>
    /// 选择要分离的卡：
    /// - 如果 clicked 属于这个堆，并且不是 root，则分离 clicked（通常是顶牌）
    /// - 否则分离顶牌（stackMembers 最后一个）
    /// </summary>
    private static CardWorldView ChooseExtractedCard(CardWorldView root, CardWorldView clicked)
    {
        if (root == null) return null;

        // root.stackMembers 约定：0=底牌，Last=顶牌（你堆叠时就是这么构造的）
        var members = root.stackMembers;
        if (members == null || members.Count == 0) return null;

        // 默认：顶牌
        return members[members.Count - 1];
    }

    /// <summary>
    /// 从堆结构中移除一张卡，但不 Destroy（用于拆堆）
    /// </summary>
    private void DetachFromStackWithoutDestroy(CardWorldView card)
    {
        if (card == null) return;

        var root = card.GetStackRoot();
        if (root == null) return;

        // 不在堆
        if (root.stackMembers == null || root.stackMembers.Count == 0)
            return;

        root.stackMembers.Remove(card);

        // 如果堆只剩 1 张：彻底解除堆状态
        if (root.stackMembers.Count == 1)
        {
            var last = root.stackMembers[0];

            // 最后这张变成单卡
            last.stackRoot = null;
            last.stackMembers = null;
            last.offsetInStack = Vector3.zero;

            // 关键：root 自己也必须清理
            if (root != last)
            {
                root.stackRoot = null;
                root.stackMembers = null;
                root.offsetInStack = Vector3.zero;
            }
            else
            {
                root.stackMembers = null;
            }
        }
    }

    /// <summary>
    /// 拆堆后刷新“剩余堆”的显示：
    /// - 底牌显示
    /// - 顶牌显示 + 数量
    /// - 中间牌隐藏
    /// 同时重新应用稳定排序
    /// </summary>
    private void RefreshStackVisualAfterSplit(CardWorldView root)
    {
        if (root == null) return;

        // 已经变成单卡
        if (root.stackMembers == null || root.stackMembers.Count == 0)
        {
            var disp0 = root.GetComponent<CardStackDisplay>();
            if (disp0 != null)
            {
                disp0.ShowAsBottom(root.transform.position);
                disp0.SetCount(0);
            }
            return;
        }

        // 重新确保 root 是底牌：堆规则里我们约定 stackMembers[0] 是底牌
        // 如果 root 不在 [0]，把它换到 [0]
        if (root.stackMembers[0] != root)
        {
            int idx = root.stackMembers.IndexOf(root);
            if (idx >= 0)
            {
                root.stackMembers.RemoveAt(idx);
                root.stackMembers.Insert(0, root);
            }
        }

        int totalCount = root.stackMembers.Count;
        Vector3 center = root.transform.position;

        // 让所有成员 stackRoot 指向 root
        for (int i = 0; i < root.stackMembers.Count; i++)
        {
            var c = root.stackMembers[i];
            if (c == null) continue;

            c.stackRoot = root;

            // 非根不维护成员列表
            if (c != root) c.stackMembers = null;
        }

        // 刷新视觉：底/中/顶
        for (int i = 0; i < root.stackMembers.Count; i++)
        {
            var c = root.stackMembers[i];
            if (c == null) continue;

            c.transform.DOComplete();

            var disp = c.GetComponent<CardStackDisplay>();
            if (disp == null) continue;

            bool isBottom = (i == 0);
            bool isTop = (i == root.stackMembers.Count - 1);

            if (isBottom)
            {
                c.offsetInStack = Vector3.zero;
                disp.ShowAsBottom(center);
                disp.SetCount(0); // 数量显示放在顶牌上（保持你原来规则）
            }
            else if (isTop)
            {
                c.offsetInStack = new Vector3(disp.topOffset.x, disp.topOffset.y, 0f);
                disp.ShowAsTop(center);
                disp.SetCount(totalCount);
            }
            else
            {
                c.offsetInStack = Vector3.zero;
                disp.HideInStack(center);
            }

            // 把 Transform 位置同步到 offset（避免视觉/碰撞不一致）
            c.transform.position = center + c.offsetInStack;
        }

        // 排序稳定化：用 root 当前排序作为 base
        int baseOrder = (root.artworkRenderer != null) ? root.artworkRenderer.sortingOrder : 0;
        ApplyStackSorting(root, baseOrder);

        // 兜底：夹回普通区并刷新 zone
        root.ClampStackToNormalArea();
    }

    /// <summary>
    /// 把某张卡当“堆根”一样开始拖拽（单卡也当单堆）
    /// </summary>
    private void BeginDragAsRoot(CardWorldView newRoot)
    {
        if (newRoot == null) return;

        if (newRoot.mainCam == null) newRoot.mainCam = Camera.main;

        newRoot.isDragging = true;
        Vector3 worldMouse = newRoot.GetMouseWorldPos();
        newRoot.dragOffset = newRoot.transform.position - worldMouse;

        // 提升渲染层级（单卡也用同一套）
        //BringStackToFront(newRoot);
        newRoot.PushStackOverlay("drag");

        // 记录开始拖拽位置（用于拖出普通区回弹）
        foreach (var c in newRoot.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            c.originalPosition = c.transform.position;
            c.transform.DOComplete();
        }
    }



    // 调试：在 Scene 视图里画出检测范围
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, 0.5f);
    }

    public CardWorldView GetStackRoot()
    {
        // 不在堆 → 自己就是根
        if (stackRoot == null) return this;
        return stackRoot;
    }

    /// <summary>
    /// 以当前卡所在的整堆为单位，计算所有「启用的 Collider2D」的包围盒并返回。
    /// 如果什么 Collider 都没有，就用 root 的位置生成一个 0 大小的 bounds。
    /// </summary>
    private Bounds GetStackWorldBounds(CardWorldView root = null)
    {
        if (root == null)
            root = GetStackRoot();

        bool hasBounds = false;
        Bounds result = new Bounds(Vector3.zero, Vector3.zero);

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;

            var col = c.GetComponent<Collider2D>();
            if (col == null || !col.enabled) continue;

            if (!hasBounds)
            {
                result = col.bounds;
                hasBounds = true;
            }
            else
            {
                result.Encapsulate(col.bounds);
            }
        }

        // 应急：整堆都没有 Collider（理论上不会）
        if (!hasBounds)
        {
            result = new Bounds(root.transform.position, Vector3.zero);
        }

        return result;
    }


    public IEnumerable<CardWorldView> GetStackMembersIncludingSelf()
    {
        var root = GetStackRoot();

        // 根没有 stackMembers = 单卡
        if (root.stackMembers == null || root.stackMembers.Count == 0)
        {
            yield return root;
            yield break;
        }

        foreach (var c in root.stackMembers)
        {
            if (c != null)
                yield return c;
        }
    }

    public int GetStackCount()
    {
        var root = GetStackRoot();
        if (root == null) return 1;
        if (root.stackMembers == null || root.stackMembers.Count == 0) return 1;
        return root.stackMembers.Count;
    }

    /// <summary>
    /// 特殊交互：武器卡 → 人物卡 = 装备
    /// 返回 true 表示已处理（不再继续堆叠/弹开）
    /// </summary>
    private bool TryHandleEquip(CardWorldView a, CardWorldView b)
    {
        if (a == null || b == null) return false;
        if (a.definition == null || b.definition == null) return false;

        // A 是武器，B 是人物
        if (IsWeaponToCharacter(a, b))
            return EquipAndConsume(a, b);

        // 反方向：B 是武器，A 是人物
        if (IsWeaponToCharacter(b, a))
            return EquipAndConsume(b, a);

        return false;
    }

    private bool IsWeaponToCharacter(CardWorldView weapon, CardWorldView character)
    {
        return
            weapon.definition.weaponDef != null &&
            character.definition.characterDef != null &&
            weapon.definition.tags.HasFlag(CardTag.Weapon) &&
            character.definition.tags.HasFlag(CardTag.Character);
    }

    private bool EquipAndConsume(CardWorldView weaponView, CardWorldView characterView)
    {
        if (weaponView == null || characterView == null) return false;

        var character = characterView.GetComponent<CardGame.Gameplay.Units.CharacterCard>();
        if (character == null) return false;

        // 规格：武器卡必须是单体（堆叠武器不可直接装备）
        int weaponStackCount = weaponView.GetStackCount();
        if (weaponStackCount != 1) return false;

        // 执行装备（返回被替换的旧武器卡ID）
        if (!character.TryEquipWeapon(weaponView.definition, weaponStackCount, out var replacedWeaponCardId))
            return false;

        // 刷新人物外观（换卡面 + 左下角图标）
        characterView.RefreshEquipmentVisuals();

        // 旧武器：生成一张卡并“弹出”
        if (!string.IsNullOrEmpty(replacedWeaponCardId))
        {
            var db = CardDatabase.Instance;
            var factory = CardGame.Gameplay.Cards.CardFactory.Instance;

            if (db != null && factory != null)
            {
                var oldDef = db.GetCardById(replacedWeaponCardId);
                if (oldDef != null)
                {
                    Vector3 from = characterView.transform.position;
                    Vector3 dir = (weaponView.transform.position - from);
                    if (dir.sqrMagnitude < 0.0001f) dir = Vector3.right;
                    dir.Normalize();

                    Vector3 to = from + dir * Mathf.Max(0.6f, bounceDistance * 0.8f);
                    to.z = from.z;

                    var oldView = factory.Spawn(oldDef, from);
                    if (oldView != null)
                    {
                        CardGame.Gameplay.Cards.CardAirdropUtility.DropSingle(
                            oldView, to,
                            liftY: 0.25f,
                            liftDur: 0.08f,
                            dropDur: 0.18f
                        );
                    }
                }
            }
        }

        // 如果武器在堆中，先从堆结构中安全移除
        DetachFromStack(weaponView);

        // 吃掉武器卡
        Destroy(weaponView.gameObject);
        return true;
    }

    private void BindCharacterEquipmentVisuals()
    {
        if (definition == null) return;
        if (!definition.tags.HasFlag(CardTag.Character)) return;

        var character = GetComponent<CardGame.Gameplay.Units.CharacterCard>();
        if (character == null) return;

        if (_boundCharacter == character) { RefreshEquipmentVisuals(); return; }

        if (_boundCharacter != null)
            _boundCharacter.OnEquipmentChanged -= RefreshEquipmentVisuals;

        _boundCharacter = character;
        _boundCharacter.OnEquipmentChanged += RefreshEquipmentVisuals;

        RefreshEquipmentVisuals();
    }

    public void RefreshEquipmentVisuals()
    {
        if (definition == null) return;
        if (!definition.tags.HasFlag(CardTag.Character)) return;

        var character = GetComponent<CardGame.Gameplay.Units.CharacterCard>();
        if (character == null) return;

        // 1) 卡面切换
        Sprite chosen = null;
        var charDef = definition.characterDef;
        if (charDef != null)
        {
            if (character.HasWeaponEquipped)
            {
                if (charDef.armedArtworkOverride != null) chosen = charDef.armedArtworkOverride;
            }
            else
            {
                if (charDef.normalArtworkOverride != null) chosen = charDef.normalArtworkOverride;
            }
        }

        if (artworkRenderer != null)
        {
            if (chosen != null) artworkRenderer.sprite = chosen;
            else if (definition.artwork != null) artworkRenderer.sprite = definition.artwork;
        }

        // 2) 武器缩略图（左下角 0.3 倍）
        if (visual == null) visual = GetComponent<CardVisualController>();
        if (visual != null)
        {
            Sprite weaponSprite = null;
            if (character.HasWeaponEquipped && !string.IsNullOrEmpty(character.EquippedWeaponCardId))
            {
                var db = CardDatabase.Instance;
                if (db != null)
                {
                    var wDef = db.GetCardById(character.EquippedWeaponCardId);
                    if (wDef != null) weaponSprite = wDef.artwork;
                }
            }

            visual.SetWeaponIcon(weaponSprite);
        }
    }

    /// <summary>
    /// 从当前堆中安全移除一张卡（用于被消耗的卡）
    /// </summary>
    private void DetachFromStack(CardWorldView card)
    {
        if (card == null) return;

        var root = card.GetStackRoot();
        if (root == null) return;

        // 单卡，不在堆中
        if (root.stackMembers == null || root.stackMembers.Count == 0)
            return;

        // 从 root 的成员列表中移除
        root.stackMembers.Remove(card);

        // 如果只剩 1 张，解除堆状态
        if (root.stackMembers.Count == 1)
        {
            var last = root.stackMembers[0];
            last.stackRoot = null;
            last.stackMembers = null;
            last.offsetInStack = Vector3.zero;

            root.stackMembers.Clear();
            root.stackMembers = null;
        }
    }


    // ------------ 找探索区 collider + 判断是否在探索区 ------------

    private bool IsInsideExplorationLane(CardWorldView root)
    {
        if (s_ExplorationLaneCollider == null)
        {
            var obj = GameObject.FindWithTag("ExplorationLaneArea");
            if (obj != null) s_ExplorationLaneCollider = obj.GetComponent<Collider2D>();
        }

        if (s_ExplorationLaneCollider == null) return false;

        // 用“堆的 bounds”来判断，而不是只用中心点
        var b = GetStackWorldBounds(root);

        Vector3[] samples =
        {
        b.center,
        new Vector3(b.min.x, b.min.y, b.center.z),
        new Vector3(b.min.x, b.max.y, b.center.z),
        new Vector3(b.max.x, b.min.y, b.center.z),
        new Vector3(b.max.x, b.max.y, b.center.z),
        };

        for (int i = 0; i < samples.Length; i++)
        {
            if (s_ExplorationLaneCollider.OverlapPoint(samples[i]))
                return true;
        }
        return false;

        // 用 root 中心点判断即可（也可以做 bounds 四角采样）
        //return s_ExplorationLaneCollider.OverlapPoint(root.transform.position);
    }

    private bool IsCharacterCard(CardWorldView root)
    {
        if (root == null) return false;
        if (root.definition == null) return false;

        // 通用 prefab 架构下，身份以 tags 为准
        return root.definition.tags.HasFlag(CardTag.Character);
    }

    // ------------ 接口：给外部（探索区等）读取 ------------

    /// <summary>
    /// 自己是否正在拖拽（只读）
    /// </summary>
    public bool IsDragging => isDragging;

    /// <summary>
    /// 堆根是否正在拖拽（只读）
    /// </summary>
    public bool IsDraggingRoot
    {
        get
        {
            var r = GetStackRoot();
            return r != null && r.IsDragging;
        }
    }

    /// <summary>
    /// 直接设置显示Sprite（用于卡背、临时表现）
    /// </summary>
    public void SetArtworkSprite(Sprite s)
    {
        if (artworkRenderer != null)
            artworkRenderer.sprite = s;
    }

    private bool IsDraggableByPlayer(CardWorldView root)
    {
        if (root == null) return false;
        if (root.definition == null) return true; // 没 definition 时先不拦（避免生成中误伤）
        return !root.definition.tags.HasFlag(CardTag.Enemy);
    }

    // ==============================
    // 停止整堆的所有 Tween（用于战斗打断）
    // ==============================
    public void KillAllMovementTweens()
    {
        var root = GetStackRoot();
        if (root == null) return;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            c.transform.DOKill(false);
        }
    }

    // ==============================
    // 获取堆顶卡（Last）
    // ==============================
    public CardWorldView GetTopCardInStack()
    {
        var root = GetStackRoot();
        if (root == null) return null;

        if (root.stackMembers == null || root.stackMembers.Count == 0)
            return root; // 单卡：自己就是顶

        return root.stackMembers[root.stackMembers.Count - 1];
    }

    // =========================================================
    // 战斗/演出用：临时抬高堆渲染层级（攻击时更好看）
    // =========================================================
    //private int _sortingOrderBeforeTemp = 0;
    //private bool _hasTempSorting = false;

    public void BeginTempFront()
    {
        PushStackOverlay("temp");
    }

    public void EndTempFront()
    {
        PopStackOverlay("temp");
    }

    // =========================================================
    // Stacklands：安全销毁堆内某一张卡（比如打碎顶牌）
    // - 会维护 stackMembers
    // - 会刷新剩余堆的显示（底/中/顶 + 数量）
    // =========================================================
    public void DespawnSafelyFromStack()
    {
        var self = this;

        // 1) 立刻停止自己身上的一切 tween，并立即隐藏（避免 Destroy 延迟导致“还显示”）
        self.transform.DOKill(false);

        if (self.artworkRenderer != null)
            self.artworkRenderer.enabled = false;

        var selfCol = self.GetComponent<Collider2D>();
        if (selfCol != null) selfCol.enabled = false;

        var selfDisp = self.GetComponent<CardStackDisplay>();
        if (selfDisp != null)
        {
            // 防止数量/叠加显示残留（用你已有的 HideInStack 即可）
            selfDisp.HideInStack(self.transform.position);
            selfDisp.SetCount(0);
        }

        var root = self.GetStackRoot();
        if (root == null)
        {
            Destroy(self.gameObject);
            return;
        }

        // 追加：清理 null
        if (root.stackMembers != null)
            root.stackMembers.RemoveAll(c => c == null);

        // 单卡：直接销毁
        if (root.stackMembers == null || root.stackMembers.Count == 0)
        {
            Destroy(self.gameObject);
            return;
        }

        // 从成员列表移除自己（注意：这里不 Destroy 列表对象）
        root.stackMembers.Remove(self);

        // 自己解除堆信息
        self.stackRoot = null;
        self.stackMembers = null;
        self.offsetInStack = Vector3.zero;

        // 2) 剩余 1 张：解除堆+ 强制刷新显示/事件
        if (root.stackMembers.Count == 1)
        {
            var last = root.stackMembers[0];
            if (last != null)
            {
                // 以 root 的位置作为“堆中心”（更稳定）
                Vector3 center = root.transform.position;

                last.stackRoot = null;
                last.stackMembers = null;
                last.offsetInStack = Vector3.zero;

                // 强制回到中心，避免残留顶牌偏移位置
                last.transform.DOKill(false);
                last.transform.position = center;

                // 确保可见/可交互
                if (last.artworkRenderer != null) last.artworkRenderer.enabled = true;
                var col = last.GetComponent<Collider2D>();
                if (col != null) col.enabled = true;

                // 让它显示为单卡
                var disp = last.GetComponent<CardStackDisplay>();
                if (disp != null)
                {
                    disp.ShowAsBottom(center);
                    disp.SetCount(0);
                }
                // 单卡化后补一次刷新（zone/按钮/状态同步）
                last.RaiseWorldPositionChanged();
            }

            // 清掉 root 的堆
            if (root != null)
            {
                root.stackMembers.Clear();
                root.stackMembers = null;
            }

            Destroy(self.gameObject);
            return;
        }

        // 3) 剩余 >= 2：刷新堆显示
        // 关键：RefreshStackVisualAfterSplit(root) 要求 root.stackMembers[0] 是 root
        // 如果 root 被销毁的是底牌（理论上资源攻击打顶牌不会），这里兜底重定 root
        if (root == self || root == null || root.stackMembers[0] != root)
        {
            // 选一个新的 root（底牌）
            var newRoot = root.stackMembers[0];
            if (newRoot != null)
            {
                // 把成员列表“交给” newRoot
                newRoot.stackMembers = root.stackMembers;
                newRoot.stackRoot = newRoot;

                // 原 root 不再持有成员列表
                if (root != null && root != newRoot)
                    root.stackMembers = null;

                // 确保每个成员的 stackRoot 指向 newRoot
                for (int i = 0; i < newRoot.stackMembers.Count; i++)
                {
                    var c = newRoot.stackMembers[i];
                    if (c == null) continue;
                    c.stackRoot = newRoot;
                    if (c != newRoot) c.stackMembers = null;
                }

                // 刷新显示
                RefreshStackVisualAfterSplit(newRoot);
            }
        }
        else
        {
            RefreshStackVisualAfterSplit(root);
        }

        Destroy(self.gameObject);
    }
}
