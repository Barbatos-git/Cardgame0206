using System.Collections;
using UnityEngine;
using CardGame.Core.Cards;

#if DOTWEEN
using DG.Tweening;
#endif

namespace CardGame.Gameplay.Combat
{
    /// <summary>
    /// 炉石風：攻撃者が前進→ヒット感→戻る（DOTween版）
    /// DOTweenが無い場合は簡易コルーチンで動作
    /// </summary>
    public sealed class AttackAnimationDriver : MonoBehaviour
    {
        [SerializeField] private float forwardDistance = 0.6f;
        [SerializeField] private float forwardTime = 0.12f;
        [SerializeField] private float hitShakeDuration = 0.06f;
        [SerializeField] private float hitShakeStrength = 0.08f;
        [SerializeField] private float returnTime = 0.10f;

        public Coroutine PlayAttack(ICardEntity attacker, ICardEntity defender)
        {
            if (attacker == null || defender == null) return null;

#if DOTWEEN
            return StartCoroutine(CoAttackTween(attacker.Transform, defender.Transform));
#else
            return StartCoroutine(CoAttackFallback(attacker.Transform, defender.Transform));
#endif
        }

#if DOTWEEN
        private IEnumerator CoAttackTween(Transform atk, Transform def)
        {
            var start = atk.position;

            // =========================================================
            // 攻击时临时抬高攻击方堆的 sorting（显示更好）
            // =========================================================
            var atkView = atk ? atk.GetComponent<CardWorldView>() : null;
            if (atkView != null) atkView.BeginTempFront();

            var dir = (def.position - start);
            dir.z = 0f;
            dir = dir.sqrMagnitude < 0.0001f ? Vector3.right : dir.normalized;

            var mid = start + dir * forwardDistance;

            // 既存Tweenを殺してから（多重クリックの事故防止）
            atk.DOKill();

            // Sequence：前進→シェイク→戻る
            Sequence seq = DOTween.Sequence();
            seq.Append(atk.DOMove(mid, forwardTime).SetEase(Ease.OutQuad));
            seq.Append(atk.DOShakePosition(hitShakeDuration, hitShakeStrength, vibrato: 12, randomness: 90, fadeOut: true));
            seq.Append(atk.DOMove(start, returnTime).SetEase(Ease.InQuad));

            // 完了待ち
            yield return seq.WaitForCompletion();

            // 攻击过程中目标可能已经死亡
            if (!atk || !def)
                yield break;

            // 恢复 sorting
            if (atkView != null) atkView.EndTempFront();

            atk.position = start;

            // 兜底：攻击结束后把双方都夹回普通区（不会影响“攻击时可临时越界”）
            var attackerView = atk ? atk.GetComponent<CardWorldView>() : null;
            if (attackerView != null)
                attackerView.ClampStackToNormalArea();

            var defenderView = def ? def.GetComponent<CardWorldView>() : null;
            if (defenderView != null)
                defenderView.ClampStackToNormalArea();
        }
#endif

        // DOTweenが無い場合の簡易版
        private IEnumerator CoAttackFallback(Transform atk, Transform def)
        {
            var start = atk.position;

            // =========================================================
            // 攻击时临时抬高攻击方堆的 sorting（显示更好）
            // =========================================================
            var atkView = atk ? atk.GetComponent<CardWorldView>() : null;
            if (atkView != null) atkView.BeginTempFront();


            var dir = (def.position - start);
            dir.z = 0f;
            dir = dir.sqrMagnitude < 0.0001f ? Vector3.right : dir.normalized;

            var mid = start + dir * forwardDistance;

            yield return Move(atk, start, mid, forwardTime);

            // ヒット感：ちょい揺れ
            float t = 0f;
            while (t < hitShakeDuration)
            {
                t += UnityEngine.Time.deltaTime;
                atk.position = mid + (Vector3)(Random.insideUnitCircle * hitShakeStrength);
                yield return null;
            }

            yield return Move(atk, atk.position, start, returnTime);

            // 恢复 sorting
            if (atkView != null) atkView.EndTempFront();

            atk.position = start;

            // 兜底：攻击结束后夹回普通区
            var attackerView = atk ? atk.GetComponent<CardWorldView>() : null;
            if (attackerView != null)
                attackerView.ClampStackToNormalArea();

            var defenderView = def ? def.GetComponent<CardWorldView>() : null;
            if (defenderView != null)
                defenderView.ClampStackToNormalArea();
        }

        private IEnumerator Move(Transform t, Vector3 from, Vector3 to, float time)
        {
            if (time <= 0f) { t.position = to; yield break; }

            float e = 0f;
            while (e < time)
            {
                e += UnityEngine.Time.deltaTime;
                float a = Mathf.Clamp01(e / time);
                a = 1f - Mathf.Pow(1f - a, 2f);
                t.position = Vector3.LerpUnclamped(from, to, a);
                yield return null;
            }
            t.position = to;
        }

        public float TotalDuration =>forwardTime + hitShakeDuration + returnTime;
    }
}
