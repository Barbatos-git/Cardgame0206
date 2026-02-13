using UnityEngine;
using CardGame.Data.Defs;

namespace CardGame.Gameplay.Units
{
    /// <summary>
    /// 外部（堆叠/合成/カード同士の特殊相互作用）から
    /// 「武器を渡す」ための受け口
    /// </summary>
    public sealed class EquipmentReceiver : MonoBehaviour
    {
        [SerializeField] private CharacterCard owner;

        private void Awake()
        {
            // Reset() はEditor時のみ呼ばれるため、実行時の安全策として Awake でも補完する
            if (owner == null)
                owner = GetComponent<CharacterCard>();
        }

        /// <summary>
        /// 武器装備を試みる（推奨：武器カード(CardDefinition)で渡す）
        /// </summary>
        public bool TryEquip(CardDefinition weaponCardDef, int weaponStackCount, out string replacedWeaponCardId)
        {
            replacedWeaponCardId = null;
            if (owner == null) return false;
            return owner.TryEquipWeapon(weaponCardDef, weaponStackCount, out replacedWeaponCardId);
        }

        /// <summary>
        /// 旧API互換：WeaponDefinition単体では「どの武器カードか」特定できないため
        /// 見た目（左下角アイコン等）が完全には同期できません。
        /// </summary>
        [System.Obsolete("Use TryEquip(CardDefinition,int,out string) instead.")]
        public bool TryEquip(WeaponDefinition weapon)
        {
            return false;
        }
    }
}
