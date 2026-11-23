using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewAI.Companion.Desktop;

public sealed record WorkRecord(string Label, string Text, string Tone = "normal");
internal sealed record WorkQuantity(string Kind, int Value);

internal sealed record WorkResult(string Id, string GoalId, string Scope, string Date, string Skill,
    string State, string Code, string Target, string Text, bool NeedsPlayer, IReadOnlyList<WorkQuantity> Quantities)
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
        "inspect-route" => "检查路线",
        "inspect-water-sources" => "查看补水位置",
        _ when skill.StartsWith("observe-", StringComparison.Ordinal) || skill.StartsWith("query-", StringComparison.Ordinal)
            || skill.StartsWith("inspect-", StringComparison.Ordinal) => "查看游戏情况",
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

    public static string Phase(string phase, string skill = "") => phase switch
    {
        "thinking" => "正在思考与安排下一步",
        "planning-route" => "正在寻找可通行的路线",
        "validating" => "正在检查执行条件",
        "executing" => Activity(skill),
        "verifying" => "正在复查游戏中的实际结果",
        "waiting-game" => "已发出操作，等待游戏确认",
        "waiting-receipt" => "等待游戏返回最终结果",
        "reconciling" => "正在查询旧动作和当前游戏状态",
        "unresolved" => "结果尚未确认，请先核对旧动作",
        "stopping" => "正在等待旧动作停止并核对结果",
        "needs-player" => "有事项需要你确认，请查看伙伴消息",
        "paused" => "工作已暂停",
        "player-control" => "玩家控制中",
        _ => "等待下一步安排"
    };

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
        string request = Text(context, "triggerRequestId");
        string scope = Text(receipt, "gameSessionId") + ":" + goal + ":" + (request.Length > 0 ? request : Text(context, "requestId"));
        string name = Name(skill), reason = Reason(code);
        string text = state switch
        {
            "succeeded" => $"{name}已完成。",
            "partially-succeeded" or "partial" => $"已完成部分{name}。{reason}",
            "cancelled" => $"{name}已停止，已确认完成的部分会保留。",
            "rejected" => $"本次{name}未开始执行。{reason}",
            "failed" => (reason.Length > 0 ? reason : $"本次{name}未能完成，请查看游戏状态。")
                + (skill == "navigate-to" ? "人物可能已移动了一段距离。" : ""),
            _ => "这次操作的结果尚未确认，请先核对旧动作。"
        };
        if (state is "partial" or "partially-succeeded" && reason.Length == 0)
            text += "请根据当前游戏情况确认剩余工作。";
        var quantities = ReadQuantities(Child(receipt, "evidence"));
        text += FormatQuantities(quantities);
        return new WorkResult(id, goal, scope, Text(receipt, "gameDate"), skill, state, code, targetKey, text,
            Flag(receipt, "playerActionRequired") || state is not ("succeeded" or "partially-succeeded" or "partial" or "failed" or "rejected" or "cancelled"), quantities);
    }

    private static IReadOnlyList<WorkQuantity> ReadQuantities(JsonElement evidence)
    {
        var quantities = Child(evidence, "quantities");
        var result = new List<WorkQuantity>();
        if (quantities.ValueKind != JsonValueKind.Array) return result;
        foreach (var quantity in quantities.EnumerateArray())
        {
            var count = Child(quantity, "value");
            if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int value) && value > 0)
                result.Add(new WorkQuantity(Text(quantity, "kind"), value));
        }
        return result;
    }

    private static string FormatQuantities(IEnumerable<WorkQuantity> quantities)
    {
        var parts = new List<string>();
        foreach (var quantity in quantities)
        {
            int value = quantity.Value;
            var text = quantity.Kind switch
            {
                "watered-tiles" => $"已浇水 {value} 格",
                "harvested-tiles" => $"已收获 {value} 格作物",
                "hoed-tiles" => $"已翻地 {value} 格",
                "planted-tiles" => $"已播种 {value} 格",
                "felled-trees" => $"已砍倒 {value} 棵树",
                "deposited-items" => $"已存入 {value} 件物品",
                "withdrawn-items" => $"已取出 {value} 件物品",
                "shipped-items" => $"已出货 {value} 件物品",
                "collected-items" => $"确认拾取 {value} 件物品",
                "inventory-gained" => $"执行期间背包增加 {value} 件物品",
                _ => ""
            };
            if (text.Length > 0) parts.Add(text);
        }
        return parts.Count > 0 ? string.Join("；", parts) + "。" : "";
    }

    public static IReadOnlyList<WorkRecord> Stages(IEnumerable<WorkResult> results) => results
        .GroupBy(item => (item.Scope, item.Date))
        .Select(group =>
        {
            var latest = group.GroupBy(item => (item.Skill, item.Target)).Select(items => items.Last()).ToArray();
            var completed = latest.Where(item => item.State == "succeeded").Select(item => Name(item.Skill)).Distinct();
            var issues = latest.Where(item => item.IsIssue).Select(item => Name(item.Skill)).Distinct();
            var parts = new List<string>();
            var quantities = group.DistinctBy(item => item.Id).SelectMany(item => item.Quantities)
                .GroupBy(item => item.Kind).Select(items => new WorkQuantity(items.Key,
                    (int)Math.Min(int.MaxValue, items.Sum(item => (long)item.Value))));
            var amounts = FormatQuantities(quantities);
            if (amounts.Length > 0) parts.Add(amounts);
            if (completed.Any()) parts.Add("已完成的操作：" + string.Join("、", completed) + "。");
            if (issues.Any()) parts.Add("仍需复查：" + string.Join("、", issues) + "。");
            if (latest.Any(item => item.State == "cancelled")) parts.Add("有操作已停止，已确认的进展保留。");
            parts.Add("剩余工作以当前游戏情况为准，可展开查看过程。");
            return new WorkRecord($"{DateLabel(group.Key.Date)} · 阶段进展", string.Join("", parts),
                latest.Any(item => item.IsIssue) ? "warning" : "normal");
        }).Reverse().ToArray();

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
        "REST_INTERRUPTED" => "休息途中被打断，尚未确认恢复到目标体力，需要查看位置和体力后重新安排。",
        "MOVEMENT_BLOCKED" or "PATH_BLOCKED" or "NATIVE_COLLISION" or "NATIVE_MOVEMENT_STALLED" => "行走受到阻挡，已停止本次操作，需要重新查看路线或障碍。",
        "DESTINATION_PATH_BLOCKED" or "EXIT_PATH_BLOCKED" or "PATH_UNREACHABLE" => "路线受到障碍阻挡，需要检查障碍后决定清理或改道。",
        "ARRIVAL_BLOCKED" => "目标地图的落脚位置被挡住，需要确认其他入口。",
        "TARGET_UNAVAILABLE" or "DESTINATION_UNREACHABLE_NOW" => "目标入口当前不可用，需要检查开放时间或游戏事件。",
        "SEARCH_LIMIT_EXCEEDED" or "ROUTE_OBSERVATION_TIMEOUT" => "本次路线检查未能完成，需要重新观察后选择较近的途经地点。",
        "NO_ROUTE" or "NO_EXIT" => "暂时没有找到可通行的出口或路线，需要查看地图。",
        "INVALID_PARAMETERS" => "操作参数不适用，需要根据最新游戏情况重新安排。",
        "STALE_WORLD_REVISION" or "WORLD_REVISION_MISMATCH" => "游戏情况已经变化，需要重新观察后再操作。",
        "ACTOR_BUSY" or "BUSY" => "伙伴正在执行另一项操作，请等其结束。",
        "PLAYER_ACTION_UNSUPPORTED" or "UNSUPPORTED_SKILL" => "当前游玩模式暂不支持这项操作。",
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
