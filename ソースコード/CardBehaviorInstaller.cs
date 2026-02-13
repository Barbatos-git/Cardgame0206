using CardGame.Gameplay.Units;
using UnityEngine;

public sealed class CardBehaviorInstaller : MonoBehaviour
{
    [Header("Required")]
    [SerializeField] private CardWorldView view;

    [Header("Optional: Fusion Zone Spawn Settings")]
    [SerializeField] private GameObject fusionZonePrefab;
    [SerializeField] private Vector3 fusionZoneOffset = new Vector3(1.6f, 0f, 0f);
    [SerializeField] private bool fusionZoneFollowCard = true;

    // cached refs (may be null, we will Ensure on Install)
    private CharacterCard _characterCard;
    private EnemyCard _enemyCard;
    private EquipmentReceiver _equipmentReceiver;
    private AutoCombatAgent _autoCombatAgent;
    private ResourceDurability _resourceDurability;
    private FusionZoneCardSpawner _fusionZoneSpawner;

    private void Reset()
    {
        view = GetComponent<CardWorldView>();
    }

    private void Awake()
    {
        if (view == null) view = GetComponent<CardWorldView>();

        // cache only
        _characterCard = GetComponent<CharacterCard>();
        _enemyCard = GetComponent<EnemyCard>();
        _equipmentReceiver = GetComponent<EquipmentReceiver>();
        _autoCombatAgent = GetComponent<AutoCombatAgent>();
        _resourceDurability = GetComponent<ResourceDurability>();
        _fusionZoneSpawner = GetComponent<FusionZoneCardSpawner>();
    }

    /// <summary>
    /// 在 CardWorldView.Setup(def) 之后调用，按 definition.tags 启用/禁用通用 prefab 上的组件。
    /// </summary>
    public void Install()
    {
        if (view == null) view = GetComponent<CardWorldView>();
        if (view == null || view.definition == null)
        {
            Debug.LogError("[CardBehaviorInstaller] CardWorldView or definition is missing.", this);
            return;
        }

        var tags = view.definition.tags;

        bool isCharacter = tags.HasFlag(CardTag.Character);
        bool isEnemy = tags.HasFlag(CardTag.Enemy);
        bool isWeapon = tags.HasFlag(CardTag.Weapon);

        // ---- 规则：每张卡只允许一种“单位身份”生效 ----
        // Character 卡：启用 CharacterCard + EquipmentReceiver + AutoCombatAgent(Friendly)
        // 新增的规则：某张卡（按 tag）会生成合成区
        bool createsFusionZone = tags.HasFlag(CardTag.FusionZoneSource);

        // --- Ensure components based on tags ---
        // 单位身份互斥：Character / Enemy
        if (isCharacter)
        {
            _characterCard = Ensure<CharacterCard>(_characterCard);
            _equipmentReceiver = Ensure<EquipmentReceiver>(_equipmentReceiver);
            _autoCombatAgent = Ensure<AutoCombatAgent>(_autoCombatAgent);

            SetEnabled(_characterCard, true);
            SetEnabled(_equipmentReceiver, true);
            SetEnabled(_autoCombatAgent, true);

            // enemy off
            if (_enemyCard != null) SetEnabled(_enemyCard, false);

            // resource off
            if (_resourceDurability != null) SetEnabled(_resourceDurability, false);

            _characterCard?.EnsureInitialized();
            _autoCombatAgent?.EnsureInitializedNow();

            // Fusion zone spawner (optional)
            HandleFusionZone(createsFusionZone);

            bool isVillager = tags.HasFlag(CardTag.Villager);

            // 村民：自动安装“村民标记 + 生命追踪”
            if (isVillager)
            {
                Ensure<VillagerMarker>(null);          // 确保存在
                Ensure<VillagerLifeReporter>(null);    // 确保存在
            }
            else
            {
                // 非村民的 Character，确保不会误判
                var marker = GetComponent<VillagerMarker>();
                if (marker != null) Destroy(marker);

                var reporter = GetComponent<VillagerLifeReporter>();
                if (reporter != null) Destroy(reporter);
            }

            return;
        }

        // Enemy 卡：启用 EnemyCard + AutoCombatAgent(Enemy)，禁用 EquipmentReceiver
        if (isEnemy)
        {
            _enemyCard = Ensure<EnemyCard>(_enemyCard);
            _autoCombatAgent = Ensure<AutoCombatAgent>(_autoCombatAgent);

            SetEnabled(_enemyCard, true);
            SetEnabled(_autoCombatAgent, true);

            // character/equipment off
            if (_characterCard != null) SetEnabled(_characterCard, false);
            if (_equipmentReceiver != null) SetEnabled(_equipmentReceiver, false);

            // resource off
            if (_resourceDurability != null) SetEnabled(_resourceDurability, false);

            _enemyCard?.EnsureInitialized();
            _autoCombatAgent?.EnsureInitializedNow();

            // Fusion zone spawner (optional)
            HandleFusionZone(createsFusionZone);

            return;
        }

        // --- Non-unit cards ---
        // combat / ai off
        if (_characterCard != null) SetEnabled(_characterCard, false);
        if (_enemyCard != null) SetEnabled(_enemyCard, false);
        if (_equipmentReceiver != null) SetEnabled(_equipmentReceiver, false);
        if (_autoCombatAgent != null) SetEnabled(_autoCombatAgent, false);

        // resource durability on (unless it’s a weapon etc.)
        // 原逻辑：资源耐久挂在通用prefab，非单位就启用
        _resourceDurability = Ensure<ResourceDurability>(_resourceDurability);
        SetEnabled(_resourceDurability, true);
        _resourceDurability.InitFromDefinition(view.definition);

        // 武器卡：不需要 AI，但可用于装备交互（已有体系）
        // isWeapon 这里留给后续扩展

        // Fusion zone spawner (optional)
        HandleFusionZone(createsFusionZone);
    }

    private void HandleFusionZone(bool createsFusionZone)
    {
        _fusionZoneSpawner = Ensure<FusionZoneCardSpawner>(_fusionZoneSpawner);

        if (!createsFusionZone)
        {
            _fusionZoneSpawner.Deactivate();
            return;
        }

        if (fusionZonePrefab == null)
        {
            Debug.LogError($"[Installer] FusionZoneSource card '{view.definition.id}' but fusionZonePrefab is NULL. Please assign on CardBehaviorInstaller prefab.", this);
            _fusionZoneSpawner.Deactivate();
            return;
        }

        _fusionZoneSpawner.Activate(fusionZonePrefab);
    }

    private static void SetEnabled(Behaviour b, bool enabled)
    {
        if (b != null) b.enabled = enabled;
    }

    private T Ensure<T>(T cached) where T : Component
    {
        if (cached != null) return cached;
        var c = GetComponent<T>();
        if (c != null) return c;
        return gameObject.AddComponent<T>();
    }
}
