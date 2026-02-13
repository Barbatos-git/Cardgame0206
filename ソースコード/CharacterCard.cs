using System;
using UnityEngine;
using CardGame.Core.Combat;
using CardGame.Data.Defs;
using CardGame.Gameplay.Common;
using CardGame.Gameplay.Combat;
using DG.Tweening;

namespace CardGame.Gameplay.Units
{
    /// <summary>
    /// 人物カード（武器装備で戦士化）
    /// 探索/採集/戦闘の「実体」
    /// 
    /// 仕様:
    /// - 武器は1本のみ装備可能
    /// - 装備は「武器カード1枚のみ」許可（武器スタックは装備不可）
    /// - 新しい武器を装備すると置き換え（旧武器は CardWorldView 側でカード化して弾き出す）
    /// - 最終攻撃力は武器依存（装備中：weapon.attackBonus を攻撃力として採用）
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CharacterCard : MonoBehaviour, ICombatant
    {
        [Header("Defs")]
        [SerializeField] private CharacterDefinition definition;

        [Header("Runtime")]
        [SerializeField] private WeaponDefinition equippedWeapon; // nullなら一般人
        [SerializeField] private string equippedWeaponCardId;     // 装備中の武器カードID（CardDefinition.id）

        private CardWorldView _view;
        private bool _initialized;

        private int _hp;
        private int _maxHp;
        private int _atk;

        public string DebugName => definition != null ? definition.displayName : name;
        public int Attack => _atk;
        public int MaxHP => _maxHp;
        public int HP => _hp;
        public bool IsDead => _hp <= 0;
        public bool CanAttack => !IsDead;
        public bool HasWeaponEquipped => equippedWeapon != null;
        public string EquippedWeaponCardId => equippedWeaponCardId;
        public WeaponDefinition EquippedWeapon => equippedWeapon;

        public event Action<int> OnHPChanged;
        public event Action OnDied;

        /// <summary>装備が変化した時に呼ばれる（CardWorldView/Visual 側が購読して見た目更新する）</summary>
        public event Action OnEquipmentChanged;

        private void Awake()
        {
            _view = GetComponent<CardWorldView>();          
        }

        private void OnEnable()
        {
            // 关键：支持“先卡背、后翻面”的延迟初始化
            EnsureInitialized();
        }

        /// <summary>
        /// 外部（Installer/翻面/运行时）可调用：确保人物属性已根据 definition 初始化
        /// </summary>
        public void EnsureInitialized()
        {
            if (_initialized) return;
            if (_view == null) _view = GetComponent<CardWorldView>();
            if (_view == null || _view.definition == null) return;

            // 不是人物卡：不初始化（但不再在 Awake 里永久禁用）
            if (!_view.definition.tags.HasFlag(CardTag.Character)) return;

            if (definition == null)
                definition = _view.definition.characterDef;

            if (definition == null)
            {
                Debug.LogError($"[CharacterCard] CharacterDefinition is NULL. card={name}", this);
                return;
            }

            // 初始：未装备
            equippedWeapon = null;
            equippedWeaponCardId = null;

            _atk = definition.baseAttack;
            _maxHp = definition.baseHP;
            _hp = _maxHp;

            _initialized = true;
            OnHPChanged?.Invoke(_hp);
        }

        public void TakeDamage(int amount, CardGame.Core.Combat.DamageType type = CardGame.Core.Combat.DamageType.Normal)
        {
            if (!_initialized) EnsureInitialized();
            if (!_initialized || IsDead) return;

            amount = Mathf.Max(0, amount);
            _hp -= amount;
            OnHPChanged?.Invoke(_hp);

            if (_hp <= 0)
            {
                _hp = 0;
                OnHPChanged?.Invoke(_hp);
                OnDied?.Invoke();
                DOTween.Kill(transform);
                Destroy(gameObject);
            }
        }

        public void Heal(int amount)
        {
            if (!_initialized) EnsureInitialized();
            if (!_initialized || IsDead) return;

            amount = Mathf.Max(0, amount);
            _hp = Mathf.Min(_maxHp, _hp + amount);
            OnHPChanged?.Invoke(_hp);
        }

        // ===== 装备武器：从 CardDefinition.weaponDef 传入 =====

        /// <summary>
        /// 武器装備（单体武器卡のみ許可）
        /// replacedWeaponCardId：如果发生替换，这里输出旧武器卡 id（否则 null）
        /// </summary>
        public bool TryEquipWeapon(CardDefinition weaponCardDef, int weaponStackCount, out string replacedWeaponCardId)
        {
            replacedWeaponCardId = null;

            if (!_initialized) EnsureInitialized();
            if (!_initialized || IsDead) return false;

            if (weaponCardDef == null) return false;
            if (!weaponCardDef.tags.HasFlag(CardTag.Weapon)) return false;
            if (weaponCardDef.weaponDef == null) return false;

            // 规格：武器堆叠不可直接装备（必须是单张）
            if (weaponStackCount != 1) return false;

            // 置换旧武器
            if (!string.IsNullOrEmpty(equippedWeaponCardId))
                replacedWeaponCardId = equippedWeaponCardId;

            equippedWeapon = weaponCardDef.weaponDef;
            equippedWeaponCardId = weaponCardDef.id;

            RecomputeStatsFromDefs();
            OnEquipmentChanged?.Invoke();
            return true;
        }

        public void UnequipWeapon()
        {
            if (!_initialized) EnsureInitialized();
            if (!_initialized) return;

            equippedWeapon = null;
            equippedWeaponCardId = null;
            RecomputeStatsFromDefs();
            OnEquipmentChanged?.Invoke();
        }

        private void RecomputeStatsFromDefs()
        {
            if (definition == null) return;

            // 最终攻击力取决于武器（未装备则 baseAttack）
            _atk = (equippedWeapon != null) ? Mathf.Max(0, equippedWeapon.attackBonus) : definition.baseAttack;

            // HP：base + bonus（没要求“HP也取决于武器”，所以保持 bonus 叠加逻辑）
            _maxHp = definition.baseHP + (equippedWeapon != null ? equippedWeapon.hpBonus : 0);

            _maxHp = Mathf.Max(1, _maxHp);
            _hp = Mathf.Clamp(_hp, 0, _maxHp);
            OnHPChanged?.Invoke(_hp);
        }
    }
}
