using System;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using CardGame.Core.Cards;
using CardGame.Gameplay.Units;
using CardGame.Core.Pause;
using CardGame.Gameplay.Cards;

namespace CardGame.Gameplay.Zones
{
    /// <summary>
    /// 探索区：人物が入ると一定間隔で「探索結果」を発生
    /// 人物卡可放置（上限），越多人探索越快。
    /// 遭遇卡（卡背）从右往左移动，触碰人物则翻面并掉落到普通区中心范围。
    /// </summary>
    public sealed class ExplorationZone : ZoneBase, IPausable, IResettable
    {
        [Header("容量")]
        [SerializeField] private int maxCharacters = 3;

        [Header("レーン基準点")]
        [SerializeField] private Transform leftAnchor;   // 人物从这里开始排
        [SerializeField] private Transform rightAnchor;  // 遭遇从这里出生

        [Header("レーンコライダー（Anchor自動追従用）")]
        [SerializeField] private Collider2D laneCollider; // 探索区实际的 Trigger Collider（推荐拖子物体上的那个）
        [SerializeField] private bool autoUpdateAnchorsFromCollider = true;

        // Anchor 距离边界的内缩（防止贴边太死）
        [SerializeField, Min(0f)] private float anchorInsetX = 0.2f;

        // Anchor 的 Y 使用 bounds.center.y（推荐），否则保持 Anchor 自己的 y
        [SerializeField] private bool anchorUseBoundsCenterY = true;

        [Header("人物排列")]
        [SerializeField] private float characterSpacingX = 1.35f;
        [SerializeField] private float snapDuration = 0.18f;

        [Header("探索速度")]
        [SerializeField] private float baseInterval = 6f;   // 1人时的间隔
        [SerializeField] private bool fasterWithMoreCharacters = true;

        [Header("遭遇表现")]
        [SerializeField] private CardWorldView encounterCardPrefab; // 通用卡牌 prefab
        [SerializeField] private Sprite encounterBackSprite;        // 卡背 sprite
        [SerializeField] private float encounterMoveDuration = 2.8f;
        [SerializeField] private float flipDuration = 0.18f;

        [Header("遭遇生成概率")]
        [Range(0f, 1f)]
        [SerializeField] private float encounterSpawnChance = 1.0f; // 每次 tick 生成一次遭遇的概率

        [Header("随机素材掉落表（按权重抽）")]
        [SerializeField] private List<LootEntry> lootTable = new();

        [Header("稀有：探索掉落人物/敌人（很小概率）")]
        [SerializeField, Range(0f, 1f)]
        private float rareCharacterChance = 0.02f; // 2% 之类

        [SerializeField, Range(0f, 1f)]
        private float rareEnemyChance = 0.01f;     // 1% 之类（建议比人物更低）

        [Serializable]
        private class RareEntry
        {
            public CardDefinition def;
            [Min(0f)] public float weight = 1f; // 越小越稀有
        }

        [SerializeField] private List<RareEntry> rareCharacterPool = new();
        [SerializeField] private List<RareEntry> rareEnemyPool = new();

        [Header("全局人物数量上限（包含场景初始2人 + 后续探索获得）")]
        [SerializeField] private int maxTotalCharactersInGame = 4;

        [Serializable]
        private class LootEntry
        {
            public CardDefinition def;
            [Min(0f)] public float weight = 1f;
        }

        [Header("掉落范围")]
        [SerializeField] private float dropRadius = 1.6f;

        [Header("碰撞判定（自动读取 Collider）")]
        [SerializeField] private LayerMask characterLayer;
        [SerializeField, Range(1.0f, 1.3f)]
        private float hitBoxScale = 1.05f;

        [SerializeField, Range(0.01f, 0.2f)]
        private float hitProbeWidthRatio = 0.06f; // 前缘探针宽度：卡宽的 6%


        public event Action<CharacterCard> OnExplorationTick; // 你原有事件保持

        private float _timeToNextTick = 0f;

        // 缓存：探索区内人物（按进入顺序）
        private readonly List<CardWorldView> _characters = new();

        // 吸附改为“松手后”才执行：进入/离开只标记 dirty
        private bool _snapDirty = false;

        // 缓存：自动读取到的命中框尺寸
        private Vector2 _encounterHitBoxSize;

        private bool _paused;

        // 让探索区能统一 Pause/Resume 自己产生的 Tween
        private readonly HashSet<string> _activeEncounterMoveIds = new HashSet<string>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            CacheEncounterHitBoxSize();
            RefreshLaneAnchorsIfNeeded(true);
        }

        private void CacheEncounterHitBoxSize()
        {
            _encounterHitBoxSize = Vector2.one;

            if (encounterCardPrefab == null)
            {
                Debug.LogWarning("[ExplorationZone] encounterCardPrefab 未设置");
                return;
            }

            var box = encounterCardPrefab.GetComponent<BoxCollider2D>();
            if (box == null)
            {
                Debug.LogWarning("[ExplorationZone] 遭遇卡 prefab 上没有 BoxCollider2D");
                return;
            }

            _encounterHitBoxSize = box.size * hitBoxScale;
        }

        private void OnValidate()
        {
            if (!Application.isPlaying)
            {
                CacheEncounterHitBoxSize();
                RefreshLaneAnchorsIfNeeded(true);
            }
        }

        private void Update()
        {
            if (_paused) return;
            // 必须：已松手 + 吸附完成，才允许探索
            bool readyToExplore = _characters.Count > 0 && !_snapDirty && !AnyCharacterDragging();
            if (!readyToExplore)
            {
                _timeToNextTick = 0f; // 不就绪时不累计
                return;
            }

            float interval = GetEffectiveInterval();

            // 第一次进入“可探索状态”时，初始化剩余时间
            if (_timeToNextTick <= 0f)
                _timeToNextTick = interval;

            _timeToNextTick -= UnityEngine.Time.deltaTime;
            if (_timeToNextTick > 0f) return;

            // 触发一次 tick 后，重置为 interval（不会补发多次）
            _timeToNextTick = interval;

            // 每次 tick：对每个人物触发事件
            for (int i = 0; i < _characters.Count; i++)
            {
                var view = _characters[i];
                if (view == null) continue;

                var character = view.GetComponent<CharacterCard>();
                if (character == null) continue;
                if (character.IsDead) continue;

                OnExplorationTick?.Invoke(character);
            }

            if (_characters.Count > 0 && UnityEngine.Random.value <= encounterSpawnChance)
            {
                SpawnEncounterVisual();
            }
        }

        private void LateUpdate()
        {
            RefreshLaneAnchorsIfNeeded(false);

            // 只在“没有任何人被拖拽”时才吸附
            if (!_snapDirty) return;
            if (AnyCharacterDragging()) return;

            _snapDirty = false;
            SnapCharactersToLane();
        }

        public override bool CanEnter(ICardEntity card)
        {
            if (card is not CardWorldView view) return false;
            if (view.definition == null) return false;

            // 只能人物卡
            if (!view.definition.tags.HasFlag(CardTag.Character)) return false;

            // 上限
            if (_characters.Count >= maxCharacters) return false;

            return true;
        }

        public bool TryAcceptCharacter(CardWorldView view)
        {
            if (view == null) return false;
            if (!CanEnter(view)) return false;

            // 模拟 enter
            OnEnterInternal(view);
            return true;
        }

        protected override void OnEnterInternal(ICardEntity card)
        {
            var view = card as CardWorldView;
            if (view == null) return;

            if (!_characters.Contains(view))
                _characters.Add(view);

            // 不要“进入就吸附”，标记 dirty，等松手后 LateUpdate 吸附
            _snapDirty = true;
        }

        protected override void OnExitInternal(ICardEntity card)
        {
            var view = card as CardWorldView;
            if (view == null) return;

            _characters.Remove(view);

            // 同上：等松手后再吸附重排
            _snapDirty = true;
        }

        private bool AnyCharacterDragging()
        {
            for (int i = 0; i < _characters.Count; i++)
            {
                var c = _characters[i];
                if (c == null) continue;

                // 关键：拖拽中不吸附，避免“没松手就吸走”
                if (c.IsDraggingRoot) return true;
            }
            return false;
        }

        private float GetEffectiveInterval()
        {
            if (!fasterWithMoreCharacters) return baseInterval;
            int n = Mathf.Max(1, _characters.Count);
            return baseInterval / n; // 人越多越快
        }

        private void SnapCharactersToLane()
        {
            if (leftAnchor == null) return;

            for (int i = 0; i < _characters.Count; i++)
            {
                var c = _characters[i];
                if (c == null) continue;

                // 再兜底一次：如果某个正在拖拽，跳过
                if (c.IsDraggingRoot) continue;

                Vector3 target = leftAnchor.position + new Vector3(characterSpacingX * i, 0f, 0f);

                // 用 DOTween 吸附
                c.transform.DOKill(false);
                c.transform.DOMove(target, snapDuration).SetEase(Ease.OutQuad);

                // 之前强调的兜底 Clamp（如果人物也需要遵守普通区边界，可按你规则决定）
                // c.ClampStackToNormalArea();
            }
        }

        private Vector2 _lastLaneSize = new Vector2(-1, -1);

        private void RefreshLaneAnchorsIfNeeded(bool force)
        {
            if (!autoUpdateAnchorsFromCollider) return;

            if (laneCollider == null)
            {
                // 兜底：自己找（优先找子物体的 ExplorationLaneAreaTrigger2D 的 Collider）
                var trigger = GetComponentInChildren<ExplorationLaneAreaTrigger2D>();
                if (trigger != null) laneCollider = trigger.GetComponent<Collider2D>();
                if (laneCollider == null) laneCollider = GetComponent<Collider2D>();
            }

            if (laneCollider == null) return;
            if (leftAnchor == null || rightAnchor == null) return;

            Bounds b = laneCollider.bounds;

            // 如果 collider 尺寸没变化就不更新（省性能）
            Vector2 size = new Vector2(b.size.x, b.size.y);
            if (!force && size == _lastLaneSize) return;
            _lastLaneSize = size;

            float y = anchorUseBoundsCenterY ? b.center.y : leftAnchor.position.y;

            // 左右锚点贴边 + 内缩
            Vector3 left = new Vector3(b.min.x + anchorInsetX, y, leftAnchor.position.z);
            Vector3 right = new Vector3(b.max.x - anchorInsetX, y, rightAnchor.position.z);

            leftAnchor.position = left;
            rightAnchor.position = right;
        }


        private void SpawnEncounterVisual()
        {
            if (encounterCardPrefab == null) return;
            if (rightAnchor == null || leftAnchor == null) return;
            if (encounterBackSprite == null) return;

            Tween moveTween = null;

            // 生成卡背
            var back = Instantiate(encounterCardPrefab, rightAnchor.position, Quaternion.identity);

            // 关键：移动中的遭遇卡禁止鼠标点击（防止 CardWorldView.OnMouseDown -> DOComplete -> OnComplete 触发掉落）
            int ignore = LayerMask.NameToLayer("Ignore Raycast");
            if (ignore >= 0) back.gameObject.layer = ignore;

            // 背面只换 sprite，不调用 Setup（避免装身份/逻辑）
            if (back.artworkRenderer != null)
                back.artworkRenderer.sprite = encounterBackSprite;

            // 目标：移动到左侧（或移动到第一个人物位置）
            Vector3 end = leftAnchor.position;

            back.transform.DOComplete();
            var moveId = $"EncounterMove_{back.GetInstanceID()}";
            var token = back.gameObject.AddComponent<EncounterMoveToken>();
            token.moveId = moveId;

            _activeEncounterMoveIds.Add(moveId);

            moveTween = back.transform.DOMove(end, encounterMoveDuration)
                .SetId(moveId)
                .SetTarget(this)   // 关键：让 DOTween.Pause(this)/Play(this) 能控制到它
                .SetEase(Ease.Linear)
                .OnUpdate(() =>
                {
                    if (_paused) return;    // 暂停时不做碰撞判定（避免暂停期间触发掉落）
                    if (back == null) return;

                    // 若途中已无人物，直接继续移动到左边并掉落
                    if (_characters.Count == 0) return;

                    // 真正碰到人物才翻面
                    if (HasHitAnyCharacter(back, out var hitChar))
                    {
                        // 防止重复触发
                        if (back.GetComponent<EncounterResolved>() != null) return;
                        back.gameObject.AddComponent<EncounterResolved>();

                        // 停在“当前碰撞位置”，不要补完到终点
                        DOTween.Kill(moveId, false);

                        // 翻面/掉落以“命中人物位置”为中心
                        var normalCenter = FindNormalAreaCenterFallback();
                        FlipAndDropToNormal(back, hitChar.transform.position, normalCenter);
                    }
                })
                .OnComplete(() =>
                {
                    if (back == null) return;

                    // 如果已经处理过（在 OnUpdate 碰到人物触发了），这里直接 return
                    if (back.GetComponent<EncounterResolved>() != null) return;

                    // 没碰到人物也到头了：直接掉落
                    back.gameObject.AddComponent<EncounterResolved>();
                    var normalCenter = FindNormalAreaCenterFallback();
                    FlipAndDropToNormal(back, back.transform.position, normalCenter);
                });
        }

        private bool HasHitAnyCharacter(CardWorldView back, out CardWorldView hitCharacter)
        {
            hitCharacter = null;
            if (back == null) return false;
            if (_characters.Count == 0) return false;

            // 用遭遇卡自己的 BoxCollider2D 来取 center（比 transform.position 更准）
            var backCol = back.GetComponent<BoxCollider2D>();
            if (backCol == null) return false;

            // 遭遇卡的 bounds（用 collider bounds 更准）
            Bounds backBounds = backCol.bounds;

            // 遭遇卡从右往左移动：第一个遇到的必然是“x 最大”的那个碰撞对象
            float bestX = float.NegativeInfinity;

            for (int i = 0; i < _characters.Count; i++)
            {
                var c = _characters[i];
                if (c == null) continue;

                // 只认人物卡（防止列表脏数据）
                if (c.definition == null || !c.definition.tags.HasFlag(CardTag.Character))
                    continue;

                var charCol = c.GetComponent<BoxCollider2D>();
                if (charCol == null) continue;

                // 注意：不依赖 charCol.enabled（哪怕被禁用也能拿 bounds，不过 bounds 可能是旧值）
                Bounds charBounds = charCol.bounds;

                // 纯几何相交：只要 bounds 相交就算碰到
                if (!backBounds.Intersects(charBounds))
                    continue;

                // 选择“最先碰到的那个”：从右往左 => x 最大的那张先碰到
                float x = charBounds.center.x;
                if (x > bestX)
                {
                    bestX = x;
                    hitCharacter = c;
                }
            }

            return hitCharacter != null;
        }


        private void FlipAndDropToNormal(CardWorldView back, Vector3 flipWorldPos, Vector3 dropCenterWorld)
        {
            if (back == null) return;

            // 彻底停掉这张卡身上的所有 tween，保留当前姿态/位置
            back.transform.DOKill(false);

            // 空投模式：掉落期间禁用碰撞/AI/人物敌人逻辑，避免被截停
            //SetAirDropMode(back, true);

            var token = back.GetComponent<EncounterMoveToken>();
            if (token != null && !string.IsNullOrEmpty(token.moveId))
            {
                DOTween.Kill(token.moveId, false); // 只杀移动，不会杀掉落
            }

            // 抽一次随机素材
            var dropDef = PickEncounterResultDefinition();
            if (dropDef == null)
            {
                // 没配置表就直接销毁，避免留空白卡
                Destroy(back.gameObject);
                return;
            }

            // 统一走通用空投（翻面 + Setup + 掉落 + 不截停）
            CardAirdropUtility.FlipSetupAndDrop(
                back,
                dropDef,
                flipWorldPos,
                dropCenterWorld,
                dropRadius,
                flipDuration
            );
        }

        private CardDefinition PickRandomLoot()
        {
            if (lootTable == null || lootTable.Count == 0) return null;

            float total = 0f;
            for (int i = 0; i < lootTable.Count; i++)
            {
                var e = lootTable[i];
                if (e == null || e.def == null) continue;
                if (e.weight <= 0f) continue;
                total += e.weight;
            }
            if (total <= 0f) return null;

            float r = UnityEngine.Random.value * total;
            float acc = 0f;

            for (int i = 0; i < lootTable.Count; i++)
            {
                var e = lootTable[i];
                if (e == null || e.def == null) continue;
                if (e.weight <= 0f) continue;

                acc += e.weight;
                if (r <= acc) return e.def;
            }

            // 兜底
            for (int i = lootTable.Count - 1; i >= 0; i--)
            {
                if (lootTable[i] != null && lootTable[i].def != null && lootTable[i].weight > 0f)
                    return lootTable[i].def;
            }
            return null;
        }

        private static CardDefinition PickWeighted(List<RareEntry> list)
        {
            if (list == null || list.Count == 0) return null;

            float total = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.def == null) continue;
                if (e.weight <= 0f) continue;
                total += e.weight;
            }
            if (total <= 0f) return null;

            float r = UnityEngine.Random.value * total;
            float acc = 0f;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.def == null) continue;
                if (e.weight <= 0f) continue;

                acc += e.weight;
                if (r <= acc) return e.def;
            }

            // 兜底：取最后一个有效项
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e != null && e.def != null && e.weight > 0f) return e.def;
            }
            return null;
        }


        private CardDefinition PickEncounterResultDefinition()
        {
            // 1) 先抽“人物”
            if (rareCharacterPool != null && rareCharacterPool.Count > 0)
            {
                if (CountAliveCharactersInGame() < maxTotalCharactersInGame)
                {
                    if (UnityEngine.Random.value <= rareCharacterChance)
                    {
                        var def = PickWeighted(rareCharacterPool);
                        if (def != null) return def;
                    }
                }
            }

            // 2) 再抽“敌人”
            if (rareEnemyPool != null && rareEnemyPool.Count > 0)
            {
                if (UnityEngine.Random.value <= rareEnemyChance)
                {
                    var def = PickWeighted(rareEnemyPool);
                    if (def != null) return def;
                }
            }

            // 3) 否则走原本资源掉落表
            return PickRandomLoot();
        }


        private Vector3 FindNormalAreaCenterFallback()
        {
            var areaObj = GameObject.FindWithTag("NormalArea");
            if (areaObj != null)
            {
                var col = areaObj.GetComponent<Collider2D>();
                if (col != null) return col.bounds.center;
                return areaObj.transform.position;
            }
            return Vector3.zero;
        }

        private int CountAliveCharactersInGame()
        {
            int count = 0;
            var list = CardWorldView.CardWorldViewRegistry;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v == null || v.definition == null) continue;

                // 只数人物卡（堆里也只算一次：算 root）
                var root = v.GetStackRoot();
                if (root != v) continue;

                if (!root.definition.tags.HasFlag(CardTag.Character)) continue;

                var cc = root.GetComponent<CharacterCard>();
                if (cc != null && !cc.IsDead) count++;
            }
            return count;
        }


        private sealed class EncounterMoveToken : MonoBehaviour
        {
            public string moveId;
        }

        public static ExplorationZone Instance { get; private set; }

        private void OnEnable() => Instance = this;
        private void OnDisable() { if (Instance == this) Instance = null; }

        private sealed class EncounterResolved : MonoBehaviour { }

        public void Pause()
        {
            _paused = true;

            // 暂停这个 Zone 上挂着的 Tween（Snap/Encounter/Flip/Drop 都会停）
            DOTween.Pause(this);

            // 兜底：把所有正在移动的遭遇卡 tween 按 id 也暂停
            foreach (var id in _activeEncounterMoveIds)
                DOTween.Pause(id);
        }

        public void Resume()
        {
            _paused = false;

            DOTween.Play(this);

            foreach (var id in _activeEncounterMoveIds)
                DOTween.Play(id);
        }

        public void ResetState()
        {
            // 1) 停止探索区自己管理的所有 tween（Snap / EncounterMove / Flip / Drop 等）
            DOTween.Kill(this);

            // 2) 兜底：把正在移动的遭遇 tween 按 id Kill（避免残留回调）
            foreach (var id in _activeEncounterMoveIds)
                DOTween.Kill(id, false);
            _activeEncounterMoveIds.Clear();

            // 3) 清掉探索区内人物缓存
            _characters.Clear();

            // 4) 重置计时与状态
            _timeToNextTick = 0f;
            _snapDirty = false;
            _paused = false;

            // 5) 让下一局重新计算 collider 尺寸变化
            _lastLaneSize = new Vector2(-1, -1);

            // 6) 清单例
            if (Instance == this)
                Instance = null;
        }
    }
}
