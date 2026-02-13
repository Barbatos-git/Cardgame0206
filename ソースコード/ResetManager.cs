using UnityEngine;
using System.Linq;

public static class ResetManager
{
    /// <summary>
    /// 新开一局前调用：自动 Reset 所有 IResettable
    /// </summary>
    public static void ResetForNewRun()
    {
        Debug.Log("[ResetManager] ResetForNewRun start");

        // 找到当前存在的所有 MonoBehaviour（包括 inactive + DDOL）
        var resettables = Object
            .FindObjectsOfType<MonoBehaviour>(true)
            .OfType<IResettable>();

        foreach (var r in resettables)
        {
            try
            {
                r.ResetState();
                Debug.Log($"[ResetManager] Reset: {r.GetType().Name}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ResetManager] Reset failed: {r.GetType().Name}\n{e}");
            }
        }

        Debug.Log("[ResetManager] ResetForNewRun done");
    }
}
