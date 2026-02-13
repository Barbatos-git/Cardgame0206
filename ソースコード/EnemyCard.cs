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
    /// 敵カード（戦闘用）
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyCard : MonoBehaviour, ICombatant
    {
        [SerializeField] private EnemyDefinition definition;

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

        public event Action<int> OnHPChanged;
        public event Action OnDied;

        private void Awake()
        {
            _view = GetComponent<CardWorldView>();
        }

        private void OnEnable()
        {
            // 关键：支持“先卡背、后翻面”的延迟初始化
            EnsureInitialized();
        }

        public void EnsureInitialized()
        {
            if (_initialized) return;
            if (_view == null) _view = GetComponent<CardWorldView>();
            if (_view == null || _view.definition == null) return;

            if (!_view.definition.tags.HasFlag(CardTag.Enemy)) return;

            if (definition == null)
                definition = _view.definition.enemyDef;

            if (definition == null)
            {
                Debug.LogError($"[EnemyCard] EnemyDefinition is NULL. card={name}", this);
                return;
            }

            _atk = definition.attack;
            _maxHp = definition.hp;
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

                // 1) 触发死亡事件（给外部系统）
                OnDied?.Invoke();

                // 2) 掉落（如果有 EnemyDropService）
                if (_view == null) _view = GetComponent<CardWorldView>();

                // _view.definition：敌人卡对应的 CardDefinition（来自 card.json）
                var enemyCardDef = _view != null ? _view.definition : null;

                // definition：EnemyDefinition（SO）
                if (EnemyDropService.Instance != null)
                {
                    EnemyDropService.Instance.SpawnEnemyDrops(
                        transform.position,
                        definition
                    );
                }

                // 3) kill tween + destroy
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
    }
}
