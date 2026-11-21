using System.Text.Json;
using System.Windows;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private bool _partnerReady;
    private bool _partnerServiceReady;

    private static string ReasoningLabel(string value) => value switch
    {
        "minimal" => "最低",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        "max" => "最高",
        _ => value
    };

    private void ApplyPartnerStatus(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("connection", out var state)
            || state.ValueKind != JsonValueKind.Object)
        {
            _partnerReady = _partnerServiceReady = false;
            if (_session is not null) PartnerStatus.Text = "伴侣服务连接已中断";
            return;
        }
        _partnerServiceReady = true;
        _partnerReady = Flag(state, "ready");
        PartnerStatus.Visibility = Visibility.Visible;
        PartnerStatus.Text = $"模型 API · {Text(state, "clientName")}";
    }

    private void ResetPartnerConnection()
    {
        _partnerReady = _partnerServiceReady = false;
        PartnerStatus.Visibility = Visibility.Collapsed;
    }
}
