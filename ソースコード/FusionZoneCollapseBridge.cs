using UnityEngine;

public class FusionZoneCollapseBridge : MonoBehaviour, IFusionZoneCollapseHandler
{
    [SerializeField] private FusionZone zone;

    private void Awake()
    {
        if (zone == null)
            zone = GetComponent<FusionZone>() ?? GetComponentInChildren<FusionZone>(true);
    }

    public bool OnBeforeCollapse()
    {
        if (zone == null) return true;

        // 收起前：把本 zone 内的卡堆挪回普通区
        zone.EvacuateCardsToNormalArea();

        return true; // 允许收起
    }
}
