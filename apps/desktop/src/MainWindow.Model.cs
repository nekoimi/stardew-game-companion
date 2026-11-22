using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record ApiProviderOption(string Id, string Name, ApiModelOption[] Models, string Protocol, string DefaultModel, bool Stream);
public sealed record ApiModelOption(string Id, string Name, string[] Reasoning);

public partial class MainWindow
{
    private static readonly ApiModelOption ManualModel = new("", "手填模型 ID…", []);
    private bool _testingModel;
    private bool _modelBusy;
    private bool IsCustomProvider => ApiProvider.SelectedValue as string == ApiModelSettings.CustomProvider;
    private bool IsManualModel => !IsCustomProvider && ReferenceEquals(ApiModel.SelectedItem, ManualModel);

    private ApiModelSettings ApiSelection()
    {
        var provider = ApiProvider.SelectedItem as ApiProviderOption
            ?? throw new InvalidOperationException("请选择模型服务商。");
        if (IsCustomProvider)
        {
            return new(provider.Id, ApiCustomModel.Text.Trim(), BaseUrl: ApiBaseUrl.Text.Trim().TrimEnd('/'),
                Protocol: ApiProtocol.SelectedValue as string, Stream: ApiCustomStream.IsChecked == true);
        }
        return new(provider.Id, IsManualModel ? ApiManualModel.Text.Trim() : ApiModel.SelectedValue as string ?? "",
            ApiReasoning.SelectedValue as string ?? "", Protocol: provider.Protocol, Stream: ApiOfficialStream.IsChecked == true);
    }

    private static bool ValidBaseUrl(string text)
        => text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !text.Any(character => char.IsWhiteSpace(character) || character is '\\' or '?' or '#')
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            && !uri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);

    private Control ApiSettingsErrorField()
    {
        if (ApiProvider.SelectedItem is null) return ApiProvider;
        if (IsCustomProvider)
        {
            if (!ValidBaseUrl(ApiBaseUrl.Text.Trim().TrimEnd('/'))) return ApiBaseUrl;
            if (ApiProtocol.SelectedItem is null) return ApiProtocol;
            if (string.IsNullOrWhiteSpace(ApiCustomModel.Text)) return ApiCustomModel;
        }
        else if (IsManualModel && string.IsNullOrWhiteSpace(ApiManualModel.Text)) return ApiManualModel;
        return ApiKey;
    }

    private void InitializeApiModel(ApiModelSettings settings, ApiModelSettings? custom)
    {
        try
        {
            var file = Path.Combine(_root, "runtime", "models.json");
            ApiProvider.ItemsSource = JsonSerializer.Deserialize<ApiProviderOption[]>(File.ReadAllText(file), JsonSerializerOptions.Web);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            ApiModelFeedback.Text = "模型目录不可用，请完整解压绿色包。";
            LogError("model_catalogue_read_failed", "无法读取模型目录。", error);
            return;
        }

        ApiProvider.SelectedValue = settings.Provider;
        if (ApiProvider.SelectedIndex < 0) ApiProvider.SelectedIndex = 0;
        if (settings.IsCustom) custom = settings;
        if (custom is not null)
        {
            ApiBaseUrl.Text = custom.BaseUrl;
            ApiCustomModel.Text = custom.Model;
            ApiProtocol.SelectedValue = custom.Protocol ?? "responses";
            ApiCustomStream.IsChecked = custom.Stream;
        }
        if (!IsCustomProvider && ApiProvider.SelectedItem is ApiProviderOption selected && settings.Provider == selected.Id)
        {
            if (selected.Models.Any(model => model.Id == settings.Model)) ApiModel.SelectedValue = settings.Model;
            else
            {
                ApiModel.SelectedItem = ManualModel;
                ApiManualModel.Text = settings.Model;
            }
            ApiOfficialStream.IsChecked = settings.Stream;
            if (ApiModel.SelectedItem is ApiModelOption model && model.Reasoning.Contains(settings.Reasoning))
                ApiReasoning.SelectedValue = settings.Reasoning;
        }
        ShowApiCredentialState();
    }

    private void ApiProviderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ApiModelFeedback is null || ApiProvider.SelectedItem is not ApiProviderOption provider) return;
        var customVisibility = IsCustomProvider ? Visibility.Visible : Visibility.Collapsed;
        ApiEndpointFields.Visibility = ApiCustomModel.Visibility = ApiCustomStream.Visibility = customVisibility;
        ApiModel.Visibility = ApiAdvanced.Visibility = IsCustomProvider ? Visibility.Collapsed : Visibility.Visible;
        ApiModel.ItemsSource = IsCustomProvider ? [] : provider.Models.Append(ManualModel).ToArray();
        ApiModel.SelectedValue = provider.DefaultModel;
        ApiOfficialStream.IsChecked = provider.Stream;
        ApiKey.Clear();
        ShowApiCredentialState();
    }

    private void ApiBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (ApiModelFeedback is null || !IsCustomProvider) return;
        ApiKey.Clear();
        ShowApiCredentialState();
    }

    private void ApiOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (ApiModelFeedback is not null) ShowApiCredentialState();
    }

    private void ShowApiCredentialState()
    {
        ApiModelFeedback.Text = "";
        ApiCredentialHint.Text = "留空表示沿用已保存的 API Key，填写新值可替换。";
        if (ApiProvider.SelectedItem is null || ApiKey.Password.Length > 0) return;
        try
        {
            var selected = ApiSelection();
            if ((!selected.IsCustom || ValidBaseUrl(selected.BaseUrl)) && ModelCredential.Read(_root, selected).Length > 0)
                ApiModelFeedback.Text = "已保存 API Key，留空即可继续使用。";
        }
        catch (Win32Exception error) { ApiModelFeedback.Text = error.Message; }
    }

    private void ApiModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ApiModelFeedback is null) return;
        ApiManualModel.Visibility = IsManualModel ? Visibility.Visible : Visibility.Collapsed;
        var reasoning = (ApiModel.SelectedItem as ApiModelOption)?.Reasoning ?? [];
        ApiReasoningFields.Visibility = reasoning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApiReasoning.ItemsSource = new[] { "" }.Concat(reasoning)
            .Select(value => new ComboBoxItem { Tag = value, Content = value.Length == 0 ? "默认" : ReasoningLabel(value) }).ToArray();
        ApiReasoning.SelectedIndex = 0;
        ShowApiCredentialState();
    }

    private static void ValidateApiSelection(ApiModelSettings selected)
    {
        if (selected.IsCustom && !ValidBaseUrl(selected.BaseUrl))
            throw new InvalidOperationException("请填写 HTTPS Base URL，保留接口前缀，例如 /v1，去掉具体接口路径。");
        if (selected.IsCustom && selected.Protocol is not ("chat-completions" or "responses"))
            throw new InvalidOperationException("请选择接口协议。");
        if (selected.Model.Length == 0 || selected.Model.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效的模型 ID。");
    }

    private string ResolveApiKey(ApiModelSettings selected)
    {
        ValidateApiSelection(selected);
        string key = ApiKey.Password.Trim();
        if (key.Length == 0) key = ModelCredential.Read(_root, selected);
        return key.Length > 0 ? key : throw new InvalidOperationException("请填写 API Key。");
    }

    private void SaveModelSettings(DesktopSettings settings, ApiModelSettings selected, string key)
    {
        if (ApiKey.Password.Trim().Length > 0) ModelCredential.Write(_root, selected, key);
        SaveDesktopSettings(settings with { ApiModel = selected,
            CustomModel = selected.IsCustom ? selected : settings.CustomModel });
    }

    private void SaveModelClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = ApiSelection();
            SaveModelSettings(_savedSettings, selected, ResolveApiKey(selected));
            ApiModelFeedback.Text = "模型设置已保存，下次启动生效。";
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception)
        {
            ShowSettingsError(ModelSettingsTab, ApiSettingsErrorField(), ApiModelFeedback, error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ApiModelFeedback.Text = "模型设置保存失败，请检查解压目录是否可写。";
            LogError("model_settings_save_failed", "无法保存模型设置。", error);
        }
    }

    private async void TestModelClicked(object sender, RoutedEventArgs e)
    {
        if (_testingModel || _closing) return;
        _testingModel = true;
        RefreshControls();
        try
        {
            var selected = ApiSelection();
            string key = ResolveApiKey(selected);
            ApiModelFeedback.Text = "正在检测模型与工具调用…";
            await DesktopSession.TestModelAsync(_root, selected, key, _logger);
            ApiModelFeedback.Text = "模型连接和工具调用均已通过。";
        }
        catch (Exception error) { ApiModelFeedback.Text = error.Message; }
        finally { _testingModel = false; RefreshControls(); }
    }

    private async void ApplyModelClicked(object sender, RoutedEventArgs e)
    {
        if (_testingModel || _closing || _modelBusy || _gameSessionId is null || _session is not { } session) return;
        _testingModel = true;
        RefreshControls();
        try
        {
            var selected = ApiSelection();
            await session.ConfigureModelAsync(selected, ResolveApiKey(selected));
            ApiModelFeedback.Text = "当前游戏已应用模型。保存模型设置后，下次启动也会使用此配置。";
        }
        catch (Exception error) { ApiModelFeedback.Text = error.Message; }
        finally { _testingModel = false; RefreshControls(); }
    }
}
