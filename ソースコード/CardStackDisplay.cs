using UnityEngine;
using TMPro;
using DG.Tweening;

/// <summary>
/// 堆叠的视觉组件：
/// - 控制右下角数量文本
/// - 控制作为底牌/顶牌时的位置偏移
/// - （具体什么时候当底/顶，由 GridManager 决定）
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(CardVisualController))]
public class CardStackDisplay : MonoBehaviour
{
    [Header("位置偏移")]
    public Vector2 topOffset = new Vector2(0f, -0.5f); // 顶牌偏移
    public Vector2 bottomOffset = Vector2.zero; // 底牌偏移（一般 0）             

    private SpriteRenderer sr;
    private Collider2D col;
    private CardVisualController visual;

    private void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
        col = GetComponent<Collider2D>();
        visual = GetComponent<CardVisualController>();
    }

    /// <summary>
    /// 设置显示数量（仅在堆的“代表牌”上显示）
    /// </summary>
    public void SetCount(int count)
    {
        // 统一交给 CardVisualController（它会处理显隐+文字+组内排序）
        if (visual != null)
        {
            visual.SetCount(count);
            return;
        }
    }

    /// <summary>
    /// 将这张牌当作“底牌”显示
    /// </summary>
    public void ShowAsBottom(Vector3 cellCenter)
    {
        if (sr != null) sr.enabled = true;
        if (col != null) col.enabled = true;
        transform.position = cellCenter + (Vector3)bottomOffset;
        visual?.SetNameVisible(true);
    }

    /// <summary>
    /// 将这张牌当作“顶牌”显示
    /// </summary>
    public void ShowAsTop(Vector3 cellCenter)
    {
        if (sr != null) sr.enabled = true;
        if (col != null) col.enabled = true;
        transform.position = cellCenter + (Vector3)topOffset;
        visual?.SetNameVisible(true);
    }

    /// <summary>
    /// 隐藏这张牌（堆中间层，只计数不显示）
    /// </summary>
    public void HideInStack(Vector3 cellCenter)
    {
        if (sr != null) sr.enabled = false;
        // 中间牌不需要再参与点击 / 碰撞
        if (col != null) col.enabled = false;
        // 逻辑上仍在堆的位置
        transform.position = cellCenter;
        // 中间牌不显示数量
        SetCount(0);
        visual?.SetNameVisible(false);
    }

    //--------------------------动画接口-------------------------------
    public void AnimateToBottom(Vector3 center, float duration = 0.15f)
    {
        transform.DOMove(center, duration).SetEase(Ease.OutCubic);
    }

    public void AnimateToTop(Vector3 center, float duration = 0.15f)
    {
        Vector3 target = center + (Vector3)topOffset;
        transform.DOMove(target, duration).SetEase(Ease.OutBack);
    }

    public void AnimateToMiddle(Vector3 center, float duration = 0.15f)
    {
        // 中间牌移到底牌位置但不显示
        transform.DOMove(center, duration).SetEase(Ease.OutCubic);
    }

}
