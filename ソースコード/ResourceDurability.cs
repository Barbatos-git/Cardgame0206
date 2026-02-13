using UnityEngine;
using static CardDefinition;

/// <summary>
/// 资源卡耐久（非 Enemy/非 Character 的卡都可用）
/// 注意：资源卡本身没脚本，所以由敌人攻击时自动 AddComponent 并初始化
/// </summary>
[DisallowMultipleComponent]
public sealed class ResourceDurability : MonoBehaviour
{
    [Header("Runtime")]
    [SerializeField] private int maxDurability;
    [SerializeField] private int durability;
    [SerializeField] private bool initialized;

    public int Max => maxDurability;
    public int Current => durability;
    public bool IsBroken => initialized && durability <= 0;

    // Installer 调用：用稀有度初始化
    public void InitFromDefinition(CardDefinition def)
    {
        initialized = true;

        var rarity = def != null ? def.rarity : CardRarity.Common;
        maxDurability = Mathf.Max(1, GetPerCardDurability(rarity));
        durability = Mathf.Clamp(durability <= 0 ? maxDurability : durability, 0, maxDurability);
    }

    public void TakeDamage(int amount)
    {
        if (!initialized)
        {
            // 兜底：如果没初始化也能工作
            var view = GetComponent<CardWorldView>();
            InitFromDefinition(view != null ? view.definition : null);
        }

        amount = Mathf.Max(1, amount);
        durability -= amount;

        if (durability <= 0)
        {
            durability = 0;

            var view = GetComponent<CardWorldView>();
            if (view != null)
            {
                // 安全从堆中销毁（Stacklands：只碎顶牌，堆仍保留）
                view.DespawnSafelyFromStack();
            }
            else
            {
                Destroy(gameObject);
            }
        }
    }

    private static int GetPerCardDurability(CardRarity rarity)
    {
        // 你可以按需要调整数值
        return rarity switch
        {
            CardRarity.Common => 3,
            CardRarity.Uncommon => 5,
            CardRarity.Rare => 8,
            CardRarity.Epic => 12,
            CardRarity.Legendary => 18,
            _ => 3
        };
    }
}
