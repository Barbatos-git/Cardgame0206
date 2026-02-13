using CardGame.Core.Combat;
using System;

namespace CardGame.Gameplay.Combat
{
    /// <summary>
    /// 戦闘解決（演出とは分離）
    /// ルール：攻撃者→防御者のみダメージ
    ///
    /// 設計方針：
    /// - 最終攻撃力は ICombatant.Attack（唯一の真実）から取得する
    /// - HP変更は defender.TakeDamage に委譲する（HP管理は Combatant 側）
    /// </summary>
    public sealed class CombatResolver
    {
        /// <summary>
        /// 将来の拡張用：ダメージ計算を外部から差し替えたい場合に使用
        /// 例）クリティカル、属性、装甲、スキル倍率など
        /// </summary>
        public Func<ICombatant, ICombatant, int, int> DamageFormula;

        public AttackResult Resolve(AttackCommand cmd)
        {
            var a = cmd.Attacker;
            var d = cmd.Defender;

            if (a == null || d == null) return new AttackResult(0, 0, false, false);
            if (!a.CanAttack || a.IsDead || d.IsDead) return new AttackResult(0, 0, d.IsDead, a.IsDead);

            // 攻撃力の唯一の真実：ICombatant.Attack
            int baseDamage = Math.Max(0, a.Attack);

            // 将来拡張：式があるならそれを使う
            int finalDamage = baseDamage;
            if (DamageFormula != null)
            {
                finalDamage = DamageFormula.Invoke(a, d, baseDamage);
                finalDamage = Math.Max(0, finalDamage);
            }

            // 同時解決（順序は関係ないように見えるが、死後処理があるので値だけ先に決める）
            d.TakeDamage(finalDamage, DamageType.Normal);

            return new AttackResult(
                finalDamage,
                0,              // 攻撃者はダメージを受けない
                d.IsDead,
                a.IsDead
            );
        }
    }
}
