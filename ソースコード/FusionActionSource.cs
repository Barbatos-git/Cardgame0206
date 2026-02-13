using UnityEngine;

public class FusionActionSource : MonoBehaviour, IFusionActionSource
{
    [SerializeField] private FusionZone zone; // Gameplay 类型只存在于 Gameplay

    private void Awake()
    {
        if (zone == null) zone = GetComponentInParent<FusionZone>();
    }

    public void RequestFuse()
    {
        if (zone != null) zone.TryFusionAll();
    }
}
