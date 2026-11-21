using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private bool NodeRuntime => DesktopInstallation.UsesNode(_root);
    private bool _testingModel;
    private ApiModelSettings ApiSelection() => new(ApiProvider.SelectedValue as string ?? "deepseek", ApiModel.Text.Trim());

    private void InitializeApiModel(ApiModelSettings settings)
    {
        if (!NodeRuntime) return;
        PartnerSourceLabel.Visibility = PartnerSource.Visibility = BuiltinFields.Visibility = LegacyImportFields.Visibility = Visibility.Collapsed;
        ApiModelFields.Visibility = Visibility.Visible;
        ApiProvider.SelectedValue = settings.Provider;
        if (ApiProvider.SelectedIndex < 0) ApiProvider.SelectedIndex = 0;
        ApiModel.Text = settings.Model;
    }

    private void ApiProviderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ApiModel is null || ApiProvider.SelectedValue is not string provider) return;
        ApiModel.Text = provider == "openai" ? "gpt-5-mini" : "deepseek-flash";
        ApiKey.Clear();
        try
        {
            if (ModelCredential.Read(_root, provider).Length > 0)
                ApiModelFeedback.Text = "已保存 API Key。填写新值可替换。";
            else ApiModelFeedback.Text = "";
        }
        catch (Win32Exception error) { ApiModelFeedback.Text = error.Message; }
    }

    private string SaveApiCredential()
    {
        var selected = ApiSelection();
        if (selected.Model.Length == 0) throw new InvalidOperationException("请填写模型名称。");
        string key = ApiKey.Password.Trim();
        if (key.Length > 0)
        {
            ModelCredential.Write(_root, selected.Provider, key);
            ApiKey.Clear();
            ApiModelFeedback.Text = "已保存 API Key。填写新值可替换。";
            return key;
        }
        key = ModelCredential.Read(_root, selected.Provider);
        return key.Length > 0 ? key : throw new InvalidOperationException("请填写 API Key。");
    }

    private async void TestModelClicked(object sender, RoutedEventArgs e)
    {
        if (!NodeRuntime || _testingModel || _session is not null || _closing) return;
        _testingModel = true;
        RefreshControls();
        try
        {
            var selected = ApiSelection();
            string key = ApiKey.Password.Trim();
            if (key.Length == 0) key = ModelCredential.Read(_root, selected.Provider);
            ApiModelFeedback.Text = "正在检测模型与工具调用…";
            await DesktopSession.TestModelAsync(_root, selected, key, Log);
            ApiModelFeedback.Text = "模型连接和工具调用均已通过。";
        }
        catch (Exception error) { ApiModelFeedback.Text = error.Message; }
        finally { _testingModel = false; RefreshControls(); }
    }
}
