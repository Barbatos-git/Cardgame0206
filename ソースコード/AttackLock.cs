using UnityEngine;

public sealed class AttackLock : MonoBehaviour
{
    public float LockedUntil { get; private set; }

    public bool IsLocked => Time.time < LockedUntil;

    public void LockFor(float seconds)
    {
        LockedUntil = Mathf.Max(LockedUntil, Time.time + Mathf.Max(0f, seconds));
    }
}
