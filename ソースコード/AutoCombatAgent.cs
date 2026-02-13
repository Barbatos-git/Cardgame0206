using UnityEngine;
using CardGame.Core.Combat;
using CardGame.Gameplay.Units;
using DG.Tweening;
using CardGame.Core.Pause;
using CardGame.Gameplay.Cards;

public sealed class AutoCombatAgent : MonoBehaviour, IPausable
{
    private enum Faction { None, Friendly, Enemy }

    // ==============================
    // Enemy 显式状态机
    // ==============================
    private enum EnemyState
    {
        Patrol,
        ChaseResource,
        AttackCharacter,
        ChaseCharacter,
        AttackResource
    }

    [Header("Search / Attack")]
    [SerializeField] private float searchRadius = 2.5f;
    [SerializeField] private float friendlyAttackCooldown = 1.0f;
    [SerializeField] private float enemyAttackCooldown = 1.2f;

    [Header("Enemy vs Resource")]
    [SerializeField] private float attackRange = 0.8f;        // 攻击范围（人物/资源都用）
    [SerializeField] private float chaseStep = 1.0f;          // 每次追击走多远（<= patrolStepDistance 或相近）
    [SerializeField] private float chaseMoveDuration = 0.45f; // 追击移动耗时
    // 追资源时与资源保持的最小间隔（避免重叠/贴太近）
    [SerializeField] private float resourceStopDistance = 2f;
    // 攻击冲刺时与资源保持的“最小贴脸距离”
    [SerializeField] private float resourceMinGap = 0.75f;


    // 追资源的“步伐节奏”控制：避免 Update 每0.15s 一直发 MoveWorldPosition
    [SerializeField] private float chasePauseMin = 0.25f;
    [SerializeField] private float chasePauseMax = 0.55f;
    private float _nextChaseMoveTime;

    // 资源攻击演出参数
    [SerializeField] private float resourceAttackLunge = 0.35f;
    [SerializeField] private float resourceAttackAnimTime = 0.16f;
    private bool _resourceAttacking;

    [Header("Drag Guard")]
    [SerializeField] private bool disableAIWhileDragging = true;

    [Header("Patrol (Enemy only)")]
    [SerializeField] private float patrolMoveDuration = 0.6f;
    [SerializeField] private float patrolWaitMin = 0.8f;
    [SerializeField] private float patrolWaitMax = 1.6f;
    [SerializeField] private float patrolRange = 2.0f;

    [Header("Smart Patrol")]
    [SerializeField] private float patrolStepDistance = 1.1f;     // 每一步走多远（建议 <= patrolRange）
    [SerializeField, Range(0f, 1f)] private float patrolInertia = 0.85f; // 越大越“保持方向”
    [SerializeField] private float patrolJitterAngle = 22f;       // 每步方向随机扰动角度（度）
    [SerializeField] private int patrolPickTries = 3;             // 选方向失败时重试次数（越大越稳）
    [SerializeField] private float boundaryMemorySeconds = 1.2f;  // 边界记忆持续时间
    [SerializeField] private float boundaryEpsilon = 0.02f;       // 贴边判定阈值

    private Vector2 _patrolDir = Vector2.right; // 当前巡逻“惯性方向”
    private float _avoidXUntil;
    private float _avoidYUntil;
    private int _avoidXSign; // +1 刚撞右边界（暂时别往右），-1 左边界
    private int _avoidYSign; // +1 上边界，-1 下边界

    [Header("Disengage / Resume")]
    [SerializeField] private float disengageDistance = 4.0f;     // 超出就退出战斗回巡逻
    [SerializeField] private float patrolResumeDelay = 0.15f;     // 退出战斗后稍微等一下再巡逻（防抖）

    [Header("Combat Service (auto-resolved if empty)")]
    [SerializeField] private MonoBehaviour combatServiceComponent;

    private static ICombatService s_CachedCombatService;
    private static MonoBehaviour s_CachedCombatComponent;

    private ICombatService _combat;
    private CardWorldView _view;
    private Faction _faction;

    private float _nextAttackTime;
    private float _nextThinkTime;
    private bool _isPatrolling;

    private bool _warnedMissingCombat;

    // 兜底需要：缓存“当前目标的 CardWorldView”，用于距离判断 / 目标丢失处理
    private CardWorldView _currentTargetView;
    private float _resumePatrolAt;

    // ==============================
    // Enemy 状态机字段
    // ==============================
    private EnemyState _enemyState = EnemyState.Patrol;
    private CardWorldView _resourceTargetRoot;     // 追击的资源堆根  
    // 人物目标：search 里追击用；attack 里攻击用
    private CardWorldView _characterTargetRootInSearch;
    private ICombatant _characterTargetInAttack;  // 攻击范围内的人物目标（HP最低）

    //pause
    private bool _paused;

    private void Awake()
    {
        _view = GetComponent<CardWorldView>();
        _combat = combatServiceComponent as ICombatService;

        if (_view == null)
            Debug.LogError("[AutoCombatAgent] CardWorldView missing.", this);

        ResolveCombatService();
        if (_combat == null && !_warnedMissingCombat)
        {
            _warnedMissingCombat = true;
            Debug.LogError("[AutoCombatAgent] Combat service not set or not implementing ICombatService.", this);
        }

        RefreshFactionFromDefinition();
    }

    private void OnEnable()
    {
        // prefab 通用时，definition 可能在实例化后才赋值，这里再刷新一次更稳
        RefreshFactionFromDefinition();

        // 有些对象是运行时生成：OnEnable 再补一次获取更稳
        ResolveCombatService();

        if (_faction == Faction.Enemy)
        {
            _patrolDir = Random.insideUnitCircle.sqrMagnitude > 0.001f
                ? Random.insideUnitCircle.normalized
                : Vector2.right;

            // 启用时重置状态
            _enemyState = EnemyState.Patrol;
            _resourceTargetRoot = null;
            _characterTargetInAttack = null;
            _characterTargetRootInSearch = null;
        }

        ApplyEnemyParamsFromDefinition();
    }

    private void ApplyEnemyParamsFromDefinition()
    {
        if (_view == null || _view.definition == null) return;
        if (!_view.definition.tags.HasFlag(CardTag.Enemy)) return;

        var ed = _view.definition.enemyDef;
        if (ed == null) return;

        enemyAttackCooldown = ed.attackCooldown;
        searchRadius = ed.searchRadius;
        attackRange = ed.attackRange;
    }


    /// <summary>
    /// 自动解析 CombatService：
    /// 1) Inspector拖入优先
    /// 2) 使用静态缓存（全局只找一次）
    /// 3) 场景中搜索第一个实现 ICombatService 的 MonoBehaviour
    /// </summary>
    private void ResolveCombatService()
    {
        // 1) Inspector 优先
        if (combatServiceComponent != null)
        {
            _combat = combatServiceComponent as ICombatService;
            if (_combat != null)
            {
                CacheCombatService(combatServiceComponent, _combat);
                return;
            }
        }

        // 2) 静态缓存
        if (s_CachedCombatService != null)
        {
            _combat = s_CachedCombatService;
            combatServiceComponent = s_CachedCombatComponent;
            return;
        }

        // 3) 场景搜索：找任意实现 ICombatService 的组件
#if UNITY_2023_1_OR_NEWER
        var all = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
#else
        var all = Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
        for (int i = 0; i < all.Length; i++)
        {
            var mb = all[i];
            if (mb == null) continue;

            if (mb is ICombatService service)
            {
                _combat = service;
                combatServiceComponent = mb;
                CacheCombatService(mb, service);
                return;
            }
        }

        // 找不到：只报一次，避免刷屏
        _combat = null;
        if (!_warnedMissingCombat)
        {
            _warnedMissingCombat = true;
            Debug.LogError("[AutoCombatAgent] Combat service not found. Please ensure CombatController (ICombatService) exists in scene.", this);
        }
    }

    private static void CacheCombatService(MonoBehaviour component, ICombatService service)
    {
        s_CachedCombatComponent = component;
        s_CachedCombatService = service;
    }

    private void Update()
    {
        if (_paused) return;
        // combat 可能在运行时才出现：如果没拿到，定期尝试一次（低频）
        if (_combat == null)
        {
            // 每 0.5 秒尝试一次，避免每帧 Find
            if (Time.time >= _nextThinkTime)
            {
                _nextThinkTime = Time.time + 0.5f;
                ResolveCombatService();
            }
            return;
        }

        if (_view == null || _combat == null) return;

        // 根据 tags 自动决定是否参战
        RefreshFactionFromDefinition();
        if (_faction == Faction.None) return;

        var root = _view.GetStackRoot();

        // 拖拽中：禁止开战，也禁止巡逻
        if (disableAIWhileDragging)
        {
            if (root.IsDraggingRoot)
            {
                _isPatrolling = false; // 停止巡逻循环
                ForceExitCombat(); // 兜底
                return;
            }
        }

        // 合成区内不AI
        if (root.isInFusionZone) return;

        // 不在普通区就不AI（用 bounds 简化）
        if (!IsRootInsideNormalArea(root)) return;

        // 只允许“正确的单位脚本”参与战斗（通用 prefab 上同时挂 CharacterCard/EnemyCard 也不怕）
        var self = ResolveSelfCombatant(root);
        if (self == null || self.IsDead)
        {
            ForceExitCombat();
            return;
        }

        // 兜底：当前目标如果无效/太远，强制退出战斗，允许稍后恢复巡逻
        if (_currentTargetView != null)
        {
            var tRoot = _currentTargetView.GetStackRoot();
            if (tRoot == null)
            {
                ForceExitCombat();
            }
            else
            {
                // 目标死亡也要退出
                var targetCombatant = ResolveTargetCombatant(tRoot);
                if (targetCombatant == null || targetCombatant.IsDead)
                {
                    ForceExitCombat();
                }
                else
                {
                    float d = Vector2.Distance(root.transform.position, tRoot.transform.position);
                    if (d > disengageDistance)
                    {
                        ForceExitCombat();
                    }
                }
            }
        }

        // 频率控制：降低索敌开销
        if (Time.time < _nextThinkTime) return;
        _nextThinkTime = Time.time + 0.15f;

        // 攻击锁（如果你已经加了 AttackLock，就会避免连发）
        var lockComp = (self as Component)?.GetComponent<AttackLock>();
        if (lockComp != null && lockComp.IsLocked) return;

        // =========================================================
        // Enemy：显式状态机
        // =========================================================
        if (_faction == Faction.Enemy)
        {
            TickEnemyStateMachine(root, self);
            return;
        }   

        // =========================================================
        // Friendly 仍沿用旧逻辑：追击/攻击敌人
        // =========================================================

        // 优先沿用当前目标（仍在 searchRadius 内才继续）
        ICombatant target = null;
        if (_currentTargetView != null)
        {
            var tRoot = _currentTargetView.GetStackRoot();
            if (tRoot != null)
            {
                float sqr = (tRoot.transform.position - root.transform.position).sqrMagnitude;
                if (sqr <= searchRadius * searchRadius)
                {
                    target = ResolveTargetCombatant(tRoot);
                    if (target == null || target.IsDead) target = null;
                }
            }
        }

        if (target == null)
        {
            target = FindNearestTarget(root.transform.position);

            if (target != null)
            {
                var comp = target as Component;
                _currentTargetView = comp != null ? comp.GetComponent<CardWorldView>() : null;
            }
        }

        if (target != null)
        {
            _isPatrolling = false;

            if (Time.time >= _nextAttackTime)
            {
                _nextAttackTime = Time.time + friendlyAttackCooldown;
                _combat.RequestAttack(self, target);
            }
            return;
        }

        //// 没目标：敌人巡逻；我方不巡逻
        //if (_faction == Faction.Enemy)
        //{
        //    // 退出战斗后的小延迟（防抖）
        //    if (Time.time < _resumePatrolAt) return;

        //    if (!_isPatrolling)
        //        StartCoroutine(PatrolLoop());
        //}

        _currentTargetView = null;
    }

    // =========================================================
    // Enemy State Machine
    //   追人规则：
    // - searchRadius 内看到人物：立刻停止追资源，进入 ChaseCharacter
    // - 进入 attackRange：切 AttackCharacter 并攻击
    // - 人物离开 searchRadius（或被拖走不在普通区）：回 Patrol（再找资源）
    // =========================================================
    private void TickEnemyStateMachine(CardWorldView root, ICombatant self)
    {
        // 空投中：任何状态都不跑
        if (CardAirdropUtility.IsAirDropping(root))
            return;

        // 任何状态下：只要攻击范围内有人物，立刻切到 AttackCharacter
        // 且停止资源搜索/追击
        _characterTargetRootInSearch = FindBestCharacterRootInRadius(root.transform.position, searchRadius);
        _characterTargetInAttack = null;
        if (_characterTargetRootInSearch != null)
        {
            // 若已到攻击距离，进入 AttackCharacter
            var inAttack = FindBestCharacterRootInRadius(root.transform.position, attackRange);
            if (inAttack != null && inAttack == _characterTargetRootInSearch)
            {
                _characterTargetInAttack = ResolveCharacterCombatant(inAttack);
                SwitchEnemyState(EnemyState.AttackCharacter, root);
            }
            else
            {
                SwitchEnemyState(EnemyState.ChaseCharacter, root);
            }
        }

        switch (_enemyState)
        {
            case EnemyState.Patrol:
                {
                    // 人物在范围会在上面抢占切走，这里只考虑资源
                    _resourceTargetRoot = FindNearestResourceRoot(root.transform.position);
                    if (_resourceTargetRoot != null)
                    {
                        SwitchEnemyState(EnemyState.ChaseResource, root);
                        return;
                    }

                    // 没人物没资源：巡逻
                    if (Time.time < _resumePatrolAt) return;
                    if (!_isPatrolling) StartCoroutine(PatrolLoop());
                    return;
                }

            case EnemyState.ChaseResource:
                {
                    // 如果此时人物进入范围，上面已经切到 AttackCharacter 并 return 到那里
                    if (_characterTargetRootInSearch != null) return;

                    // 追资源时动态重选最近资源（目标被拖走/不再最近时能立刻换目标）
                    var nearest = FindNearestResourceRoot(root.transform.position);

                    // 没资源了 -> 回巡逻
                    if (nearest == null)
                    {
                        _resourceTargetRoot = null;
                        SwitchEnemyState(EnemyState.Patrol, root);
                        return;
                    }

                    // 目标正在被拖拽，或最近资源已经不是原目标 -> 换目标
                    if (_resourceTargetRoot == null ||
                        (_resourceTargetRoot != nearest) ||
                        (disableAIWhileDragging && _resourceTargetRoot.IsDraggingRoot))
                    {
                        _resourceTargetRoot = nearest;
                    }

                    // 追击移动：step + pause
                    Vector3 myPos = root.transform.position;
                    Vector3 targetPos = _resourceTargetRoot.transform.position;
                    Vector2 dir = (targetPos - myPos);
                    float dist = dir.magnitude;
                    if (dist > 0.0001f) dir /= dist;

                    Vector3 desiredStop = targetPos - (Vector3)(dir * resourceStopDistance);

                    if (dist > resourceStopDistance)
                    {
                        if (Time.time >= _nextChaseMoveTime && !_resourceAttacking)
                        {
                            Vector3 dest = PickChaseDestinationWithinNormal(root, desiredStop, chaseStep);
                            root.MoveWorldPosition(dest, chaseMoveDuration);

                            _nextChaseMoveTime = Time.time + chaseMoveDuration + Random.Range(chasePauseMin, chasePauseMax);
                        }
                        return;
                    }

                    // 进入范围 -> 攻击资源
                    SwitchEnemyState(EnemyState.AttackResource, root);
                    return;
                }

            case EnemyState.AttackCharacter:
                {
                    // 看不见人了（离开 searchRadius / 被拖走出普通区） -> 回 Patrol
                    if (_characterTargetRootInSearch == null)
                    {
                        SwitchEnemyState(EnemyState.Patrol, root);
                        return;
                    }

                    // 不在 attackRange 就回追人（而不是回 Patrol）
                    var inAttack = FindBestCharacterRootInRadius(root.transform.position, attackRange);
                    if (inAttack == null || inAttack != _characterTargetRootInSearch)
                    {
                        SwitchEnemyState(EnemyState.ChaseCharacter, root);
                        return;
                    }

                    _characterTargetInAttack = ResolveCharacterCombatant(inAttack);
                    if (_characterTargetInAttack == null) return;

                    if (Time.time >= _nextAttackTime)
                    {
                        _nextAttackTime = Time.time + enemyAttackCooldown;
                        _combat.RequestAttack(self, _characterTargetInAttack);
                    }
                    return;
                }

            case EnemyState.AttackResource:
                {
                    // AttackResource 状态：仍然优先人物（上面抢占切状态）
                    if (_characterTargetRootInSearch != null) return;

                    if (_resourceTargetRoot == null)
                    {
                        SwitchEnemyState(EnemyState.Patrol, root);
                        return;
                    }

                    // 如果又离开攻击范围，则继续追
                    float dist = Vector2.Distance(root.transform.position, _resourceTargetRoot.transform.position);
                    if (dist > resourceStopDistance)
                    {
                        SwitchEnemyState(EnemyState.ChaseResource, root);
                        return;
                    }

                    if (Time.time >= _nextAttackTime && !_resourceAttacking)
                    {
                        _nextAttackTime = Time.time + enemyAttackCooldown;

                        // Stacklands：只打堆顶那一张
                        var top = _resourceTargetRoot.GetTopCardInStack();
                        PlayAttackResourceAnimAndDamage(root, top, self.Attack);
                    }

                    return;
                }

            case EnemyState.ChaseCharacter:
                {
                    // 看不见人了 -> 回 Patrol（下一帧会找资源）
                    if (_characterTargetRootInSearch == null)
                    {
                        SwitchEnemyState(EnemyState.Patrol, root);
                        return;
                    }

                    // 人物在 attackRange -> 交给 AttackCharacter（上面的抢占也会切，但这里兜底）
                    var inAttack = FindBestCharacterRootInRadius(root.transform.position, attackRange);
                    if (inAttack != null && inAttack == _characterTargetRootInSearch)
                    {
                        _characterTargetInAttack = ResolveCharacterCombatant(inAttack);
                        SwitchEnemyState(EnemyState.AttackCharacter, root);
                        return;
                    }

                    // 追人：step+pause（不需要 resourceStopDistance；用 attackRange 外侧停靠）
                    if (Time.time >= _nextChaseMoveTime)
                    {
                        Vector3 myPos = root.transform.position;
                        Vector3 targetPos = _characterTargetRootInSearch.transform.position;

                        Vector2 dir = (targetPos - myPos);
                        float dist = dir.magnitude;
                        if (dist > 0.0001f) dir /= dist;

                        // 停在 attackRange 外侧一点点，避免重叠
                        Vector3 desiredStop = targetPos - (Vector3)(dir * (attackRange * 0.9f));

                        Vector3 dest = PickChaseDestinationWithinNormal(root, desiredStop, chaseStep);
                        root.MoveWorldPosition(dest, chaseMoveDuration);
                        _nextChaseMoveTime = Time.time + chaseMoveDuration + Random.Range(chasePauseMin, chasePauseMax);
                    }
                    return;
                }
        }
    }

    private void SwitchEnemyState(EnemyState next, CardWorldView root)
    {
        if (_enemyState == next) return;

        // 退出旧状态：必要时中断移动
        if (next == EnemyState.ChaseCharacter || next == EnemyState.AttackCharacter)
        {
            _isPatrolling = false;

            // 进入人物攻击：必须停掉所有移动 tween，彻底终止追资源表现
            root.KillAllMovementTweens();
            _resourceAttacking = false;
            _resourceTargetRoot = null;
            _nextChaseMoveTime = Time.time;
            root.ClampStackToNormalArea();
        }

        if (next == EnemyState.Patrol)
        {
            _resourceTargetRoot = null;
            _nextChaseMoveTime = Time.time;
        }

        // 只要切到非 Patrol，就立刻停止巡逻协程
        if (next != EnemyState.Patrol)
        {
            _isPatrolling = false;
        }

        _enemyState = next;
    }

    /// <summary>
    /// 兜底：强制退出战斗状态，让 AI 能恢复巡逻
    /// </summary>
    private void ForceExitCombat()
    {
        // 只有当“之前确实有目标”时，才设置恢复巡逻延迟（防抖）
        if (_currentTargetView != null)
        {
            _currentTargetView = null;
            _resumePatrolAt = Time.time + patrolResumeDelay;
        }
        else
        {
            // 之前就没目标：不要反复刷新 resume 时间，否则永远无法巡逻
            _currentTargetView = null;
        }
    }

    /// <summary>
    /// 从 CardDefinition.tags 自动判断阵营 & 是否需要AI
    /// </summary>
    private void RefreshFactionFromDefinition()
    {
        if (_view == null || _view.definition == null)
        {
            _faction = Faction.None;
            return;
        }

        var tags = _view.definition.tags;

        // 只有 Character/Enemy 参与战斗AI
        if (tags.HasFlag(CardTag.Enemy)) _faction = Faction.Enemy;
        else if (tags.HasFlag(CardTag.Character)) _faction = Faction.Friendly;
        else _faction = Faction.None;
    }

    /// <summary>
    /// 在“通用 prefab 同时挂了 CharacterCard/EnemyCard”时，
    /// 强制按 tags 选择正确的那个组件作为 ICombatant
    /// </summary>
    private ICombatant ResolveSelfCombatant(CardWorldView root)
    {
        if (root == null || root.definition == null) return null;

        if (_faction == Faction.Friendly)
            return root.GetComponent<CharacterCard>(); // 必须是组件型

        if (_faction == Faction.Enemy)
            return root.GetComponent<EnemyCard>();

        return null;
    }

    private ICombatant FindNearestTarget(Vector3 myPos)
    {
        ICombatant best = null;
        float bestSqr = float.MaxValue;

        var list = CardWorldView.CardWorldViewRegistry; // 你系统里已有注册表
        float r2 = searchRadius * searchRadius;

        for (int i = 0; i < list.Count; i++)
        {
            var otherView = list[i];
            if (otherView == null) continue;

            var otherRoot = otherView.GetStackRoot();
            if (otherRoot == _view.GetStackRoot()) continue;

            // 空投中的卡，一律无视
            if (CardAirdropUtility.IsAirDropping(otherRoot)) continue;

            // 对方正在拖拽：不作为目标
            if (disableAIWhileDragging && otherRoot.IsDraggingRoot) continue;

            if (otherRoot.isInFusionZone) continue;
            if (!IsRootInsideNormalArea(otherRoot)) continue;

            // 阵营过滤（用 tags，绝不靠 inspector）
            if (!IsOppositeFaction(otherRoot)) continue;

            // 目标 combatant 也必须是组件型，且与 tags 匹配
            var target = ResolveTargetCombatant(otherRoot);
            if (target == null || target.IsDead) continue;

            float sqr = (otherRoot.transform.position - myPos).sqrMagnitude;
            if (sqr > r2) continue;

            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = target;
            }
        }

        return best;
    }

    private bool IsOppositeFaction(CardWorldView otherRoot)
    {
        if (otherRoot == null || otherRoot.definition == null) return false;

        bool otherIsEnemy = otherRoot.definition.tags.HasFlag(CardTag.Enemy);
        bool otherIsChar = otherRoot.definition.tags.HasFlag(CardTag.Character);

        return _faction switch
        {
            Faction.Enemy => otherIsChar,
            Faction.Friendly => otherIsEnemy,
            _ => false
        };
    }

    private ICombatant ResolveTargetCombatant(CardWorldView otherRoot)
    {
        if (otherRoot == null || otherRoot.definition == null) return null;

        if (_faction == Faction.Friendly)
        {
            // 我方找敌
            if (!otherRoot.definition.tags.HasFlag(CardTag.Enemy)) return null;
            return otherRoot.GetComponent<EnemyCard>();
        }
        else if (_faction == Faction.Enemy)
        {
            // 敌找我方
            if (!otherRoot.definition.tags.HasFlag(CardTag.Character)) return null;
            return otherRoot.GetComponent<CharacterCard>();
        }

        return null;
    }

    private bool IsRootInsideNormalArea(CardWorldView root)
    {
        if (root == null) return false;

        var area = root.normalAreaCollider;
        if (area == null) return true;

        // 用 bounds 判断
        Bounds areaBounds = area.bounds;

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

        if (!has) return true;

        return areaBounds.Contains(stackBounds.min) && areaBounds.Contains(stackBounds.max);
    }

    private bool TryGetStackBounds(CardWorldView root, out Bounds stackBounds)
    {
        stackBounds = new Bounds(root.transform.position, Vector3.zero);
        bool has = false;

        foreach (var c in root.GetStackMembersIncludingSelf())
        {
            if (c == null) continue;
            var col = c.GetComponent<Collider2D>();
            if (col == null || !col.enabled) continue;

            if (!has) { stackBounds = col.bounds; has = true; }
            else stackBounds.Encapsulate(col.bounds);
        }

        return has;
    }

    // =========================================================
    // 2D Bounds 最短距离（用于“攻击范围”判定，避免中心点误差）
    // =========================================================
    private static float BoundsDistance2D(in Bounds a, in Bounds b)
    {
        float dx = 0f;
        if (a.max.x < b.min.x) dx = b.min.x - a.max.x;
        else if (b.max.x < a.min.x) dx = a.min.x - b.max.x;

        float dy = 0f;
        if (a.max.y < b.min.y) dy = b.min.y - a.max.y;
        else if (b.max.y < a.min.y) dy = a.min.y - b.max.y;

        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private bool IsInAttackRangeBounds(CardWorldView selfRoot, CardWorldView otherRoot)
    {
        if (selfRoot == null || otherRoot == null) return false;

        // 有 collider：用 bounds-to-bounds
        if (TryGetStackBounds(selfRoot, out var a) && TryGetStackBounds(otherRoot, out var b))
        {
            return BoundsDistance2D(a, b) <= attackRange;
        }

        // 兜底：没有 collider 就用中心点
        return Vector2.Distance(selfRoot.transform.position, otherRoot.transform.position) <= attackRange;
    }


    /// <summary>
    /// 选一个“整堆 bounds 不越界”的巡逻目标点；
    /// 如果某方向会越界，会自动换方向，并记录边界记忆（短时间不再往那个方向试）
    /// </summary>
    private Vector3 PickSmartPatrolDestination(CardWorldView root)
    {
        var start = root.transform.position;
        var area = root.normalAreaCollider;
        if (area == null)
        {
            // 没普通区边界就退化：惯性方向 + 少量随机
            return start + (Vector3)(_patrolDir * patrolStepDistance);
        }

        if (!TryGetStackBounds(root, out var sb))
        {
            // 没 collider：退化为 root 点 clamp
            Vector2 d0 = _patrolDir * patrolStepDistance;
            Vector3 dest0 = new Vector3(start.x + d0.x, start.y + d0.y, start.z);
            var b0 = area.bounds;
            dest0.x = Mathf.Clamp(dest0.x, b0.min.x, b0.max.x);
            dest0.y = Mathf.Clamp(dest0.y, b0.min.y, b0.max.y);
            return dest0;
        }

        var ab = area.bounds;

        // 允许的 delta 范围：保证 (stackBounds + delta) 完整落在 ab 内
        float minDx = ab.min.x - sb.min.x;
        float maxDx = ab.max.x - sb.max.x;
        float minDy = ab.min.y - sb.min.y;
        float maxDy = ab.max.y - sb.max.y;

        // 1) 先生成一个“目标方向”（惯性 + 小扰动）
        Vector2 bestDelta = Vector2.zero;
        float bestPenalty = float.MaxValue;

        for (int i = 0; i < patrolPickTries; i++)
        {
            // jitter：在 [-patrolJitterAngle, +patrolJitterAngle] 内旋转
            float ang = Random.Range(-patrolJitterAngle, patrolJitterAngle);
            Vector2 jitterDir = Quaternion.Euler(0, 0, ang) * _patrolDir;

            // inertia：把随机方向和当前方向做混合，保持“惯性”
            Vector2 candDir = Vector2.Lerp(_patrolDir, jitterDir.normalized, 1f - patrolInertia);
            if (candDir.sqrMagnitude < 0.0001f) candDir = _patrolDir;
            candDir.Normalize();

            // 边界记忆：短时间内避免朝刚撞过的边界方向走
            if (Time.time < _avoidXUntil && _avoidXSign != 0 && Mathf.Sign(candDir.x) == _avoidXSign) candDir.x = -candDir.x;
            if (Time.time < _avoidYUntil && _avoidYSign != 0 && Mathf.Sign(candDir.y) == _avoidYSign) candDir.y = -candDir.y;

            Vector2 rawDelta = candDir * patrolStepDistance;
            Vector2 d = rawDelta;

            // 2) 预判：把 delta clamp 到合法范围（这样永远不会先越界）
            d.x = Mathf.Clamp(d.x, minDx, maxDx);
            d.y = Mathf.Clamp(d.y, minDy, maxDy);

            float penalty = Mathf.Abs(d.x - rawDelta.x) + Mathf.Abs(d.y - rawDelta.y);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestDelta = d;

                // 已经几乎不需要 clamp，直接用
                if (bestPenalty <= boundaryEpsilon) break;
            }
        }

        // 3) 根据“贴边情况”更新边界记忆 & 反弹方向（让它懂得墙在哪里）
        bool hitRight = Mathf.Abs(bestDelta.x - maxDx) <= boundaryEpsilon;
        bool hitLeft = Mathf.Abs(bestDelta.x - minDx) <= boundaryEpsilon;
        bool hitUp = Mathf.Abs(bestDelta.y - maxDy) <= boundaryEpsilon;
        bool hitDown = Mathf.Abs(bestDelta.y - minDy) <= boundaryEpsilon;

        if (hitRight) { _avoidXSign = +1; _avoidXUntil = Time.time + boundaryMemorySeconds; _patrolDir.x = -Mathf.Abs(_patrolDir.x); }
        else if (hitLeft) { _avoidXSign = -1; _avoidXUntil = Time.time + boundaryMemorySeconds; _patrolDir.x = +Mathf.Abs(_patrolDir.x); }

        if (hitUp) { _avoidYSign = +1; _avoidYUntil = Time.time + boundaryMemorySeconds; _patrolDir.y = -Mathf.Abs(_patrolDir.y); }
        else if (hitDown) { _avoidYSign = -1; _avoidYUntil = Time.time + boundaryMemorySeconds; _patrolDir.y = +Mathf.Abs(_patrolDir.y); }

        // 4) 更新“惯性方向”：用这次实际走出去的 delta 来决定下一次朝向
        if (bestDelta.sqrMagnitude > 0.0001f)
            _patrolDir = bestDelta.normalized;

        return new Vector3(start.x + bestDelta.x, start.y + bestDelta.y, start.z);
    }

    private System.Collections.IEnumerator PatrolLoop()
    {
        _isPatrolling = true;

        while (_isPatrolling && _faction == Faction.Enemy)
        {
            if (_paused)
            {
                yield return null;
                continue;
            }
                // 防抖：刚退出战斗的一小段时间内，不要立刻巡逻移动
                while (Time.time < _resumePatrolAt)
            {
                if (_faction != Faction.Enemy) { _isPatrolling = false; yield break; }
                yield return null;
            }

            // 如果出现目标就停止巡逻
            var root = _view.GetStackRoot();

            // 拖拽中不巡逻
            if (disableAIWhileDragging && root.IsDraggingRoot)
            {
                _isPatrolling = false;
                yield break;
            }

            var self = ResolveSelfCombatant(root);
            if (self == null || self.IsDead) { _isPatrolling = false; yield break; }

            // 状态机版：巡逻时若有人物在范围 -> 立即停巡逻（让 Update 切状态）
            if (FindBestCharacterRootInRadius(root.transform.position, searchRadius) != null)
            {
                _isPatrolling = false;
                yield break;
            }

            // 资源存在也停巡逻（让 Update 切到 ChaseResource）
            if (FindNearestResourceRoot(root.transform.position) != null)
            {
                _isPatrolling = false;
                yield break;
            }

            //var start = root.transform.position;
            //Vector2 rand = Random.insideUnitCircle * patrolRange;
            //Vector3 dest = new Vector3(start.x + rand.x, start.y + rand.y, start.z);

            //var area = root.normalAreaCollider;
            //if (area != null)
            //{
            //    var b = area.bounds;
            //    dest.x = Mathf.Clamp(dest.x, b.min.x, b.max.x);
            //    dest.y = Mathf.Clamp(dest.y, b.min.y, b.max.y);
            //}

            Vector3 dest = PickSmartPatrolDestination(root);

            root.MoveWorldPosition(dest, patrolMoveDuration);

            // 可中断等待：移动期间每帧检查，一旦人物进入攻击范围就立刻停止巡逻
            float moveT = 0f;
            while (moveT < patrolMoveDuration)
            {
                if (!_isPatrolling) yield break;

                // 人物进入攻击范围 -> 立刻退出，让 Update 的状态机接管
                if (FindBestCharacterRootInRadius(root.transform.position, searchRadius) != null)
                {
                    _isPatrolling = false;
                    yield break;
                }

                if (_paused)
                {
                    yield return null;
                    continue;
                }

                moveT += Time.deltaTime;
                yield return null;
            }
            root.ClampStackToNormalArea();

            float wait = Random.Range(patrolWaitMin, patrolWaitMax);
            float t = 0f;
            while (t < wait)
            {
                if (_faction != Faction.Enemy) { _isPatrolling = false; yield break; }

                if (FindNearestTarget(root.transform.position) != null)
                {
                    _isPatrolling = false;
                    yield break;
                }

                if (_paused)
                {
                    yield return null;
                    continue;
                }

                t += Time.deltaTime;
                yield return null;
            }
        }
    }

    // ------------ 人物选择/资源选择/追击点计算 ------------

    // 规则：攻击范围内若有多个我方人物，优先 HP 最少；同 HP 再按最近
    private CardWorldView FindBestCharacterRootInRadius(Vector3 myPos, float radius)
    {
        CardWorldView bestRoot = null;
        int bestHp = int.MaxValue;
        float bestDist2 = float.MaxValue;

        var selfRoot = _view.GetStackRoot();
        var list = CardWorldView.CardWorldViewRegistry;

        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v == null) continue;

            var r = v.GetStackRoot();
            if (r == null) continue;

            // 空投中的卡，一律无视
            if (CardAirdropUtility.IsAirDropping(r)) continue;

            if (r == selfRoot) continue;
            if (disableAIWhileDragging && r.IsDraggingRoot) continue;
            if (r.isInFusionZone) continue;
            if (!IsRootInsideNormalArea(r)) continue;

            if (r.definition == null || !r.definition.tags.HasFlag(CardTag.Character)) continue;

            var c = r.GetComponent<CharacterCard>();
            if (c == null || c.IsDead) continue;

            // 半径判定：优先用 bounds
            bool inRadius;
            if (TryGetStackBounds(selfRoot, out var a) && TryGetStackBounds(r, out var b))
            {
                inRadius = BoundsDistance2D(a, b) <= radius;
            }
            else
            {
                inRadius = Vector2.Distance(selfRoot.transform.position, r.transform.position) <= radius;
            }
            if (!inRadius) continue;

            float d2 = (r.transform.position - myPos).sqrMagnitude;
            if (c.HP < bestHp || (c.HP == bestHp && d2 < bestDist2))
            {
                bestHp = c.HP;
                bestDist2 = d2;
                bestRoot = r;
            }
        }

        return bestRoot;
    }

    private ICombatant ResolveCharacterCombatant(CardWorldView charRoot)
    {
        if (charRoot == null) return null;
        var cc = charRoot.GetComponent<CharacterCard>();
        if (cc == null || cc.IsDead) return null;
        return cc;
    }


    // 规则：资源卡 = 非 Enemy & 非 Character（你说“除了敌人和人物其他都算资源”）
    // 不使用半径：始终在普通区内找最近资源
    private CardWorldView FindNearestResourceRoot(Vector3 myPos)
    {
        CardWorldView best = null;
        float bestDist2 = float.MaxValue;

        var list = CardWorldView.CardWorldViewRegistry;

        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v == null) continue;

            var r = v.GetStackRoot();
            if (r == null) continue;

            // 空投中的卡，一律无视
            if (CardAirdropUtility.IsAirDropping(r)) continue;

            if (r == _view.GetStackRoot()) continue;
            if (disableAIWhileDragging && r.IsDraggingRoot) continue;
            if (r.isInFusionZone) continue;
            if (!IsRootInsideNormalArea(r)) continue;

            if (r.definition == null) continue;

            var tags = r.definition.tags;

            // 资源定义：除了敌人和人物其他都算资源
            if (tags.HasFlag(CardTag.Enemy)) continue;
            if (tags.HasFlag(CardTag.Character)) continue;

            float d2 = (r.transform.position - myPos).sqrMagnitude;

            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = r;
            }
        }

        return best;
    }

    // 追击目标：朝资源方向走一步，并确保“整堆 bounds 不会越界”
    private Vector3 PickChaseDestinationWithinNormal(CardWorldView root, Vector3 targetPos, float step)
    {
        if (root == null) return targetPos;

        Vector3 start = root.transform.position;
        Vector2 dir = (targetPos - start);
        if (dir.sqrMagnitude < 0.0001f) return start;
        dir.Normalize();

        Vector3 desired = start + (Vector3)(dir * Mathf.Max(0.01f, step));

        var area = root.normalAreaCollider;
        if (area == null) return desired;

        if (!TryGetStackBounds(root, out var sb))
            return desired;

        var ab = area.bounds;

        // 允许的 delta 范围：保证 (stackBounds + delta) 完整落在 ab 内
        Vector3 delta = desired - start;

        float minDx = ab.min.x - sb.min.x;
        float maxDx = ab.max.x - sb.max.x;
        float minDy = ab.min.y - sb.min.y;
        float maxDy = ab.max.y - sb.max.y;

        delta.x = Mathf.Clamp(delta.x, minDx, maxDx);
        delta.y = Mathf.Clamp(delta.y, minDy, maxDy);

        return start + delta;
    }

    // =========================================================
    // 攻击资源：目标是“堆顶那张卡”
    // =========================================================
    private void AttackResourceRoot(CardWorldView resourceTopCard, int damage)
    {
        if (resourceTopCard == null) return;
        if (resourceTopCard.definition == null) return;

        var tags = resourceTopCard.definition.tags;
        if (tags.HasFlag(CardTag.Enemy) || tags.HasFlag(CardTag.Character))
            return;

        var dur = resourceTopCard.GetComponent<ResourceDurability>();
        if (dur == null)
        {
            dur = resourceTopCard.gameObject.AddComponent<ResourceDurability>();
            dur.InitFromDefinition(resourceTopCard.definition);
        }

        dur.TakeDamage(Mathf.Max(1, damage));
    }

    // 资源攻击演出：不走 CombatController（资源不是 ICombatant）
    private void PlayAttackResourceAnimAndDamage(CardWorldView attackerRoot, CardWorldView resourceTopCard, int damage)
    {
        if (attackerRoot == null || resourceTopCard == null) return;

        _resourceAttacking = true;

        // 攻击开始：抬高攻击者堆渲染层级
        attackerRoot.BeginTempFront();

        Vector3 a = attackerRoot.transform.position;
        Vector3 b = resourceTopCard.transform.position;

        Vector2 dir = (b - a);
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
        dir.Normalize();

        // 冲刺但保持“最小间隔”，避免和资源卡位置重叠
        //Vector3 lungePos = a + (Vector3)(dir * resourceAttackLunge);
        float dist = Vector2.Distance(a, b);
        float minGap = Mathf.Max(0.25f, resourceMinGap); // 你可再调
        float maxForward = Mathf.Max(0f, dist - minGap);
        float lunge = Mathf.Min(resourceAttackLunge, maxForward);

        Vector3 lungePos = a + (Vector3)(dir * lunge);

        // 先杀掉自身 transform 上的旧 tween，避免叠加抖动
        attackerRoot.transform.DOKill(false);

        Sequence seq = DOTween.Sequence();
        seq.Append(attackerRoot.transform.DOMove(lungePos, resourceAttackAnimTime).SetEase(Ease.OutQuad));
        seq.AppendCallback(() =>
        {
            // 命中瞬间：资源抖动一下
            if (resourceTopCard != null)
            {
                // 被打的顶牌也可以临时抬一下（可选，但一般更清晰）
                resourceTopCard.BeginTempFront();

                resourceTopCard.transform.DOKill(false);
                resourceTopCard.transform.DOShakePosition(0.12f, 0.08f, 12, 90, false, true);
            }

            // 扣耐久（现在资源 prefab 上已经有 ResourceDurability，正常直接取）
            AttackResourceRoot(resourceTopCard, damage);
        });
        seq.Append(attackerRoot.transform.DOMove(a, resourceAttackAnimTime).SetEase(Ease.InQuad));
        seq.OnComplete(() =>
        {
            _resourceAttacking = false;

            // 攻击结束：恢复渲染层级
            if (attackerRoot != null) attackerRoot.EndTempFront();
            if (resourceTopCard != null) resourceTopCard.EndTempFront();
        });
    }

    // ------------ 接口：给外部读取 ------------
    public void EnsureInitializedNow()
    {
        // definition 可能刚刚 Setup 完
        RefreshFactionFromDefinition();
        ResolveCombatService();

        // 如果此时是 Enemy，确保巡逻方向有值（OnEnable 有做，这里做额外兜底）
        if (_faction == Faction.Enemy && _patrolDir == Vector2.zero)
            _patrolDir = Random.insideUnitCircle.normalized;

        ApplyEnemyParamsFromDefinition();
    }

    public void Pause()
    {
        _paused = true;

        // 1. 停止所有位移 Tween（非常重要）
        var view = _view != null ? _view.GetStackRoot() : null;
        if (view != null)
        {
            view.transform.DOKill(false);
            view.KillAllMovementTweens();
        }
    }

    public void Resume()
    {
        _paused = false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        var v = GetComponent<CardWorldView>();
        if (v == null) return;
        var root = v.GetStackRoot();
        if (root == null) return;

        RefreshFactionFromDefinition();

        Gizmos.color = _faction == Faction.Enemy
            ? new Color(1f, 0.3f, 0.3f, 0.8f)
            : new Color(0.3f, 0.8f, 1f, 0.8f);

        Gizmos.DrawWireSphere(root.transform.position, searchRadius);
        Gizmos.DrawWireSphere(root.transform.position, attackRange);
    }
#endif
}
