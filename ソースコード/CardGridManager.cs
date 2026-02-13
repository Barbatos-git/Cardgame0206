using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

public class CardGridManager : MonoBehaviour, IResettable
{
    public static CardGridManager Instance { get; private set; }

    [Header("网格设置")]
    // 一格宽高
    public Vector2 cellSize = new Vector2(1.5f, 2.0f);
    // 左下角世界坐标
    public Vector2 origin = new Vector2(-7f, -3f);      
    public int columns = 8;
    public int rows = 4;
    public bool clampToGridArea = true;

    [Header("整理 & 显示")]
    // 按 G 整理
    public KeyCode sortKey = KeyCode.G;
    // 网格显示方块 prefab
    public GameObject cellVisualPrefab;     
    public float cellVisualAlpha = 0.3f;
    // 整理时的动画时间
    public float snapDuration = 0.15f;

    [Header("网格闪显（Stacklands 风格）")]
    public bool flashGridOnSort = true;
    public float gridFlashDuration = 0.6f;

    [Header("整理过滤")]
    [Tooltip("如果卡的 definition.tags 包含 Enemy，则不参与整理")]
    public bool excludeEnemyFromSort = true;

    private bool gridVisible = false;
    private readonly List<GameObject> cellVisuals = new List<GameObject>();

    // 用来取消上一次“延迟隐藏”
    private Tween _hideGridTween;

    /// <summary>
    /// 内部用：记录每个堆根的整理目标
    /// </summary>
    private class RootPlacementInfo
    {
        public CardWorldView root;
        public Vector3 currentPos;
        public Vector2Int preferredIndex;
        public Vector2Int assignedIndex;
        public Vector3 assignedPos;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        CreateGridVisuals();
        SetGridVisible(false);
    }

    private void Update()
    {
        if (Input.GetKeyDown(sortKey))
        {
            // 按键：整理 + 网格闪显
            SortWithOptionalFlash();
        }
    }

    /// <summary>
    /// 按键触发：切换网格显示 + 整理所有卡牌
    /// </summary>
    public void SortWithOptionalFlash()
    {
        if (flashGridOnSort)
            FlashGrid();

        SortAllCards();
    }

    /// <summary>
    /// 让网格显示一会儿，然后自动隐藏
    /// </summary>
    private void FlashGrid()
    {
        // 取消上一次延迟隐藏
        _hideGridTween?.Kill(false);

        // 显示
        gridVisible = true;
        SetGridVisible(true);

        // 延迟隐藏
        _hideGridTween = DOVirtual.DelayedCall(gridFlashDuration, () =>
        {
            gridVisible = false;
            SetGridVisible(false);
        }, ignoreTimeScale: true);
    }

    #region 网格可视化

    private void SetGridVisible(bool visible)
    {
        foreach (var go in cellVisuals)
        {
            if (go != null)
                go.SetActive(visible);
        }
    }

    private void CreateGridVisuals()
    {
        if (cellVisualPrefab == null)
            return;

        foreach (var go in cellVisuals)
        {
            if (go != null) Destroy(go);
        }
        cellVisuals.Clear();

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Vector2 center = new Vector2(
                    origin.x + x * cellSize.x,
                    origin.y + y * cellSize.y
                );

                var cell = Instantiate(cellVisualPrefab, center, Quaternion.identity, transform);
                cellVisuals.Add(cell);

                var sr = cell.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    var c = sr.color;
                    c.a = cellVisualAlpha;
                    sr.color = c;
                }
            }
        }
    }

    #endregion

    #region 坐标 & 格子工具

    /// <summary>
    /// 将世界坐标转换为格子坐标（col,row）
    /// </summary>
    private Vector2Int WorldToGridIndex(Vector3 worldPos)
    {
        Vector2 local = (Vector2)worldPos - origin;

        int col = Mathf.RoundToInt(local.x / cellSize.x);
        int row = Mathf.RoundToInt(local.y / cellSize.y);

        if (clampToGridArea)
        {
            col = Mathf.Clamp(col, 0, columns - 1);
            row = Mathf.Clamp(row, 0, rows - 1);
        }

        return new Vector2Int(col, row);
    }

    /// <summary>
    /// 将格子坐标转换为世界坐标中心
    /// </summary>
    private Vector3 GridIndexToWorldCenter(Vector2Int index, float z)
    {
        return new Vector3(
            origin.x + index.x * cellSize.x,
            origin.y + index.y * cellSize.y,
            z
        );
    }

    /// <summary>
    /// 将一个世界坐标吸附到最近的格子中心
    /// </summary>
    public Vector3 GetSnappedPosition(Vector3 worldPos)
    {
        Vector2Int index = WorldToGridIndex(worldPos);
        return GridIndexToWorldCenter(index, worldPos.z);
    }

    private Vector2Int ClampIndex(Vector2Int index)
    {
        int col = Mathf.Clamp(index.x, 0, columns - 1);
        int row = Mathf.Clamp(index.y, 0, rows - 1);
        return new Vector2Int(col, row);
    }

    #endregion

    #region 整理（保证每格唯一）

    /// <summary>
    /// 整理所有堆：
    /// - 每个堆根先算出“理想格子”
    /// - 如果被占用，则寻找离理想格子最近的空格子
    /// - 再整体移动该堆
    /// </summary>
    public void SortAllCards()
    {
        var cards = FindObjectsOfType<CardWorldView>();

        // 1. 先收集唯一的“堆根”
        List<CardWorldView> roots = new List<CardWorldView>();
        HashSet<CardWorldView> rootSet = new HashSet<CardWorldView>();

        foreach (var card in cards)
        {
            if (card == null) continue;

            CardWorldView root = card.stackRoot == null ? card : card.stackRoot;
            if (root == null) continue;

            // 过滤：敌人不整理
            if (!ShouldSortRoot(root)) continue;

            if (!rootSet.Contains(root))
            {
                rootSet.Add(root);
                roots.Add(root);
            }
        }

        // 2. 为每个堆根准备初始信息
        List<RootPlacementInfo> placements = new List<RootPlacementInfo>();
        foreach (var root in roots)
        {
            var info = new RootPlacementInfo();
            info.root = root;
            info.currentPos = root.transform.position;

            var preferred = WorldToGridIndex(info.currentPos);
            preferred = ClampIndex(preferred);
            info.preferredIndex = preferred;
            // 先填同一个，后面再找空位
            info.assignedIndex = preferred; 
            info.assignedPos = GridIndexToWorldCenter(preferred, info.currentPos.z);

            placements.Add(info);
        }

        // 3. 为了固定顺序，按照行列排序（也可按 currentPos.x/y）
        placements.Sort((a, b) =>
        {
            int r = a.preferredIndex.y.CompareTo(b.preferredIndex.y);
            if (r != 0) return r;
            return a.preferredIndex.x.CompareTo(b.preferredIndex.x);
        });

        // 4. 逐个把堆根分配到“最近的空格子”
        bool[,] occupied = new bool[columns, rows];

        foreach (var info in placements)
        {
            Vector2Int desired = ClampIndex(info.preferredIndex);
            Vector2Int chosen = FindNearestFreeIndex(desired, occupied);

            info.assignedIndex = chosen;
            info.assignedPos = GridIndexToWorldCenter(chosen, info.currentPos.z);

            if (IsInsideGrid(chosen))
            {
                occupied[chosen.x, chosen.y] = true;
            }
        }

        // 5. 真正移动：每个堆根算 delta，全堆一起移动
        foreach (var info in placements)
        {
            Vector3 delta = info.assignedPos - info.currentPos;
            MoveStackWithDelta(info.root, delta);
        }
    }

    /// <summary>
    /// 决定某个堆根是否参与整理
    /// </summary>
    private bool ShouldSortRoot(CardWorldView root)
    {
        if (!excludeEnemyFromSort) return true;

        // CardWorldView 上有 definition（CardDefinition）
        // 如果字段名不同（比如 Definition），把下面这一行改成实际字段即可。
        var def = root.definition;
        if (def == null) return true; // 没定义就先允许整理，避免误伤

        // Enemy 标签存在时排除（如果标签名不是 Enemy，把这里改成对应 tag）
        return (def.tags & CardTag.Enemy) == 0;
    }

    /// <summary>
    /// 对单张卡进行吸附（不做“唯一格子”判定，只是随手吸附）
    /// 如果之后想在拖拽结束时自动吸附，就可以用这个。
    /// </summary>
    public void SnapCard(CardWorldView card)
    {
        if (card == null) return;

        CardWorldView root = card.stackRoot == null ? card : card.stackRoot;
        SnapStackRoot(root);
    }

    /// <summary>
    /// 只根据自己最近格子吸附整堆（不管其它堆占不占用）
    /// 通常用于：拖拽结束时稍微贴一下格子，而不是全局整理。
    /// </summary>
    public void SnapStackRoot(CardWorldView root)
    {
        if (root == null) return;

        Vector3 currentCenter = root.transform.position;
        Vector3 targetCenter = GetSnappedPosition(currentCenter);
        Vector3 delta = targetCenter - currentCenter;

        MoveStackWithDelta(root, delta);
    }

    /// <summary>
    /// 把以 root 为根的整堆，整体平移 delta（带 DOTween 动画）
    /// </summary>
    private void MoveStackWithDelta(CardWorldView root, Vector3 delta)
    {
        if (root == null) return;

        foreach (var member in GetStackMembersIncludingRoot(root))
        {
            if (member == null) continue;

            Vector3 current = member.transform.position;
            Vector3 target = current + delta;

            member.transform.DOComplete();
            member.transform.DOMove(target, snapDuration)
                            .SetEase(Ease.OutQuad);
        }
    }

    /// <summary>
    /// 获取某个堆根下的所有卡（包含根自身）
    /// 如果没有 stackMembers，则视为单卡
    /// </summary>
    private IEnumerable<CardWorldView> GetStackMembersIncludingRoot(CardWorldView root)
    {
        // 没有成员列表，说明这就是单独一张卡
        if (root.stackMembers == null || root.stackMembers.Count == 0)
        {
            yield return root;
            yield break;
        }

        foreach (var c in root.stackMembers)
        {
            if (c != null)
                yield return c;
        }
    }

    /// <summary>
    /// 在 occupied 数组中，找到离 desired 最近的空格子
    /// </summary>
    private Vector2Int FindNearestFreeIndex(Vector2Int desired, bool[,] occupied)
    {
        desired = ClampIndex(desired);

        // 先看看理想格子本身空不空
        if (IsInsideGrid(desired) && !occupied[desired.x, desired.y])
        {
            return desired;
        }

        Vector2Int best = desired;
        float bestDistSq = float.MaxValue;

        Vector3 desiredCenter = GridIndexToWorldCenter(desired, 0f);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (occupied[x, y]) continue;

                Vector2Int idx = new Vector2Int(x, y);
                Vector3 center = GridIndexToWorldCenter(idx, 0f);

                float d = (center - desiredCenter).sqrMagnitude;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = idx;
                }
            }
        }

        // 如果 bestDistSq 还是 MaxValue，说明格子全满，只能退回原来的 desired（会重叠）
        if (bestDistSq == float.MaxValue)
        {
            return desired;
        }

        return best;
    }

    private bool IsInsideGrid(Vector2Int idx)
    {
        return idx.x >= 0 && idx.x < columns && idx.y >= 0 && idx.y < rows;
    }

    #endregion

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Vector2 center = new Vector2(
                    origin.x + x * cellSize.x,
                    origin.y + y * cellSize.y
                );
                Gizmos.DrawWireCube(center, new Vector3(cellSize.x, cellSize.y, 0f));
            }
        }
    }

    public void ResetState()
    {
        // 1) Kill 延迟隐藏 tween，避免残留回调下一局又把网格关掉
        _hideGridTween?.Kill(false);
        _hideGridTween = null;

        // 2) Kill 这个对象相关的 DOTween（本脚本里 Snap/Move/DelayedCall 都可能残留）
        DOTween.Kill(this);

        // 3) 清理网格可视化对象
        for (int i = 0; i < cellVisuals.Count; i++)
        {
            if (cellVisuals[i] != null)
                Destroy(cellVisuals[i]);
        }
        cellVisuals.Clear();
        gridVisible = false;

        // 4) 希望“Reset 后仍能继续使用网格管理器”，可以重新创建一遍
        // CreateGridVisuals();
        // SetGridVisible(false);

        // 5) 清单例
        if (Instance == this)
            Instance = null;
    }
}
