using System;
using UnityEngine;
using CardGame.Core.Combat;
using CardGame.Core.Cards;

namespace CardGame.Gameplay.Combat
{
    /// <summary>
    /// UI/入力から「攻撃したい」を受け、演出→解算を実行
    /// </summary>
    public sealed class CombatController : MonoBehaviour, ICombatService
    {
        [SerializeField] private AttackAnimationDriver animationDriver;

        private readonly CombatResolver _resolver = new CombatResolver();

        public event Action<AttackResult> OnAttackResolved;

        [SerializeField] private float extraAttackLock = 0.05f; // 防抖

        public void RequestAttack(ICombatant attacker, ICombatant defender)
        {
            // 攻击锁：动画期间禁止同一攻击者再次发起攻击
            if (attacker is Component atkComp)
            {
                var lockComp = atkComp.GetComponent<AttackLock>();
                if (lockComp == null) lockComp = atkComp.gameObject.AddComponent<AttackLock>();

                if (lockComp.IsLocked) return;

                float lockTime = (animationDriver != null) ? (animationDriver.TotalDuration + extraAttackLock) : 0.2f;
                lockComp.LockFor(lockTime);
            }

            if (attacker == null || defender == null) return;

            var cmd = new AttackCommand(attacker, defender);

            // 演出が必要なら：ICardEntityを持ってる実体を渡す
            var atkEntity = ResolveEntity(attacker);
            var defEntity = ResolveEntity(defender);

            if (animationDriver != null && atkEntity != null && defEntity != null)
            {
                StartCoroutine(CoResolveAfterAnim(cmd, atkEntity, defEntity));
            }
            else
            {
                var result = _resolver.Resolve(cmd);
                OnAttackResolved?.Invoke(result);
            }
        }

        private System.Collections.IEnumerator CoResolveAfterAnim(AttackCommand cmd, ICardEntity atk, ICardEntity def)
        {
            yield return animationDriver.PlayAttack(atk, def);
            var result = _resolver.Resolve(cmd);
            OnAttackResolved?.Invoke(result);
        }

        private static ICardEntity ResolveEntity(ICombatant combatant)
        {
            var c = combatant as Component;
            if (c != null)
            {
                var view = c.GetComponent<CardWorldView>();
                if (view != null)
                {
                    var entity = view as ICardEntity;
                    if (entity != null) return entity;
                }
            }

            return combatant as ICardEntity;
        }
    }
}
