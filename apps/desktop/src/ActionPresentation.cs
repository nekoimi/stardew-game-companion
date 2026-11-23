using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewAI.Companion.Desktop;

public sealed record WorkRecord(string Label, string Text, string Tone = "normal");

internal sealed record WorkResult(string Id, string GoalId, string Scope, string Date, string Skill,
    string State, string Code, string Target, string Text, bool NeedsPlayer)
{
    public bool IsIssue => State != "succeeded" && State != "cancelled";
    public string RepeatKey => string.Join("|", Scope, Date, Skill, State, Code, Target);
}

/// <summary>Player-facing text only. Raw receipts remain available to the runtime and file diagnostics.</summary>
internal static class ActionPresentation
{
    public static string Name(string skill) => skill switch
    {
        "navigate-to" or "navigating" => "移动",
        "water-zone" or "watering" => "浇水",
        "harvest-zone" or "harvesting" => "收获",
        "chop-tree" => "采木",
        "clear-debris" => "清障",
        "cut-grass" => "割草",
        "pickup-items" => "拾取",
        "plant-seeds" => "播种",
        "apply-fertilizer" => "施肥",
        "hoe-tiles" => "翻地",
        "refill-watering-can" => "补水",
        "deposit-chest" => "物品入箱",
        "withdraw-chest" => "从箱子取物",
        "organize-chest" => "整理箱子",
        "open-chest" => "打开箱子",
        "ship-items" => "出货",
        "craft-items" => "制作物品",
        "eat-food" => "进食",
        "sleep" => "就寝",
        "rest" => "休息",
        "pet-animal" or "pet-animals" => "抚摸动物",
        "feed-animals" => "喂养动物",
        "collect-animal-produce" or "harvest-animals" => "收集动物产物",
        "toggle-animal-door" => "开关动物门",
        "insert-machine" => "机器投料",
        "collect-machine" => "收取成品",
        "place-items" => "放置物品",
        "remove-items" => "回收物品",
        "purchase-items" => "采购",
        "build-building" or "construct-building" => "建造建筑",
        "upgrade-building" => "升级建筑",
        "move-building" => "移动建筑",
        "purchase-animal" => "购买动物",
        "open-shop" => "打开商店",
        "talk-npc" => "与村民交谈",
        "attack" => "战斗",
        "fish" => "钓鱼",
        "interact-tile" or "native-menu-select" or "native-answer" or "native-advance" => "游戏交互",
        _ when skill.StartsWith("observe-", StringComparison.Ordinal) || skill.StartsWith("query-", StringComparison.Ordinal) => "查看游戏情况",
        _ => "游戏操作"
    };

    public static string State(string state) => state switch
    {
        "succeeded" => "已完成",
        "partially-succeeded" or "partial" => "部分完成",
        "cancelled" => "已取消",
        "failed" or "rejected" => "暂未完成",
        _ => "待核对"
    };

    public static string Activity(string skill) => string.IsNullOrEmpty(skill) || skill == "idle"
        ? "正在安排下一步" : $"正在{Name(skill)}";

    public static string DisplayText(string text) => Regex.Replace(text, @"\bbedtime\b", "就寝时间", RegexOptions.IgnoreCase);

    public static string GoalTitle(string text)
    {
        var display = DisplayText(text).Trim();
        int separator = display.IndexOfAny(new[] { '。', '；', ';', '\n' });
        if (separator > 0) display = display[..separator];
        return display.Length > 36 ? display[..36] + "…" : display;
    }

    public static WorkResult Read(JsonElement receipt)
    {
        var context = Child(receipt, "workContext");
        string id = Text(receipt, "commandId"), goal = Text(context, "goalId");
        string skill = Text(receipt, "skillId"), state = Text(receipt, "terminalState");
        string code = Text(Child(receipt, "error"), "code");
        var target = Child(context, "target");
        // Missing targets must not make unrelated attempts look like retries of one destination.
        string targetKey = target.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? Text(context, "locationId") + ":" + target.GetRawText() : id;
        string scope = Text(receipt, "gameSessionId") + ":" + goal + ":" + Text(context, "requestId");
        string name = Name(skill), reason = Reason(code);
        string text = state switch
        {
            "succeeded" => $"{name}已完成。",
            "partially-succeeded" or "partial" => $"已完成部分{name}。{reason}",
            "cancelled" => $"{name}已停止，已完成的部分会保留。",
            "failed" or "rejected" => reason.Length > 0 ? reason : $"本次{name}未能完成，请查看游戏状态。",
            _ => "这次操作的结果尚未确认，请先核对旧动作。"
        };
        if (state is "partial" or "partially-succeeded" && reason.Length == 0)
            text += "请根据当前游戏情况确认剩余工作。";
        return new WorkResult(id, goal, scope, Text(receipt, "gameDate"), skill, state, code, targetKey, text,
            Flag(receipt, "playerActionRequired") || state is not ("succeeded" or "partially-succeeded" or "partial" or "failed" or "rejected" or "cancelled"));
    }

    private static string Reason(string code) => code.ToUpperInvariant() switch
    {
        "DESTINATION_UNREACHABLE" => "目标位置暂时无法到达，需要重新确认可站立的位置或路线。",
        "ROUTE_PLANNING_TIMEOUT" => "暂时未能找到路线，本次移动已停止。",
        "NO-ITEM" => "到达目标位置时已没有可拾取物，需要重新查看地面和背包。",
        "INVENTORY-FULL" or "INVENTORY_FULL" => "背包空间不足，需要先整理物品。",
        "NO-WATER" or "WATER_EMPTY" or "INSUFFICIENT_WATER" => "水壶里的水不足，需要先补水。",
        "INSUFFICIENT_STAMINA" or "STAMINA_LOW" or "LOW_STAMINA" => "体力不足，需要先恢复体力。",
        "BUDGET_EXCEEDED" => "本次工作已达到执行限额，需要确认剩余工作。",
        "TIMEOUT" => "这项操作长时间没有进展，已停止本次执行。",
        "PLAYER_OBSTRUCTION" or "PLAYER-OBSTRUCTION" => "路线受到人物阻挡，需要确认周围情况。",
        "NATIVE_ROUTE_CHANGED" => "人物位置发生变化，需要重新确认路线。",
        "NATIVE_UI_BLOCKED" or "MENU_BLOCKED" => "当前游戏菜单或事件阻挡了操作，请先处理游戏中的提示。",
        "NO_BED" or "BED_UNAVAILABLE" => "暂时找不到可用的床，需要确认休息位置。",
        "LOCATION_NOT_FOUND" => "目标地点暂时不可用，需要重新查看游戏情况。",
        "CANCELLED" => "操作已停止，已完成的部分会保留。",
        "" => "",
        _ => "当前条件下未能完成，需要根据游戏情况确认原因。详细信息已保留供排查。"
    };

    public static IReadOnlyList<WorkRecord> Process(IEnumerable<WorkResult> results)
    {
        var records = new List<WorkRecord>();
        WorkResult? previous = null;
        int repeated = 0;
        foreach (var result in results)
        {
            if (previous is not null && previous.IsIssue && result.IsIssue && previous.RepeatKey == result.RepeatKey)
            {
                repeated++;
                records[^1] = Record(result, result.Text + $"（连续 {repeated} 次）");
            }
            else
            {
                repeated = 1;
                records.Add(Record(result, result.Text));
            }
            previous = result;
        }
        records.Reverse();
        return records;
    }

    public static IReadOnlyList<WorkRecord> Summaries(IEnumerable<WorkResult> results) => results
        .GroupBy(item => (item.Scope, item.Date, item.Skill))
        .Select(group =>
        {
            var last = group.Last();
            var text = last.Text;
            if (!last.IsIssue && group.Any(item => item.IsIssue)) text += "此前有受阻或部分完成的记录，可展开工作过程查看。";
            return Record(last, text);
        }).Reverse().ToArray();

    private static WorkRecord Record(WorkResult result, string text) => new(
        $"{DateLabel(result.Date)} · {Name(result.Skill)} · {State(result.State)}", text,
        result.IsIssue ? "warning" : "normal");

    public static string DateLabel(string date)
    {
        var parts = date.Split(':');
        if (parts.Length != 3) return "日期未记录";
        string season = parts[1] switch { "spring" => "春季", "summer" => "夏季", "fall" => "秋季", "winter" => "冬季", _ => "" };
        return season.Length == 0 ? "日期未记录" : $"第 {parts[0]} 年{season}第 {parts[2]} 天";
    }

    private static JsonElement Child(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement parent, string key) => Child(parent, key) is var value
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Flag(JsonElement parent, string key) => Child(parent, key).ValueKind == JsonValueKind.True;
}
