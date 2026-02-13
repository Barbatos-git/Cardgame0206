using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class FusionResolver : MonoBehaviour
{
    [SerializeField] private TextAsset craftJson;

    private FusionRuleTable table;
    private CardDatabase db;

    private List<CompiledTagRule> tagRules;
    private Dictionary<string, FusionRuleTable.IdRule> idRuleMap;

    private void Awake()
    {
        if (craftJson == null)
        {
            Debug.LogError("[FusionResolver] craftJson 未设置（TextAsset 为空）", this);
            enabled = false;
            return;
        }

        table = JsonUtility.FromJson<FusionRuleTable>(craftJson.text);
        if (table == null)
        {
            Debug.LogError("[FusionResolver] craftJson 解析失败（table == null）", this);
            enabled = false;
            return;
        }

        CompileTagRules();
        CompileIdRules();
    }

    // ===== 对外唯一 API =====
    public bool TryResolve(
        List<CardDefinition> materials,
        out CardDefinition result,
        out float seconds)
    {
        if (db == null)
        {
            db = CardDatabase.Instance;
            if (db == null)
            {
                Debug.LogError("[FusionResolver] CardDatabase.Instance == null。请确认场景中存在 CardDatabase，且已在 Awake 初始化。", this);
                result = null;
                seconds = 0;
                return false;
            }
        }

        result = null;
        seconds = 0f;

        if (materials == null || materials.Count < 2)
            return false;

        // 1) tag 驱动（主）
        CardTag combined = CardTag.None;
        foreach (var m in materials)
            combined |= m.tags;

        foreach (var r in tagRules)
        {
            if (!r.Matches(combined)) continue;

            result = db.GetCardById(r.outputId);
            seconds = r.seconds;
            return result != null;
        }

        // 2) id 驱动（补）
        var key = BuildIdKey(materials.Select(m => m.id));
        if (idRuleMap.TryGetValue(key, out var idRule))
        {
            result = db.GetCardById(idRule.outputId);
            seconds = idRule.seconds;
            return result != null;
        }

        return false;
    }

    // ===== 内部 =====

    private void CompileTagRules()
    {
        tagRules = new List<CompiledTagRule>();

        foreach (var r in table.tagRules)
        {
            tagRules.Add(new CompiledTagRule
            {
                requiredAll = ParseTags(r.requiredAll),
                requiredAny = ParseTags(r.requiredAny),
                forbidden = ParseTags(r.forbidden),
                outputId = r.outputId,
                seconds = r.seconds,
                priority = r.priority
            });
        }

        tagRules = tagRules
            .OrderByDescending(r => r.priority)
            .ToList();
    }

    private void CompileIdRules()
    {
        idRuleMap = new Dictionary<string, FusionRuleTable.IdRule>();

        foreach (var r in table.idRules)
        {
            var key = BuildIdKey(r.inputs);
            idRuleMap[key] = r;
        }
    }

    private static string BuildIdKey(IEnumerable<string> ids)
    {
        return string.Join("|", ids.OrderBy(x => x));
    }

    private static CardTag ParseTags(List<string> names)
    {
        CardTag result = CardTag.None;
        if (names == null) return result;

        foreach (var n in names)
            if (System.Enum.TryParse(n, out CardTag t))
                result |= t;

        return result;
    }

    private class CompiledTagRule
    {
        public CardTag requiredAll;
        public CardTag requiredAny;
        public CardTag forbidden;
        public string outputId;
        public float seconds;
        public int priority;

        public bool Matches(CardTag combined)
        {
            if ((combined & requiredAll) != requiredAll) return false;
            if (requiredAny != CardTag.None && (combined & requiredAny) == CardTag.None) return false;
            if (forbidden != CardTag.None && (combined & forbidden) != CardTag.None) return false;
            return true;
        }
    }
}
