param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Samples,
    [Parameter(Mandatory)][string]$SavePath,
    [Parameter(Mandatory)][string]$GamePath
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$archiveFile = (Resolve-Path -LiteralPath $Archive).Path
$stamp = "插件 绿色验收 " + [guid]::NewGuid().ToString('N').Substring(0, 8)
$extracted = Join-Path $repo ".test-runs/$stamp"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public static class PluginAcceptanceWindow
{
    private delegate bool WindowCallback(IntPtr window, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr unused);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr wParam, string text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint current, uint other, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static IntPtr[] OwnedWindows(uint expectedPid)
    {
        var result = new List<IntPtr>();
        EnumWindows((window, unused) => {
            if (GetWindowThreadProcessId(window, out var pid) != 0 && pid == expectedPid) result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    public static IntPtr FindDialog(uint expectedPid, string title)
    {
        foreach (var window in OwnedWindows(expectedPid)) {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            if (text.ToString() == title) return window;
        }
        return IntPtr.Zero;
    }

    public static bool SelectFile(IntPtr dialog, string path)
    {
        IntPtr combo = GetDlgItem(dialog, 1148);
        IntPtr edit = IntPtr.Zero;
        EnumChildWindows(combo == IntPtr.Zero ? dialog : combo, (child, unused) => {
            var name = new StringBuilder(32);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "Edit") return true;
            edit = child;
            return false;
        }, IntPtr.Zero);
        var open = GetDlgItem(dialog, 1);
        return edit != IntPtr.Zero && open != IntPtr.Zero
            && SendMessageTimeout(edit, 0x000C, IntPtr.Zero, path, 2, 5000, out _) != IntPtr.Zero
            && PostMessage(dialog, 0x0111, (IntPtr)1, open);
    }

    public static bool Focus(IntPtr window, uint expectedPid)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid != expectedPid) return false;
        if (GetForegroundWindow() == window) return true;
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); return GetForegroundWindow() == window; }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
}
'@
$ui = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
$property = [System.Windows.Automation.PropertyCondition]
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(12)
$game = $null
$mainHwnd = [IntPtr]::Zero

function Wait-Check([scriptblock]$Check, [string]$Failure, [int]$Seconds = 20) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $detail = ''
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        try { if (& $Check) { return } } catch { $detail = $_.Exception.Message }
        Start-Sleep -Milliseconds 125
    }
    throw "$Failure $detail"
}
function Get-DesktopWindows {
    foreach ($window in [PluginAcceptanceWindow]::OwnedWindows([uint32]$app.Id)) {
        try { $ui::FromHandle($window) } catch {}
    }
}
function Find-Ui([string]$Value, [switch]$ByName, [System.Windows.Automation.AutomationElement]$From) {
    $which = if ($ByName) { $ui::NameProperty } else { $ui::AutomationIdProperty }
    $condition = $property::new($which, $Value)
    if ($From) { return $From.FindFirst($scope::Descendants, $condition) }
    foreach ($top in (Get-DesktopWindows)) {
        if ($match = $top.FindFirst($scope::Descendants, $condition)) { return $match }
    }
}
function Select-Tab([string]$Name) {
    Wait-Check { Find-Ui $Name } "Missing tab $Name."
    (Find-Ui $Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Click([string]$Name, [System.Windows.Automation.AutomationElement]$From) {
    Wait-Check { (Find-Ui $Name -From $From).Current.IsEnabled } "Cannot invoke $Name."
    (Find-Ui $Name -From $From).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Value([string]$Name, [string]$Value) {
    (Find-Ui $Name).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Install-Sample([string]$Zip) {
    Click 'InstallPlugin'
    $script:picker = $null
    try {
        Wait-Check {
            $script:picker = Get-DesktopWindows | ForEach-Object {
                if ($_.Current.ClassName -eq '#32770') { $_ }
                else { $_.FindFirst($scope::Descendants, $property::new($ui::ClassNameProperty, '#32770')) }
            } | Where-Object { $_ -and $_.Current.NativeWindowHandle -ne $mainHwnd.ToInt64() } | Select-Object -First 1
            $script:picker
        } 'Missing ZIP file picker.'
    } catch {
        Get-DesktopWindows | ForEach-Object { Write-Host "Window: $($_.Current.Name) [$($_.Current.ClassName)]" }
        throw
    }
    if (![PluginAcceptanceWindow]::SelectFile([IntPtr]$picker.Current.NativeWindowHandle, (Resolve-Path -LiteralPath $Zip).Path)) {
        throw 'Could not enter package path in the isolated file picker.'
    }
    Wait-Check {
        $script:confirmation = [PluginAcceptanceWindow]::FindDialog([uint32]$app.Id, '确认安装本地插件')
        $script:confirmation -ne [IntPtr]::Zero
    } 'Missing install confirmation.'
    $dialog = $ui::FromHandle($confirmation)
    Click 'ConfirmButton' $dialog
}
function Activate-Plugin([string]$Name, [string]$State) {
    Wait-Check { Find-Ui $Name -ByName } "Missing plugin $Name."
    $element = Find-Ui $Name -ByName
    $selection = $null
    while ($element -and !$element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $element = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($element)
    }
    if (!$selection) { throw "Plugin has no selectable list row: $Name" }
    $selection.Select()
    Click 'TogglePluginRun'
    Wait-Check { (Find-Ui 'PluginStatusText').Current.Name -eq $State } "Plugin did not reach $State."
}
function Lifecycle([string]$Action, [string]$Label) {
    $request = Join-Path $evidence 'lifecycle-request.json'
    @{ action = $Action; label = $Label } | ConvertTo-Json -Compress | Set-Content -LiteralPath "$request.tmp" -Encoding utf8NoBOM
    Move-Item -LiteralPath "$request.tmp" -Destination $request
    $path = Join-Path $evidence "lifecycle-$Label.json"
    Wait-Check { Test-Path -LiteralPath $path } "Missing isolated game response for $Action." 45
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($result.error -or $result.gamePid -ne $game.Id) { throw "Isolated game failed $Action. $($result.error)" }
    $result
}
function Read-Lines([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Get-Content -LiteralPath $Path -Encoding UTF8 | ForEach-Object {
            try { $_ | ConvertFrom-Json -ErrorAction Stop } catch {}
        }
    }
}
function Get-SettingsResult([string]$Intent, [string]$Text = '') {
    $page = $http.GetStringAsync($settings).GetAwaiter().GetResult()
    $formToken = [regex]::Match($page, 'name="csrf" value="([a-f0-9]+)"').Groups[1].Value
    if (!$formToken) { throw 'Missing current sample settings session.' }
    $fields = [Collections.Generic.Dictionary[string,string]]::new()
    $fields['csrf'] = $formToken
    $fields['intent'] = $Intent
    $fields['text'] = $Text
    $content = [Net.Http.FormUrlEncodedContent]::new($fields)
    $response = $http.PostAsync($settings, $content).GetAwaiter().GetResult()
    try {
        [void]$response.EnsureSuccessStatusCode()
        $resultHtml = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        [Net.WebUtility]::HtmlDecode([regex]::Match($resultHtml, '(?s)<pre[^>]*>(.*?)</pre>').Groups[1].Value)
    } finally { $response.Dispose(); $content.Dispose() }
}
function Reply([string]$Text) {
    $rows = @(Read-Lines $trace)
    $submission = $rows | Where-Object { $_.type -eq 'submitted' -and $_.payload.text -eq $Text } | Select-Object -Last 1
    if ($submission) { $rows | Where-Object { $_.type -eq 'life.chat.reply' -and $_.payload.requestId -eq $submission.payload.requestId } }
}
function Request-DesktopExit {
    if (!$app.HasExited -and $mainHwnd) {
        [void][PluginAcceptanceWindow]::PostMessage($mainHwnd, 0x10, 0, 0)
        Click 'ExitButton'
        if (!$app.WaitForExit(22000)) { throw 'Desktop did not exit.' }
    }
}

[IO.Compression.ZipFile]::ExtractToDirectory($archiveFile, $extracted)
$run = & (Join-Path $repo 'tests/acceptance/start-desktop-acceptance.ps1') -ReleaseDir $extracted -SavePath $SavePath -GamePath $GamePath -Simulated
$desktop = Join-Path $run.RunDirectory 'desktop'
$evidence = Join-Path $run.RunDirectory 'evidence'
$trace = Join-Path $evidence 'agent-runtime-events.jsonl'
Write-Host "Evidence: $evidence"
$app = Get-Process -Id $run.DesktopProcessId

try {
    Wait-Check { $app.Refresh(); $app.MainWindowHandle -ne 0 } 'Packaged WPF did not start.' 30
    $mainHwnd = $app.MainWindowHandle
    Wait-Check {
        $initialTab = Find-Ui 'CommandPage'
        $initialTab -and $initialTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
    } 'Packaged WPF did not open on Today.'
    Select-Tab 'PluginsPage'
    if (Find-Ui 'BrowsePluginCatalog') {
        Click 'BrowsePluginCatalog'
        Wait-Check {
            $script:catalogWindow = [PluginAcceptanceWindow]::FindDialog([uint32]$app.Id, '在线插件目录')
            $script:catalogWindow -ne [IntPtr]::Zero -and (Find-Ui 'CatalogUrl')
        } 'Online catalog dialog did not render.'
        [void][PluginAcceptanceWindow]::PostMessage($catalogWindow, 0x10, 0, 0)
    }
    Install-Sample (Join-Path $Samples 'command-0.1.0-win-x64.zip')
    Activate-Plugin '指令示例' '待配置'
    $sampleSettingsLog = Join-Path $desktop 'data/logs/plugins/org.example.command/settings.log'
    Wait-Check { (Test-Path -LiteralPath $sampleSettingsLog) -and (Get-Content -Raw -LiteralPath $sampleSettingsLog) } 'Command settings page did not register.'
    $settings = [regex]::Match((Get-Content -Raw -LiteralPath $sampleSettingsLog), 'http://127\.0\.0\.1:\d+/settings').Value
    if (!$settings -or (Get-SettingsResult 'save' '走到出口附近') -notlike '*已保存*') { throw 'Sample settings did not save.' }
    Install-Sample (Join-Path $Samples 'events-0.1.0-win-x64.zip')
    Activate-Plugin '事件订阅示例' '已就绪'
    $eventLog = Join-Path $desktop 'data/logs/plugins/org.example.events/events.jsonl'
    Wait-Check { Read-Lines $eventLog | Where-Object type -eq 'snapshot' } 'Event plugin cannot run before the game.'
    $eventData = Join-Path $desktop 'data/plugins/org.example.events/keep.txt'
    Set-Content -LiteralPath $eventData -Value 'test data'
    Click 'UninstallPlugin'
    Wait-Check {
        $script:uninstallWindow = [PluginAcceptanceWindow]::FindDialog([uint32]$app.Id, '卸载插件')
        $script:uninstallWindow -ne [IntPtr]::Zero
    } 'Missing themed uninstall dialog.'
    $uninstall = $ui::FromHandle($uninstallWindow)
    $keepOption = (Find-Ui 'Option' -From $uninstall).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($keepOption.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw 'Keep plugin data should be on by default.' }
    Click 'CancelCloseButton' $uninstall
    Wait-Check { (Find-Ui 'PluginStatusText').Current.Name -eq '已就绪' } 'Cancelling uninstall stopped the running plugin.'
    Click 'UninstallPlugin'
    Wait-Check {
        $script:uninstallWindow = [PluginAcceptanceWindow]::FindDialog([uint32]$app.Id, '卸载插件')
        $script:uninstallWindow -ne [IntPtr]::Zero
    } 'Missing themed uninstall dialog.'
    Click 'ConfirmButton' ($ui::FromHandle($uninstallWindow))
    Wait-Check { !(Find-Ui '事件订阅示例' -ByName) } 'Confirmed uninstall did not remove plugin.'
    if (!(Test-Path -LiteralPath $eventData)) { throw 'Uninstall did not preserve plugin data.' }
    Install-Sample (Join-Path $Samples 'events-0.1.0-win-x64.zip')
    Activate-Plugin '事件订阅示例' '已就绪'

    Select-Tab 'SettingsPage'
    Select-Tab 'ModelSettingsTab'
    Set-Value 'ApiKey' 'accept-usage'
    Click 'TestModelButton'
    Wait-Check { (Find-Ui 'ApiModelFeedback').Current.Name -eq '模型连接和工具调用均已通过。' } 'Mock model tool check failed.' 45
    Click 'LaunchHeader'
    Wait-Check {
        $candidate = Get-CimInstance Win32_Process -Filter "Name='StardewModdingAPI.exe' AND ParentProcessId=$($app.Id)"
        if ($candidate) { $script:game = Get-Process -Id $candidate.ProcessId; $script:game }
    } 'Desktop did not start its own isolated game.' 35
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        if ((Lifecycle 'snapshot' "title-$attempt").menu -eq 'StardewValley.Menus.TitleMenu') { break }
        Start-Sleep -Milliseconds 500
    }
    [void](Lifecycle 'load' 'initial-load')
    Select-Tab 'CommandPage'
    Wait-Check { (Find-Ui 'SendButton').Current.IsEnabled } 'Desktop did not bind to the isolated save.' 55
    Wait-Check {
        Read-Lines $eventLog | Where-Object { $_.type -eq 'snapshot' -and $_.payload.game.connected }
    } 'Subscriber did not refresh its snapshot after save load.' 20

    Click 'AuthorityButton'
    Wait-Check { (Find-Ui 'AuthorityButton').Current.Name -like '玩家接管*' } 'Could not hand over control.'
    if ((Get-SettingsResult 'submit') -notmatch '"requestId"') { throw 'Plugin instruction was not accepted.' }
    Wait-Check {
        $script:pluginResult = Get-SettingsResult 'query' | ConvertFrom-Json
        $script:pluginResult.turnEnded -and ($script:pluginResult.actions | Where-Object {
            $_.skillId -eq 'navigate-to' -and $_.terminalState -eq 'succeeded'
        })
    } 'Plugin did not produce a successful native movement receipt.' 70
    Wait-Check {
        $actionId = $pluginResult.actions[0].actionId
        Read-Lines $eventLog | Where-Object { $_.type -eq 'agent.action.completed' -and $_.payload.actionId -eq $actionId }
    } 'Second plugin did not observe the same native receipt.'
    $origin = Read-Lines $trace | Where-Object { $_.type -eq 'submitted' -and $_.payload.pluginSource -eq 'org.example.command' } | Select-Object -Last 1
    if (!$origin -or !(Find-Ui '插件 · org.example.command' -ByName)) { throw 'Desktop did not label the plugin source.' }
    Wait-Check {
        Read-Lines $eventLog | Where-Object { $_.type -eq 'agent.reply.completed' -and $_.payload.messageId -eq $origin.payload.requestId }
    } 'Subscriber did not receive the completed public reply.'
    $firstId = $pluginResult.requestId
    $same = Get-SettingsResult 'retry' | ConvertFrom-Json
    if ($same.requestId -ne $firstId -or $same.actions[0].actionId -ne $pluginResult.actions[0].actionId) { throw 'Retry did not retain the original receipt.' }

    Set-Value 'Input' '今天的普通聊天'
    Click 'SendButton'
    Wait-Check { (Reply '今天的普通聊天').payload.replyText } 'Normal conversation did not finish.' 45
    if (@(Reply '今天的普通聊天').Count -ne 1) { throw 'Ordinary conversation produced duplicates.' }
    Set-Value 'Input' '等待接管'
    Click 'SendButton'
    Wait-Check { (Find-Ui 'ChatStatus').Current.Name -like '*规划或回复*' } 'Could not enter planning.'
    $game.Refresh()
    $gameShell = New-Object -ComObject WScript.Shell
    Wait-Check {
        $game.Refresh()
        if ([PluginAcceptanceWindow]::Focus($game.MainWindowHandle, [uint32]$game.Id)) { return $true }
        [void]$gameShell.AppActivate($game.Id)
        [PluginAcceptanceWindow]::Focus($game.MainWindowHandle, [uint32]$game.Id)
    } 'Could not focus the isolated game.'
    [PluginAcceptanceWindow]::keybd_event(0x78, 0x43, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 140
    [PluginAcceptanceWindow]::keybd_event(0x78, 0x43, 2, [UIntPtr]::Zero)
    Wait-Check { (Find-Ui 'AuthorityButton').Current.Name -eq '交给 AI' } 'F9 did not restore player control.'
    Wait-Check { (Reply '等待接管').payload.error -eq '本轮已停止。' } 'F9 did not settle planning.'

    Click 'AuthorityButton'
    Wait-Check { (Find-Ui 'AuthorityButton').Current.Name -like '玩家接管*' } 'Could not hand over control after F9.'
    Set-Value 'Input' '请等待接管，先观察农场'
    Click 'SendButton'
    Wait-Check { (Find-Ui 'ChatStatus').Current.Name -like '*规划或回复*' } 'Replacement fixture did not enter planning.'
    Set-Value 'Input' '改派为检查当前位置'
    Click 'InterruptButton'
    Wait-Check { (Reply '改派为检查当前位置').payload.replyText } 'Replacement did not finish.' 45
    if ((Reply '请等待接管，先观察农场').payload.error -ne '本轮已停止。') { throw 'Replaced request was not settled.' }
    Set-Value 'Input' '记住今日目标'
    Click 'SendButton'
    Wait-Check { (Reply '记住今日目标').payload.replyText } 'Player goal was not recorded.' 45
    Wait-Check {
        Read-Lines $trace | Where-Object { $_.type -eq 'life.chat.reply' -and $_.payload.requestId -like 'autonomy-*' -and $_.payload.replyText }
    } 'Existing player goal did not continue automatically.' 45

    [void](Lifecycle 'return-title' 'return-title')
    Wait-Check { !(Test-Path -LiteralPath (Join-Path $desktop 'Mods/StardewAI.Companion.Mod/data/transport-discovery.json')) } 'Old game session did not close.'
    $previous = Get-SettingsResult 'query' | ConvertFrom-Json
    if ($previous.requestId -ne $firstId -or !$previous.turnEnded) { throw 'Could not query the old session receipt.' }
    [void](Lifecycle 'reload' 'reload')
    Wait-Check { (Find-Ui 'SendButton').Current.IsEnabled } 'Reloaded save did not bind.' 55
    if ((Find-Ui 'AuthorityButton').Current.Name -ne '交给 AI') { throw 'Reload gave control back to Agent.' }

    $ownedPlugins = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id)" |
        Where-Object { $_.Name -in 'CommandSample.exe','EventSubscriber.exe' } |
        ForEach-Object { Get-Process -Id $_.ProcessId })
    if ($ownedPlugins.Count -ne 2) { throw 'Both plugins must run in the packaged desktop process.' }
    Request-DesktopExit
    Wait-Check { @($ownedPlugins | Where-Object {!$_.HasExited}).Count -eq 0 } 'A managed plugin survived desktop exit.'
    if ($game.HasExited -or !$game.Responding) { throw 'Desktop exit must leave the player game available.' }
    [void](Lifecycle 'save-exit' 'save-exit')
    if (!$game.WaitForExit(15000)) { throw 'Isolated game did not exit.' }

    @{
        result = 'pass'; mode = 'local simulated model API'; archiveSha256 = (Get-FileHash -LiteralPath $archiveFile).Hash.ToLowerInvariant()
        samples = @('org.example.command@0.1.0', 'org.example.events@0.1.0')
        nativeReceipt = $pluginResult.actions[0]; queryAfterSaveExit = $true
        duplicateCount = @((Reply '今天的普通聊天')).Count; f9 = 'human'; ownedPluginsStopped = $true
        replacement = $true; playerGoalContinued = $true
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'plugin-acceptance.json') -Encoding utf8NoBOM
    Write-Output 'Packaged desktop, two installed plugins, simulated model, native action, SSE, idempotence, ordinary chat, F9, replacement, goal continuation, reload and shutdown passed.'
} catch {
    Write-Warning $_.ScriptStackTrace
    throw
} finally {
    try {
        if (!$app.HasExited -and $picker -and $picker.Current.NativeWindowHandle) {
            [void][PluginAcceptanceWindow]::PostMessage([IntPtr]$picker.Current.NativeWindowHandle, 0x10, 0, 0)
        }
    } catch {}
    try { Request-DesktopExit } catch { Write-Warning $_ }
    if (!$app.HasExited -and $app.Path -eq (Join-Path $desktop '星露谷游戏伴侣.exe')) {
        $app.Kill()
        [void]$app.WaitForExit(3000)
    }
    if ($game -and !$game.HasExited) {
        [void]$game.CloseMainWindow()
        [void]$game.WaitForExit(8000)
    }
    $rootHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($desktop.TrimEnd('\') + '\').ToUpperInvariant())))
    & cmdkey.exe "/delete:StardewGameCompanion/$rootHash/deepseek" | Out-Null
    $http.Dispose()
}
