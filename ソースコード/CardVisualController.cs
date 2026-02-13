using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 统一的卡牌视觉控制器：
/// - 以 SortingGroup 为“整张卡”的排序入口
/// - 负责名字/数量文字永远在卡面上方（组内相对顺序）
/// - 负责“堆叠排序”“拖拽置顶”“攻击临时置顶”的抬高/恢复
///
/// 设计原则：
/// - 外部系统永远不要再直接改 SpriteRenderer.sortingOrder
/// - 外部系统只调用：SetStackOrder / PushOverlay / PopOverlay / CaptureBaseIfNeeded
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SortingGroup))]
public sealed class CardVisualController : MonoBehaviour
{
    [Header("必需组件")]
    [SerializeField] private SortingGroup sortingGroup;
    [SerializeField] private SpriteRenderer artworkRenderer;

    [Header("文字（世界空间 TMP）")]
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private TextMeshPro countLabel;

    [Header("组内相对排序（越大越在上）")]
    [SerializeField] private int artworkLocalOrder = 0;
    [SerializeField] private int weaponIconLocalOrder = 5;
    [SerializeField] private int nameLocalOrder = 10;
    [SerializeField] private int countLocalOrder = 20;

    [Header("Weapon Icon (optional)")]
    [SerializeField] private Vector3 weaponIconLocalPos = new Vector3(-0.36f, -0.46f, 0f);
    [SerializeField, Range(0.05f, 1f)] private float weaponIconScale = 0.3f;

    // “堆叠系统”给的基础顺序（每张卡自己的 SortingGroup.sortingOrder）
    private int _stackOrder;

    // 多来源抬高：拖拽、攻击等（取最大值）
    private readonly Dictionary<string, int> _overlays = new Dictionary<string, int>();

    private Renderer _nameRenderer;
    private Renderer _countRenderer;
    private SpriteRenderer _weaponIconRenderer;

    // 全局置顶序号：保证不同“置顶事件”不会打平
    private static int s_FrontTicket = 0;

    /// <summary>
    /// 获取一段不会冲突的置顶 baseOrder（通常给整堆使用：base+i）
    /// </summary>
    public static int NextFrontBase(int step = 10, int baseUnit = 1000)
    {
        // baseUnit 控制置顶的“绝对高度”，避免被普通 stackOrder 超过
        int t = ++s_FrontTicket;
        return baseUnit + t * step * 10; // 例如 1000 + 1*100, 1000 + 2*100...
    }

    public int CurrentOrder => sortingGroup != null ? sortingGroup.sortingOrder : 0;

    private void Awake()
    {
        if (sortingGroup == null) sortingGroup = GetComponent<SortingGroup>();
        if (artworkRenderer == null) artworkRenderer = GetComponent<SpriteRenderer>();

        // 自动绑定文字（已经做了 Marker 就会非常稳）
        if (nameText == null)
        {
            var marker = GetComponentInChildren<CardNameTextMarker>(true);
            if (marker != null) nameText = marker.GetComponent<TMP_Text>();
        }

        if (countLabel == null)
        {
            var marker = GetComponentInChildren<CardCountTextMarker>(true);
            if (marker != null) countLabel = marker.GetComponent<TextMeshPro>();
        }

        _nameRenderer = nameText != null ? nameText.GetComponent<Renderer>() : null;
        _countRenderer = countLabel != null ? countLabel.GetComponent<Renderer>() : null;

        EnsureWeaponIconRenderer();

        // 初始堆叠顺序
        _stackOrder = sortingGroup.sortingOrder;

        // 初次同步“组内相对排序”
        SyncLocalSorting();
        ApplyFinalOrder();

        SetNameVisible(true);
    }

    /// <summary>仅同步“组内相对顺序”：卡面=0，名字=10，数量=20（不改变 SortingGroup.sortingOrder）</summary>
    public void SyncLocalSorting()
    {
        if (sortingGroup == null) return;

        int layerId = sortingGroup.sortingLayerID;

        if (artworkRenderer != null)
        {
            artworkRenderer.sortingLayerID = layerId;
            artworkRenderer.sortingOrder = artworkLocalOrder;
        }

        if (_weaponIconRenderer != null)
        {
            _weaponIconRenderer.sortingLayerID = layerId;
            _weaponIconRenderer.sortingOrder = weaponIconLocalOrder;
        }

        if (_nameRenderer != null)
        {
            _nameRenderer.sortingLayerID = layerId;
            _nameRenderer.sortingOrder = nameLocalOrder;
        }

        if (_countRenderer != null)
        {
            _countRenderer.sortingLayerID = layerId;
            _countRenderer.sortingOrder = countLocalOrder;
        }
    }

    /// <summary>
    /// 装备武器的小图标（世界空间 SpriteRenderer）
    /// - 默认挂在本对象下（不参与碰撞）
    /// - 默认位置：左下角
    /// - 默认缩放：0.3
    /// </summary>
    public void SetWeaponIcon(Sprite sprite)
    {
        EnsureWeaponIconRenderer();
        if (_weaponIconRenderer == null) return;

        if (sprite == null)
        {
            _weaponIconRenderer.sprite = null;
            _weaponIconRenderer.enabled = false;
        }
        else
        {
            _weaponIconRenderer.sprite = sprite;
            _weaponIconRenderer.enabled = true;
        }

        SyncLocalSorting();
    }

    private void EnsureWeaponIconRenderer()
    {
        if (_weaponIconRenderer != null) return;

        var t = transform.Find("WeaponIcon");
        GameObject go;
        if (t != null) go = t.gameObject;
        else
        {
            go = new GameObject("WeaponIcon");
            go.transform.SetParent(transform, false);
        }

        go.transform.localPosition = weaponIconLocalPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * weaponIconScale;

        _weaponIconRenderer = go.GetComponent<SpriteRenderer>();
        if (_weaponIconRenderer == null)
            _weaponIconRenderer = go.AddComponent<SpriteRenderer>();

        _weaponIconRenderer.enabled = false;

        // 不需要 Collider
        var col = go.GetComponent<Collider2D>();
        if (col != null) Destroy(col);
    }

    public void SetStackOrder(int order)
    {
        _stackOrder = order;
        ApplyFinalOrder();
    }

    public void PushOverlay(string key, int absoluteOrder)
    {
        if (string.IsNullOrEmpty(key)) return;
        _overlays[key] = absoluteOrder;
        ApplyFinalOrder();
    }

    public void PopOverlay(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (_overlays.Remove(key))
            ApplyFinalOrder();
    }

    public void ClearAllOverlays()
    {
        _overlays.Clear();
        ApplyFinalOrder();
    }

    private void ApplyFinalOrder()
    {
        if (sortingGroup == null) return;

        int final = _stackOrder;
        foreach (var kv in _overlays)
            final = Mathf.Max(final, kv.Value);

        sortingGroup.sortingOrder = final;

        // 组内相对层级必须稳定
        SyncLocalSorting();
    }

    public void SetNameVisible(bool visible)
    {
        if (nameText == null) return;
        if (!nameText.gameObject.activeSelf) nameText.gameObject.SetActive(true);
        nameText.enabled = visible;
    }

    public void SetNameText(string text)
    {
        if (nameText == null) return;

        if (!nameText.gameObject.activeSelf)
            nameText.gameObject.SetActive(true);

        nameText.text = text;
    }

    public void SetCount(int count)
    {
        if (countLabel == null) return;

        if (count <= 1)
        {
            countLabel.gameObject.SetActive(false);
        }
        else
        {
            countLabel.gameObject.SetActive(true);
            countLabel.text = "x" + count;
        }

        SyncLocalSorting();
    }
}
