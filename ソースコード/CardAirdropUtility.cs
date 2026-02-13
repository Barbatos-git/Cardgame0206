using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace CardGame.Gameplay.Cards
{
    /// <summary>
    /// 通用空投工具（探索掉落 / 合成产物掉落 / 合成区撤离）
    /// - 空投期间不被截停：禁用 Collider2D / 暂停 Rigidbody2D / 禁用关键逻辑脚本（按类型名匹配，避免 asmdef/namespace 问题）
    /// - 可重入：Setup 后再次进入空投时，只“追加记录新组件”，不会覆盖旧记录（修复：探索掉落后组件不生效）
    /// - foreach 版本：不依赖 IEnumerable 的 Count/索引器
    /// </summary>
    public static class CardAirdropUtility
    {
        // 公开的“空投中”标记（给 AI/战斗系统过滤用）
        public sealed class AirDropMarker : MonoBehaviour { }

        private static readonly string[] DisableBehaviourTypeNames =
        {
            "CharacterCard",
            "EnemyCard",
            "AutoCombatAgent",
        };

        private sealed class AirDropToken : MonoBehaviour
        {
            public readonly List<Collider2D> Cols = new List<Collider2D>();
            public readonly List<bool> ColEnabled = new List<bool>();

            public readonly List<Rigidbody2D> Rbs = new List<Rigidbody2D>();
            public readonly List<bool> RbSimulated = new List<bool>();

            public readonly List<MonoBehaviour> Behaviours = new List<MonoBehaviour>();
            public readonly List<bool> BehaviourEnabled = new List<bool>();
        }

        // 给外部一个统一判断接口（不依赖 token 私有实现）
        public static bool IsAirDropping(CardWorldView view)
        {
            if (view == null) return false;
            return view.GetComponent<AirDropMarker>() != null;
        }

        public static Vector3 GetNormalAreaCenterFallback()
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

        public static Vector3 GetRandomDropPos(Vector3 center, float radius, float z)
        {
            Vector2 rnd = Random.insideUnitCircle * radius;
            return new Vector3(center.x + rnd.x, center.y + rnd.y, z);
        }

        public static void DropSingle(CardWorldView view, Vector3 dropPos,
            float liftY = 0.6f, float liftDur = 0.12f, float dropDur = 0.28f)
        {
            if (view == null) return;

            var t = view.transform;
            t.DOKill(false);

            SetAirDropMode(view, true);

            Sequence seq = DOTween.Sequence();
            seq.Append(t.DOMoveY(t.position.y + liftY, liftDur).SetEase(Ease.OutQuad));
            seq.Append(t.DOMove(dropPos, dropDur).SetEase(Ease.OutQuad));
            seq.OnComplete(() =>
            {
                if (view == null) return;
                SetAirDropMode(view, false);
                view.ClampStackToNormalArea();
                view.RaiseWorldPositionChanged();
            });
        }

        public static void DropStack(CardWorldView root, Vector3 targetRootPos,
            float liftY = 0.6f, float liftDur = 0.12f, float dropDur = 0.28f)
        {
            if (root == null) return;

            Vector3 delta = targetRootPos - root.transform.position;

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;
                c.transform.DOKill(false);
                SetAirDropMode(c, true);
            }

            Sequence seq = DOTween.Sequence();

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;
                var t = c.transform;
                seq.Join(t.DOMoveY(t.position.y + liftY, liftDur).SetEase(Ease.OutQuad));
            }

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;
                var t = c.transform;
                Vector3 to = t.position + delta;
                seq.Join(t.DOMove(to, dropDur).SetEase(Ease.OutQuad));
            }

            seq.OnComplete(() =>
            {
                if (root == null) return;

                foreach (var c in root.GetStackMembersIncludingSelf())
                {
                    if (c == null) continue;
                    SetAirDropMode(c, false);
                }

                root.ClampStackToNormalArea();
                root.RaiseWorldPositionChanged();
            });
        }

        public static void FlipSetupAndDrop(CardWorldView back, CardDefinition dropDef,
            Vector3 flipPos, Vector3 dropCenter, float radius,
            float flipDuration,
            float liftY = 0.6f, float liftDur = 0.12f, float dropDur = 0.28f)
        {
            if (back == null) return;

            if (dropDef == null)
            {
                Object.Destroy(back.gameObject);
                return;
            }

            var t = back.transform;
            t.DOKill(false);

            // 先进入空投模式（记录当下状态并禁用）
            SetAirDropMode(back, true);

            // 固定到碰撞点
            t.position = new Vector3(flipPos.x, flipPos.y, t.position.z);

            // 先算好落点
            var dropPos = GetRandomDropPos(dropCenter, radius, t.position.z);

            // 翻面：scaleX -> 0 -> Setup -> scaleX -> 1
            t.DOScaleX(0f, flipDuration * 0.5f)
             .SetEase(Ease.InQuad)
             .OnComplete(() =>
             {
                 if (back == null) return;

                 // Setup 会安装/启用人物敌人耐久等组件
                 back.Setup(dropDef);

                 // 关键：Setup 后再次进入空投模式，但只“追加记录新组件”，不会覆盖旧记录
                 SetAirDropMode(back, true);

                 t.DOScaleX(1f, flipDuration * 0.5f).SetEase(Ease.OutQuad);
             });

            // 抬升 + 落点
            Sequence seq = DOTween.Sequence();
            seq.Append(t.DOMoveY(t.position.y + liftY, liftDur).SetEase(Ease.OutQuad));
            seq.Append(t.DOMove(dropPos, dropDur).SetEase(Ease.OutQuad));
            seq.OnComplete(() =>
            {
                if (back == null) return;

                // 恢复所有记录的组件（含 Setup 新装的）
                SetAirDropMode(back, false);

                back.ClampStackToNormalArea();
                back.RaiseWorldPositionChanged();
            });
        }

        // ===== 可重入 AirDropMode =====
        private static void SetAirDropMode(CardWorldView view, bool isAirDrop)
        {
            if (view == null) return;

            var token = view.GetComponent<AirDropToken>();

            if (isAirDrop)
            {
                if (token == null)
                    token = view.gameObject.AddComponent<AirDropToken>();

                // 挂上标记（如果已有则不重复挂）
                if (view.GetComponent<AirDropMarker>() == null)
                    view.gameObject.AddComponent<AirDropMarker>();

                // 1) Colliders：只追加没记录过的
                var cols = view.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c == null) continue;
                    if (token.Cols.Contains(c)) continue;

                    token.Cols.Add(c);
                    token.ColEnabled.Add(c.enabled);
                    c.enabled = false;
                }

                // 2) Rigidbodies：只追加没记录过的
                var rbs = view.GetComponentsInChildren<Rigidbody2D>(true);
                for (int i = 0; i < rbs.Length; i++)
                {
                    var rb = rbs[i];
                    if (rb == null) continue;
                    if (token.Rbs.Contains(rb)) continue;

                    token.Rbs.Add(rb);
                    token.RbSimulated.Add(rb.simulated);
                    rb.simulated = false;
                }

                // 3) Behaviours：按类型名匹配，只追加没记录过的
                var mbs = view.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < mbs.Length; i++)
                {
                    var mb = mbs[i];
                    if (mb == null) continue;

                    string typeName = mb.GetType().Name;
                    bool match = false;
                    for (int j = 0; j < DisableBehaviourTypeNames.Length; j++)
                    {
                        if (typeName == DisableBehaviourTypeNames[j]) { match = true; break; }
                    }
                    if (!match) continue;

                    if (token.Behaviours.Contains(mb)) continue;

                    token.Behaviours.Add(mb);
                    token.BehaviourEnabled.Add(mb.enabled);
                    mb.enabled = false;
                }

                return;
            }

            // restore
            if (token == null) return;

            for (int i = 0; i < token.Cols.Count && i < token.ColEnabled.Count; i++)
            {
                var c = token.Cols[i];
                if (c != null) c.enabled = token.ColEnabled[i];
            }

            for (int i = 0; i < token.Rbs.Count && i < token.RbSimulated.Count; i++)
            {
                var rb = token.Rbs[i];
                if (rb != null) rb.simulated = token.RbSimulated[i];
            }

            for (int i = 0; i < token.Behaviours.Count && i < token.BehaviourEnabled.Count; i++)
            {
                var mb = token.Behaviours[i];
                if (mb != null) mb.enabled = token.BehaviourEnabled[i];
            }

            // 移除标记（空投结束后可被锁定）
            var marker = view.GetComponent<AirDropMarker>();
            if (marker != null) Object.Destroy(marker);

            Object.Destroy(token);
        }
    }
}
