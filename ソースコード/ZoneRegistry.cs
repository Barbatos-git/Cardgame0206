using System.Collections.Generic;
using CardGame.Core.Zones;

namespace CardGame.Gameplay.Zones
{
    /// <summary>
    /// シーン内のZoneをキャッシュ（Find不要）
    /// </summary>
    public static class ZoneRegistry
    {
        private static readonly List<IZone> _zones = new();
        public static IReadOnlyList<IZone> Zones => _zones;

        public static void Register(IZone zone)
        {
            if (zone == null) return;
            if (_zones.Contains(zone)) return;
            _zones.Add(zone);
        }

        public static void Unregister(IZone zone)
        {
            if (zone == null) return;
            _zones.Remove(zone);
        }
    }
}
