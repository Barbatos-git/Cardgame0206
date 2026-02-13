using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

/// <summary>
/// 合成黏稠融合演出（计时期间播放）
/// - 起手：素材卡聚到中心周围环形槽位
/// - 中段：绕中心小半径“搅动旋转 + 摩擦抖动”
/// - 末段(remaining <= finalSmashTime)：快速压到中心 + 闪白 + 爆一下
/// - 合成期间：自动置顶 Sorting（用 CardVisualController overlay）
/// - 支持 Pause/Resume/Stop
/// </summary>
public sealed class FusionFuseVfxPro : MonoBehaviour
{
    [Header("中心帧动画")]
    [SerializeField] private SpriteRenderer fxRenderer;
    [SerializeField] private List<Sprite> frames = new List<Sprite>();
    [SerializeField] private float fps = 18f;
    [SerializeField] private bool loopFrames = true;

    [Header("时间分段")]
    [Tooltip("末段压缩+闪白+爆一下的持续时间（秒）")]
    [SerializeField] private float finalSmashTime = 0.20f;

    [Header("聚拢参数")]
    [SerializeField] private float gatherDuration = 0.30f;
    [SerializeField] private float ringRadius = 0.55f;
    [SerializeField] private Ease gatherEase = Ease.OutQuad;

    [Header("搅动/黏稠参数")]
    [SerializeField] private float swirlRadius = 0.18f;     // 搅动半径
    [SerializeField] private float swirlSpeed = 5.5f;       // 搅动速度（越大越快）
    [SerializeField] private float rubRotate = 8f;          // 旋转抖动幅度
    [SerializeField] private float pulseScale = 0.06f;      // 缩放呼吸幅度

    [Header("末段爆一下")]
    [SerializeField] private float smashPunchScale = 0.18f; // 爆一下缩放
    [SerializeField] private float smashPunchTime = 0.18f;

    [Header("闪白")]
    [SerializeField] private float flashToWhiteTime = 0.05f;
    [SerializeField] private float flashBackTime = 0.12f;

    [Header("置顶")]
    [SerializeField] private string overlayKey = "fusion";
    private int _overlayBaseOrder;

    private readonly List<CardWorldView> _views = new();
    private readonly Dictionary<CardWorldView, Vector3> _basePos = new();
    private readonly Dictionary<CardWorldView, Vector3> _baseScale = new();
    private readonly Dictionary<CardWorldView, Quaternion> _baseRot = new();
    private readonly Dictionary<CardWorldView, Color> _baseColor = new();

    private readonly List<CardVisualController> _visuals = new();

    private Coroutine _frameCo;
    private bool _playing;
    private bool _paused;
    private bool _finalSmashed;

    // 用同一个 Target 统一 Pause/Kill
    private object _tweenTarget;

    // 搅动相位
    private float _phase;

    // 用来避免“聚拢 tween 未结束就被 Tick 抢写 position”
    private float _elapsed;

    private readonly Dictionary<CardWorldView, Vector3> _slotCenter = new();
    private float _totalDuration;

    private void Awake()
    {
        _tweenTarget = this;
        if (fxRenderer == null) fxRenderer = GetComponentInChildren<SpriteRenderer>(true);
    }

    /// <summary>
    /// 播放
    /// totalFuseDuration：合成总计时（你 CoFuse 的 fuseDuration）
    /// </summary>
    public void Play(Vector3 centerWorld, List<CardWorldView> materials, float totalFuseDuration)
    {
        Stop(restore: true);

        if (materials == null || materials.Count == 0) return;

        _playing = true;
        _paused = false;
        _finalSmashed = false;
        _phase = Random.value * 100f;
        _elapsed = 0f;
        _totalDuration = Mathf.Max(0.01f, totalFuseDuration);

        transform.position = centerWorld;
        gameObject.SetActive(true);

        _views.Clear();
        _basePos.Clear();
        _baseScale.Clear();
        _baseRot.Clear();
        _baseColor.Clear();
        _visuals.Clear();
        _slotCenter.Clear();

        // 记录基态 + 置顶 overlay（整堆）
        for (int i = 0; i < materials.Count; i++)
        {
            var v = materials[i];
            if (v == null) continue;

            // 用 root 堆来避免堆成员重复加入
            var root = v.GetStackRoot();
            if (root == null) continue;
            if (_views.Contains(root)) continue;

            _views.Add(root);

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;

                _basePos[c] = c.transform.position;
                _baseScale[c] = c.transform.localScale;
                _baseRot[c] = c.transform.rotation;

                if (c.artworkRenderer != null)
                    _baseColor[c] = c.artworkRenderer.color;

                // 置顶：用你的 CardVisualController overlay 系统
                var vis = c.GetComponent<CardVisualController>();
                if (vis != null) _visuals.Add(vis);
            }
        }

        ApplyTopOverlay();

        // 1) 聚拢：每个 root 移到环形槽位
        for (int i = 0; i < _views.Count; i++)
        {
            var root = _views[i];
            if (root == null) continue;

            // 以“当前卡相对中心的方向”决定聚拢槽位，避免跨半个圆跳位
            Vector3 dir = (root.transform.position - centerWorld);
            if (dir.sqrMagnitude < 0.0001f)
            {
                // 万一刚好在中心，用 index 做个兜底方向
                float angle = (Mathf.PI * 2f) * (i / Mathf.Max(1f, _views.Count));
                dir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            }
            dir.Normalize();

            // slot：从当前位置方向往中心收缩到 ringRadius 处
            Vector3 slot = centerWorld + dir * ringRadius;
            _slotCenter[root] = slot;   // 保存稳定槽位

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;
                c.transform.DOKill(false);

                Vector3 target = slot + c.offsetInStack;
                c.transform.DOMove(target, gatherDuration)
                    .SetEase(gatherEase)
                    .SetTarget(_tweenTarget);
            }
        }

        // 2) 中心帧动画开始
        if (frames != null && frames.Count > 0 && fxRenderer != null)
            _frameCo = StartCoroutine(CoFrames());

        // 3) 摩擦抖动（Tween）
        StartRubTweens();
    }

    /// <summary>
    /// 在合成计时协程里每帧调用：传 remainingTime（剩余秒）
    /// 末段 remaining <= finalSmashTime 时触发压缩闪白爆一下
    /// </summary>
    public void Tick(float remainingTime, Vector3 centerWorld)
    {
        if (!_playing || _paused) return;

        _elapsed += Time.deltaTime;

        // 聚拢阶段：不要搅动/不要抢写 position，避免和 DOMove 打架造成“抖”
        if (_elapsed < gatherDuration)
            return;

        // 搅动：root 围绕中心做小半径搅动（手写更新，比 tween 更“黏”）
        _phase += Time.deltaTime * swirlSpeed;

        for (int i = 0; i < _views.Count; i++)
        {
            var root = _views[i];
            if (root == null) continue;

            // 每个 root 不同相位，避免整齐
            float p = _phase + i * 1.7f;

            Vector3 swirl = new Vector3(
                Mathf.Cos(p) * swirlRadius,
                Mathf.Sin(p * 1.13f) * swirlRadius,
                0f
            );

            // 计算“中段吸入进度”：聚拢结束后开始从 slot 吸向 center
            float middleStart = gatherDuration;
            float middleEnd = Mathf.Max(middleStart + 0.01f, _totalDuration - finalSmashTime);
            float t = Mathf.InverseLerp(middleStart, middleEnd, _elapsed);
            // 让吸入更顺滑（类似 SmoothStep）
            t = t * t * (3f - 2f * t);

            if (!_slotCenter.TryGetValue(root, out var slot))
                slot = root.transform.position;

            // 稳定基准：从 slot 慢慢吸到中心，再叠加搅拌偏移
            Vector3 baseCenter = Vector3.Lerp(slot, centerWorld, t);
            Vector3 rootCenter = baseCenter + swirl;

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;
                c.transform.position = rootCenter + c.offsetInStack;
            }
        }

        if (!_finalSmashed && remainingTime <= finalSmashTime)
        {
            _finalSmashed = true;
            DoFinalSmash(centerWorld);
        }
    }

    public void Pause()
    {
        if (!_playing) return;
        _paused = true;
        DOTween.Pause(_tweenTarget);
    }

    public void Resume()
    {
        if (!_playing) return;
        _paused = false;
        DOTween.Play(_tweenTarget);
    }

    /// <summary>
    /// 停止。restore=true 会把卡牌还原到开始前的位置/旋转/缩放/颜色，并撤销 overlay。
    /// 合成成功时建议 restore=false（因为马上 Destroy 材料卡）。
    /// </summary>
    public void Stop(bool restore)
    {
        if (_frameCo != null)
        {
            StopCoroutine(_frameCo);
            _frameCo = null;
        }

        DOTween.Kill(_tweenTarget);

        RemoveTopOverlay();

        if (restore)
        {
            var keys = new List<CardWorldView>(_basePos.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = keys[i];
                if (c == null) continue;

                if (_basePos.TryGetValue(c, out var p)) c.transform.position = p;
                if (_baseScale.TryGetValue(c, out var s)) c.transform.localScale = s;
                if (_baseRot.TryGetValue(c, out var r)) c.transform.rotation = r;

                if (c.artworkRenderer != null && _baseColor.TryGetValue(c, out var col))
                    c.artworkRenderer.color = col;
            }
        }

        _views.Clear();
        _basePos.Clear();
        _baseScale.Clear();
        _baseRot.Clear();
        _baseColor.Clear();
        _visuals.Clear();

        _playing = false;
        _paused = false;
        _finalSmashed = false;

        if (fxRenderer != null) fxRenderer.sprite = null;
        gameObject.SetActive(false);
    }

    private void ApplyTopOverlay()
    {
        _overlayBaseOrder = CardVisualController.NextFrontBase();

        var set = new HashSet<CardVisualController>(_visuals);
        int i = 0;
        foreach (var vis in set)
        {
            if (vis == null) continue;
            vis.PushOverlay(overlayKey, _overlayBaseOrder + i);
            i++;
        }

        // 关键：中心特效永远更高
        if (fxRenderer != null)
        {
            fxRenderer.sortingOrder = _overlayBaseOrder + 9999;
            fxRenderer.enabled = true;
            var c = fxRenderer.color; c.a = 1f; fxRenderer.color = c;
        }
    }

    private void RemoveTopOverlay()
    {
        var set = new HashSet<CardVisualController>(_visuals);
        foreach (var vis in set)
        {
            if (vis == null) continue;
            vis.PopOverlay(overlayKey);
        }
    }

    private void StartRubTweens()
    {
        // 旋转抖动 + 缩放呼吸（位置搅动由 Tick 控制）
        for (int i = 0; i < _views.Count; i++)
        {
            var root = _views[i];
            if (root == null) continue;

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;

                c.transform.DORotate(new Vector3(0, 0, rubRotate), 0.22f)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo)
                    .SetTarget(_tweenTarget);

                var baseScale = c.transform.localScale;
                c.transform.DOScale(baseScale * (1f + pulseScale), 0.28f)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo)
                    .SetTarget(_tweenTarget);
            }
        }

        // 中心特效呼吸
        transform.localScale = Vector3.one;
        transform.DOScale(1.08f, 0.35f)
            .SetEase(Ease.InOutSine)
            .SetLoops(-1, LoopType.Yoyo)
            .SetTarget(_tweenTarget);
    }

    private void DoFinalSmash(Vector3 centerWorld)
    {
        // 末段：快速压到中心（整堆） + 闪白 + 爆一下
        for (int i = 0; i < _views.Count; i++)
        {
            var root = _views[i];
            if (root == null) continue;

            foreach (var c in root.GetStackMembersIncludingSelf())
            {
                if (c == null) continue;

                Vector3 target = centerWorld + c.offsetInStack * 0.15f;

                c.transform.DOMove(target, finalSmashTime)
                    .SetEase(Ease.InQuad)
                    .SetTarget(_tweenTarget);

                if (c.artworkRenderer != null)
                {
                    var sr = c.artworkRenderer;
                    sr.DOKill(false);

                    // 记录原色（你本来就存了 _baseColor）
                    Color baseCol = _baseColor.TryGetValue(c, out var bc) ? bc : sr.color;

                    // 变白
                    DOTween.To(() => sr.color, x => sr.color = x, Color.white, flashToWhiteTime)
                        .SetEase(Ease.OutQuad)
                        .SetTarget(_tweenTarget)
                        .OnComplete(() =>
                        {
                            if (c != null && c.artworkRenderer != null)
                            {
                                var sr2 = c.artworkRenderer;
                                // 还原
                                DOTween.To(() => sr2.color, x => sr2.color = x, baseCol, flashBackTime)
                                    .SetEase(Ease.OutQuad)
                                    .SetTarget(_tweenTarget);
                            }
                        });
                }

                c.transform.DOPunchScale(Vector3.one * smashPunchScale, smashPunchTime, 10, 1f)
                    .SetTarget(_tweenTarget);
            }
        }

        transform.DOPunchScale(Vector3.one * 0.25f, 0.20f, 10, 1f)
            .SetTarget(_tweenTarget);
    }

    private Vector3 ComputeRingSlot(Vector3 center, int index, int count, float radius)
    {
        float angle = (Mathf.PI * 2f) * (index / Mathf.Max(1f, count)) + Mathf.PI * 0.75f;
        Vector3 offset = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
        return center + offset;
    }

    private IEnumerator CoFrames()
    {
        int idx = 0;
        float dt = 1f / Mathf.Max(1f, fps);

        while (_playing)
        {
            if (_paused)
            {
                yield return null;
                continue;
            }

            if (fxRenderer != null && frames != null && frames.Count > 0)
            {
                fxRenderer.sprite = frames[idx];
                idx++;
                if (idx >= frames.Count)
                {
                    if (loopFrames) idx = 0;
                    else break;
                }
            }

            yield return new WaitForSeconds(dt);
        }
    }
}
