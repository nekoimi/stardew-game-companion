using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record ApiProviderOption(string Id, string Name, ApiModelOption[] Models);
public sealed record ApiModelOption(string Id, string Name, string[] Reasoning);

public partial class MainWindow
{
    private bool _testingModel;
    private bool UsesCompatibleResponses => ApiProvider.SelectedValue as string == ApiModelSettings.CompatibleProvider;
    private ApiModelSettings ApiSelection() => UsesCompatibleResponses
        ? CompatibleResponsesSelection()
        : new(ApiProvider.SelectedValue as string ?? "deepseek", ApiModel.SelectedValue as string ?? "", ApiReasoning.SelectedValue as string ?? "");

    private ApiModelSettings CompatibleResponsesSelection() => new(ApiModelSettings.CompatibleProvider,
        ApiCustomModel.Text.Trim(), BaseUrl: ApiBaseUrl.Text.Trim().TrimEnd('/'));

    private ApiModelSettings? CompatibleResponsesSettings()
    {
        var selected = CompatibleResponsesSelection();
        return ValidResponsesBaseUrl(selected.BaseUrl) && selected.Model.Length > 0 ? selected : null;
    }

    private static bool ValidResponsesBaseUrl(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !uri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);

    private Control ApiSettingsErrorField()
    {
        if (UsesCompatibleResponses)
        {
            if (!ValidResponsesBaseUrl(ApiBaseUrl.Text.Trim().TrimEnd('/'))) return ApiBaseUrl;
            if (string.IsNullOrWhiteSpace(ApiCustomModel.Text)) return ApiCustomModel;
        }
        else if (ApiModel.SelectedItem is null) return ApiModel;
        return ApiKey;
    }

    private void InitializeApiModel(ApiModelSettings settings, ApiModelSettings? compatible)
    {
        try
        {
            var file = Path.Combine(_root, "runtime", "models.json");
            ApiProvider.ItemsSource = JsonSerializer.Deserialize<ApiProviderOption[]>(File.ReadAllText(file), JsonSerializerOptions.Web);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            ApiModelFeedback.Text = "模型目录不可用，请完整解压绿色包。";
            Log(error.Message);
            return;
        }
        ApiProvider.SelectedValue = settings.Provider;
        if (ApiProvider.SelectedIndex < 0) ApiProvider.SelectedIndex = 0;
        if (UsesCompatibleResponses)
            compatible = settings;
        if (compatible is not null)
        {
            ApiBaseUrl.Text = compatible.BaseUrl;
            ApiCustomModel.Text = compatible.Model;
        }
        if (ApiProvider.SelectedItem is ApiProviderOption selected && selected.Models.Any(model => model.Id == settings.Model))
            ApiModel.SelectedValue = settings.Model;
        if (ApiModel.SelectedItem is ApiModelOption model && model.Reasoning.Contains(settings.Reasoning))
            ApiReasoning.SelectedValue = settings.Reasoning;
    }

    private void ApiProviderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ApiCustomModel is null || ApiProvider.SelectedItem is not ApiProviderOption provider) return;
        ApiEndpointFields.Visibility = ApiCustomModel.Visibility = UsesCompatibleResponses ? Visibility.Visible : Visibility.Collapsed;
        ApiModel.Visibility = UsesCompatibleResponses ? Visibility.Collapsed : Visibility.Visible;
        ApiModel.ItemsSource = provider.Models;
        var preferred = provider.Id == "openai" ? "gpt-5-mini" : "deepseek-flash";
        ApiModel.SelectedValue = provider.Models.FirstOrDefault(model => model.Id == preferred)?.Id
            ?? provider.Models.FirstOrDefault()?.Id;
        ApiKey.Clear();
        ShowApiCredentialState();
    }

    private void ApiBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (ApiCustomModel is null || !UsesCompatibleResponses) return;
        ApiKey.Clear();
        ShowApiCredentialState();
    }

    private void ShowApiCredentialState()
    {
        try
        {
            var selected = ApiSelection();
            if ((!UsesCompatibleResponses || ValidResponsesBaseUrl(selected.BaseUrl)) && ModelCredential.Read(_root, selected).Length > 0)
                ApiModelFeedback.Text = "已保存 API Key。填写新值可替换。";
            else ApiModelFeedback.Text = "";
        }
        catch (Win32Exception error) { ApiModelFeedback.Text = error.Message; }
    }

    private void ApiModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ApiReasoning is null) return;
        var reasoning = (ApiModel.SelectedItem as ApiModelOption)?.Reasoning ?? [];
        ApiReasoningFields.Visibility = reasoning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApiReasoning.ItemsSource = new[] { "" }.Concat(reasoning)
            .Select(value => new ComboBoxItem { Tag = value, Content = value.Length == 0 ? "默认" : ReasoningLabel(value) }).ToArray();
        ApiReasoning.SelectedIndex = 0;
    }

    private string SaveApiCredential()
    {
        var selected = ApiSelection();
        ValidateApiSelection(selected);
        string key = ApiKey.Password.Trim();
        if (key.Length > 0)
        {
            ModelCredential.Write(_root, selected, key);
            ApiKey.Clear();
            ApiModelFeedback.Text = "已保存 API Key。填写新值可替换。";
            return key;
        }
        key = ModelCredential.Read(_root, selected);
        return key.Length > 0 ? key : throw new InvalidOperationException("请填写 API Key。");
    }

    private static void ValidateApiSelection(ApiModelSettings selected)
    {
        if (selected.Provider == ApiModelSettings.CompatibleProvider && !ValidResponsesBaseUrl(selected.BaseUrl))
            throw new InvalidOperationException("请填写 HTTPS Base URL，保留接口前缀，例如 /v1，去掉 /responses。");
        if (selected.Model.Length == 0) throw new InvalidOperationException("请填写模型名称。");
    }

    private async void TestModelClicked(object sender, RoutedEventArgs e)
    {
        if (_testingModel || _closing) return;
        _testingModel = true;
        RefreshControls();
        try
        {
            var selected = ApiSelection();
            ValidateApiSelection(selected);
            string key = ApiKey.Password.Trim();
            if (key.Length == 0) key = ModelCredential.Read(_root, selected);
            ApiModelFeedback.Text = "正在检测模型与工具调用…";
            await DesktopSession.TestModelAsync(_root, selected, key, Log);
            ApiModelFeedback.Text = "模型连接和工具调用均已通过。";
        }
        catch (Exception error) { ApiModelFeedback.Text = error.Message; }
        finally { _testingModel = false; RefreshControls(); }
    }

    private async void ApplyModelClicked(object sender, RoutedEventArgs e)
    {
        if (_testingModel || _closing || _session is not { } session) return;
        _testingModel = true;
        RefreshControls();
        try
        {
            string key = SaveApiCredential();
            await session.ConfigureModelAsync(ApiSelection(), key);
            DesktopInstallation.SaveSettings(_root, Settings());
            ApiModelFeedback.Text = "模型已切换，继续使用当前存档的伙伴数据。";
        }
        catch (Exception error) { ApiModelFeedback.Text = error.Message; }
        finally { _testingModel = false; RefreshControls(); }
    }
}
