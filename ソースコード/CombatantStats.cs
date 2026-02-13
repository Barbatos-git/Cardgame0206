using System;
using CardGame.Core.Combat;

namespace CardGame.Gameplay.Combat
{
    /// <summary>
    /// ICombatant 共通ステータス（HP管理 + 攻撃力は「唯一の真実」から取得）
    ///
    /// 設計方針：
    /// - 攻撃力の唯一の真実は ICombatant.Attack。
    /// - CombatantStats は Attack を「最終値として保持しない」。
    ///   => AttackProvider（Func<int>）を優先し、なければ静的値を fallback とする。
    ///
    /// これにより：
    /// - 装備/バフ/状態異常で攻撃力が変わっても、常に最新値が参照される
    /// - 「装備したのにダメージが変わらない」問題を根絶できる
    /// </summary>
    public sealed class CombatantStats : ICombatant
    {
        public string DebugName { get; private set; }

        // ---- 攻撃力：唯一の真実を参照するための Provider ----
        private Func<int> _attackProvider;   // ある場合は常にこれを採用
        private int _fallbackAttack;         // Provider がない場合のみ使用（互換用）

        // ---- HP ----
        public int MaxHP { get; private set; }
        public int HP { get; private set; }
        public bool IsDead => HP <= 0;

        public bool CanAttack => !IsDead; // 今は簡易。後でターン制にするなら拡張。

        /// <summary>
        /// 最終攻撃力（唯一の真実）
        /// Provider がある場合：Provider() の値
        /// Provider がない場合：_fallbackAttack
        /// </summary>
        public int Attack
        {
            get
            {
                int v = (_attackProvider != null) ? _attackProvider.Invoke() : _fallbackAttack;
                // 念のため負数を防ぐ（バフ/デバフで事故りやすい）
                return Math.Max(0, v);
            }
        }

        public event Action<int> OnHPChanged;
        public event Action OnDied;

        public CombatantStats(string debugName, int fallbackAttack, int maxHp)
        {
            DebugName = debugName;
            _fallbackAttack = Math.Max(0, fallbackAttack);
            MaxHP = Math.Max(1, maxHp);
            HP = MaxHP;
        }

        /// <summary>
        /// 攻撃力の唯一の真実（Provider）を差し替える。
        /// 例：武器装備/解除のたびに Provider を変える必要はない。
        ///     Provider が「現在の武器」を参照していれば常に最新になる。
        /// </summary>
        public void BindAttackProvider(Func<int> provider)
        {
            _attackProvider = provider;
        }

        /// <summary>
        /// 互換用：Provider がない場合の fallback 値を更新する。
        /// ※最終攻撃力を固定したい敵などで利用可能
        /// </summary>
        public void SetAttack(int value)
        {
            _fallbackAttack = Math.Max(0, value);
        }


        public void SetMaxHP(int value, bool healToFull)
        {
            MaxHP = Math.Max(1, value);
            if (healToFull) HP = MaxHP;
            else HP = Math.Min(HP, MaxHP);

            OnHPChanged?.Invoke(HP);
        }

        public void TakeDamage(int amount, DamageType type = DamageType.Normal)
        {
            if (IsDead) return;
            amount = Math.Max(0, amount);
            if (amount == 0) return;
            HP -= amount;
            OnHPChanged?.Invoke(HP);
            if (HP <= 0)
            {
                HP = 0;
                OnHPChanged?.Invoke(HP);
                OnDied?.Invoke();
            }
        }

        public void Heal(int amount)
        {
            if (IsDead) return;
            amount = Math.Max(0, amount);
            if (amount == 0) return;
            HP = Math.Min(MaxHP, HP + amount);
            OnHPChanged?.Invoke(HP);
        }
    }
}
