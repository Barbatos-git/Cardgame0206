using UnityEngine;

public sealed class FusionZoneCardSpawner : MonoBehaviour
{
    [SerializeField] private GameObject fusionZonePrefab;

    private GameObject _spawnedZone;
    private IFusionZoneHost _host;
    private bool _active;
    private bool _warnedNoHost;

    /// <summary>由 Installer 在确认该卡是 FusionZoneSource 后调用</summary>
    public void Activate(GameObject prefab)
    {
        _active = true;
        fusionZonePrefab = prefab;
        TrySpawn();
    }

    /// <summary>由 Installer 在确认该卡不是 FusionZoneSource 时调用</summary>
    public void Deactivate()
    {
        _active = false;
        Cleanup();
    }

    private void TrySpawn()
    {
        if (!_active) return;
        if (_spawnedZone != null) return;
        if (fusionZonePrefab == null) return;

        _host ??= FusionZoneHostLocator.Get();
        if (_host == null)
        {
            if (!_warnedNoHost)
            {
                Debug.LogWarning(
                    "[FusionZoneCardSpawner] IFusionZoneHost not found yet. " +
                    "Make sure FusionZoneHostBridge is on an ACTIVE UI GameObject in a loaded scene.",
                    this
                );
                _warnedNoHost = true;
            }
            return;
        }

        _warnedNoHost = false;
        _spawnedZone = _host.SpawnFusionZone(fusionZonePrefab, gameObject);
    }

    private void Cleanup()
    {
        if (_spawnedZone == null) return;

        _host ??= FusionZoneHostLocator.Get();
        if (_host != null) _host.DespawnFusionZone(_spawnedZone);
        else Destroy(_spawnedZone);

        _spawnedZone = null;
    }

    private void OnDisable() => Cleanup();
    private void OnDestroy() => Cleanup();
}
