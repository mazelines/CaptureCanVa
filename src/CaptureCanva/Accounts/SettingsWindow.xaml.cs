using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CaptureCanva.Interop;

namespace CaptureCanva.Accounts;

/// <summary>
/// 환경설정 창: Google 계정 연동(YouTube/Drive)과 단축키 지정.
/// 연결된 토큰은 TokenStore(DPAPI)에 저장되고 메인 윈도우는 업로드 시 읽어간다.
/// 단축키는 적용 버튼을 눌렀을 때 HotkeySettings로 저장되고 메인 윈도우가 다시 등록한다.
/// </summary>
public partial class SettingsWindow : Window
{
    public HotkeySetting? PendingRecordHotkey { get; private set; }
    public HotkeySetting? PendingPauseHotkey { get; private set; }

    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private HotkeySetting? _recordHotkey;
    private HotkeySetting? _pauseHotkey;
    private bool _capturing;     // a hotkey box has focus and awaits the next key
    private bool _hotkeyDirty;
    /// <summary>Tries to activate a new pair on the main window; returns an error message on
    /// failure (duplicate pair, OS conflict) or null on success. The main window owns the HWND,
    /// so only it can call RegisterHotKey; on failure it must keep the OLD pair registered.</summary>
    private readonly Func<HotkeySetting?, HotkeySetting?, string?> _tryApply;
    /// <summary>Silences the main window's WM_HOTKEY handling while a capture box has focus.</summary>
    private readonly Action<bool> _setSuspended;
    private bool _applying;
    private CancellationTokenSource? _connectCancellation;
    private bool _connecting;

    public SettingsWindow(string? clientId, string? clientSecret,
        HotkeySetting? recordHotkey, HotkeySetting? pauseHotkey,
        Func<HotkeySetting?, HotkeySetting?, string?>? tryApply = null,
        Action<bool>? setSuspended = null)
    {
        InitializeComponent();
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tryApply = tryApply ?? ((_, _) => null);
        _setSuspended = setSuspended ?? (_ => { });
        _recordHotkey = recordHotkey;
        _pauseHotkey = pauseHotkey;
        PendingRecordHotkey = recordHotkey;
        PendingPauseHotkey = pauseHotkey;
        Loaded += (_, _) => { RefreshAccountUi(); RefreshHotkeyBoxes(); };
        Closed += OnWindowClosed;
    }

    // ───────────────────────── 계정 연동 ─────────────────────────

    private void RefreshAccountUi()
    {
        RefreshServiceUi(GoogleService.YouTube, YouTubeStatusText, YouTubeConnectButton, YouTubeDisconnectButton);
        // Feature gate: while Drive is hidden, its token file is never read and its panel state
        // is left at whatever XAML set (the whole DriveSection is Collapsed).
        if (DriveFeatureGate.Enabled)
            RefreshServiceUi(GoogleService.Drive, DriveStatusText, DriveConnectButton, DriveDisconnectButton);
    }

    private static void RefreshServiceUi(GoogleService service, System.Windows.Controls.TextBlock status,
        System.Windows.Controls.Button connect, System.Windows.Controls.Button disconnect)
    {
        GoogleTokens? tokens;
        try
        {
            tokens = TokenStore.Load(service);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = "토큰 파일을 읽을 수 없습니다: " + ex.Message;
            connect.Visibility = Visibility.Visible;
            connect.Content = "다시 연결";
            disconnect.Visibility = Visibility.Collapsed;
            return;
        }
        if (tokens == null)
        {
            status.Text = "연결되지 않음";
            connect.Visibility = Visibility.Visible;
            connect.Content = "연결";
            disconnect.Visibility = Visibility.Collapsed;
        }
        else
        {
            string required = service == GoogleService.YouTube ? GoogleOAuthClient.ScopeYouTube : GoogleOAuthClient.ScopeDrive;
            if (!GoogleOAuthClient.HasScope(tokens, required))
            {
                status.Text = "업로드 권한이 없습니다. 다시 연결해 주세요.";
                connect.Visibility = Visibility.Visible;
                connect.Content = "다시 연결";
                disconnect.Visibility = Visibility.Visible;
                return;
            }
            bool expired = DateTime.UtcNow >= tokens.AccessTokenExpiry;
            status.Text = "연결됨 — " + (string.IsNullOrEmpty(tokens.Email) ? "Google 계정" : tokens.Email)
                + (expired ? "  (다음 업로드 시 자동 갱신)" : "");
            connect.Visibility = Visibility.Visible;
            connect.Content = "다시 연결";
            disconnect.Visibility = Visibility.Visible;
        }
    }

    private async void OnYouTubeConnect(object sender, RoutedEventArgs e) =>
        await ConnectAsync(GoogleService.YouTube, [GoogleOAuthClient.ScopeYouTube, GoogleOAuthClient.ScopeUserInfo]);

    private async void OnDriveConnect(object sender, RoutedEventArgs e) =>
        await ConnectAsync(GoogleService.Drive, [GoogleOAuthClient.ScopeDrive, GoogleOAuthClient.ScopeUserInfo]);

    private async Task ConnectAsync(GoogleService service, string[] scopes)
    {
        // Feature gate: a hidden Drive panel's button can never start the browser/token/UI flow.
        if (service == GoogleService.Drive && !DriveFeatureGate.Enabled)
            return;
        if (_connecting)
            return; // Reentry: a second click (or the other service) must not open a second browser.
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            AccountStatusText.Text = "Google OAuth 클라이언트가 설정되지 않았습니다. 프로그램 폴더 또는 %APPDATA%\\CaptureCanva에 google-oauth-client.json (client_id 포함)을 넣은 뒤 다시 시도하세요.";
            return;
        }
        _connecting = true;
        var cancellation = new CancellationTokenSource();
        _connectCancellation = cancellation;
        var button = service == GoogleService.YouTube ? YouTubeConnectButton : DriveConnectButton;
        var otherButton = service == GoogleService.YouTube ? DriveConnectButton : YouTubeConnectButton;
        button.IsEnabled = false;
        otherButton.IsEnabled = false;
        CancelConnectButton.Visibility = Visibility.Visible;
        AccountStatusText.Text = service switch
        {
            GoogleService.YouTube => "YouTube 연결을 위해 브라우저를 열었습니다. 승인해 주세요…",
            _ => "Google Drive 연결을 위해 브라우저를 열었습니다. 승인해 주세요…",
        };
        try
        {
            using var oauth = new GoogleOAuthClient(_clientId, _clientSecret);
            var tokens = await oauth.AuthorizeAsync(scopes, cancellation.Token);
            await CompleteConnectionForTestAsync(service, oauth, tokens, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AccountStatusText.Text = $"{ServiceName(service)} 연결이 취소되었습니다.";
        }
        catch (TimeoutException ex)
        {
            AccountStatusText.Text = $"{ServiceName(service)} 연결 시간 초과: {ex.Message}";
        }
        catch (Exception ex)
        {
            AccountStatusText.Text = $"{ServiceName(service)} 연결 실패: {ex.Message}";
        }
        finally
        {
            _connectCancellation = null;
            _connecting = false;
            cancellation.Dispose();
            button.IsEnabled = true;
            otherButton.IsEnabled = true;
            CancelConnectButton.Visibility = Visibility.Collapsed;
            RefreshAccountUi();
        }
    }

    /// <summary>Connection completion phase: scope gate → optional account label → encrypted
    /// save → confirmation text. Internal so UI checks can exercise the real phase without
    /// launching the browser authorization. Throws OperationCanceledException if the label fetch
    /// is cancelled (an existing saved token is then left untouched by this method).</summary>
    internal async Task CompleteConnectionForTestAsync(
        GoogleService service, GoogleOAuthClient oauth, GoogleTokens tokens, CancellationToken cancellation)
    {
        // Scope gate: Google may grant a subset (prompt=consent re-consent flows). A token
        // without the needed scope must NEVER be presented or stored as "connected".
        string required = service == GoogleService.YouTube
            ? GoogleOAuthClient.ScopeYouTube : GoogleOAuthClient.ScopeDrive;
        if (!GoogleOAuthClient.HasScope(tokens, required))
        {
            AccountStatusText.Text = $"❌ {ServiceName(service)} 필요 권한이 승인되지 않았습니다. (승인됨: {string.Join(", ", tokens.Scopes)}) 다시 연결해 주세요.";
            return;
        }
        // The account label is optional: a valid service grant must not be lost because the
        // userinfo lookup failed. Fall back to a generic label and still persist the token.
        string labelNote = "";
        try { tokens.Email = await oauth.GetEmailAsync(tokens, cancellation); }
        catch (OperationCanceledException) { throw; } // cancellation must propagate
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            tokens.Email = "";
            labelNote = $" (계정 정보(이메일)를 가져오지 못했습니다: {ex.Message})";
        }
        TokenStore.Save(service, tokens);
        // Confirmation: never blank — a generic label stands in when the e-mail is unknown, and
        // the label-fetch note is preserved instead of being overwritten by "연결 완료".
        AccountStatusText.Text = string.IsNullOrWhiteSpace(tokens.Email)
            ? $"{ServiceName(service)} 연결 완료.{labelNote}"
            : $"{ServiceName(service)} 연결 완료: {tokens.Email}{labelNote}";
    }

    private void OnCancelConnect(object sender, RoutedEventArgs e)
    {
        _connectCancellation?.Cancel();
        AccountStatusText.Text = "연결 취소 중…";
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Closing the window cancels a pending browser consent; the main window then restores
        // its previous hotkey registration.
        _connectCancellation?.Cancel();
        _setSuspended(false);
    }

    private void OnYouTubeDisconnect(object sender, RoutedEventArgs e) => Disconnect(GoogleService.YouTube);
    private void OnDriveDisconnect(object sender, RoutedEventArgs e) => Disconnect(GoogleService.Drive);

    private void Disconnect(GoogleService service)
    {
        try
        {
            TokenStore.Delete(service);
            AccountStatusText.Text = $"{ServiceName(service)} 연결을 해제했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AccountStatusText.Text = $"❌ {ServiceName(service)} 연결 해제 실패 (파일을 삭제할 수 없습니다): {ex.Message}";
        }
        RefreshAccountUi();
    }

    private static string ServiceName(GoogleService service) =>
        service == GoogleService.YouTube ? "YouTube" : "Google Drive";

    // ───────────────────────── 단축키 ─────────────────────────

    private void RefreshHotkeyBoxes()
    {
        RecordHotkeyBox.Text = _recordHotkey?.Name ?? "없음";
        PauseHotkeyBox.Text = _pauseHotkey?.Name ?? "없음";
        _hotkeyDirty = false;
        HotkeyApplyButton.IsEnabled = false;
        HotkeyStatusText.Text = "";
    }

    private void OnHotkeyBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
        {
            _capturing = true;
            _setSuspended(true); // A stray F12 while capturing must not start a recording.
            box.Text = "키 입력…";
        }
    }

    private void OnHotkeyBoxBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box)
            return;
        _capturing = false;
        _setSuspended(false);
        // Restore the pending value; the capture only commits on a real key press.
        box.Text = ReferenceEquals(box, RecordHotkeyBox)
            ? _recordHotkey?.Name ?? "없음"
            : _pauseHotkey?.Name ?? "없음";
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing || sender is not System.Windows.Controls.TextBox box)
            return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Standalone modifier presses are not a usable hotkey on their own.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Escape)
        {
            box.Text = key == Key.Escape ? "취소" : "키 입력… (Esc는 취소)";
            if (key == Key.Escape)
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        uint modifiers = 0;
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { modifiers |= Native.MOD_CONTROL; parts.Add("Ctrl"); }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { modifiers |= Native.MOD_SHIFT; parts.Add("Shift"); }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Win32 RegisterHotKey uses MOD_ALT; WPF alt keys arrive as System keys.
            modifiers |= 0x0001; // MOD_ALT
            parts.Add("Alt");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { modifiers |= 0x0008; parts.Add("Win"); }
        parts.Add(HotkeyName(key));

        var setting = new HotkeySetting(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), string.Join("+", parts));
        if (ReferenceEquals(box, RecordHotkeyBox))
        {
            _recordHotkey = setting;
            PendingRecordHotkey = setting;
        }
        else
        {
            _pauseHotkey = setting;
            PendingPauseHotkey = setting;
        }
        box.Text = setting.Name;
        _hotkeyDirty = true;
        HotkeyApplyButton.IsEnabled = true;
        HotkeyStatusText.Text = "";
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void OnClearRecordHotkey(object sender, RoutedEventArgs e)
    {
        _recordHotkey = null;
        PendingRecordHotkey = null;
        RecordHotkeyBox.Text = "없음";
        _hotkeyDirty = true;
        HotkeyApplyButton.IsEnabled = true;
    }

    private void OnClearPauseHotkey(object sender, RoutedEventArgs e)
    {
        _pauseHotkey = null;
        PendingPauseHotkey = null;
        PauseHotkeyBox.Text = "없음";
        _hotkeyDirty = true;
        HotkeyApplyButton.IsEnabled = true;
    }

    private void OnApplyHotkeys(object sender, RoutedEventArgs e)
    {
        if (!_hotkeyDirty || _applying)
            return;

        // 1) Duplicate pair: both actions on one key makes the binding ambiguous.
        if (PendingRecordHotkey is { } rec && PendingPauseHotkey is { } pause
            && rec.Modifiers == pause.Modifiers && rec.VirtualKey == pause.VirtualKey)
        {
            HotkeyStatusText.Text = "❌ 녹화와 일시정지에 같은 키 조합을 쓸 수 없습니다.";
            return;
        }

        _applying = true;
        HotkeyApplyButton.IsEnabled = false;
        try
        {
            // 2) Real registration attempt on the main window (HWND owner). On failure the main
            //    window keeps the old pair registered and returns the OS conflict message.
            if (_tryApply(PendingRecordHotkey, PendingPauseHotkey) is { } error)
            {
                HotkeyStatusText.Text = "❌ " + error;
                // The stored/UI pending values stay as the user set them; they can retry.
                return;
            }

            // The main window commits storage and registration together, rolling back failures.

            _recordHotkey = PendingRecordHotkey;
            _pauseHotkey = PendingPauseHotkey;
            _hotkeyDirty = false;
            HotkeyStatusText.Text = "적용되었습니다.";
        }
        finally
        {
            _applying = false;
            HotkeyApplyButton.IsEnabled = _hotkeyDirty;
        }
    }

    /// <summary>Best-effort friendly name for a WPF key (F-keys, digits, letters, others).</summary>
    private static string HotkeyName(Key key) => key switch
    {
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + ((int)key - (int)Key.NumPad0),
        Key.Oem3 => "`",
        _ => key.ToString(),
    };
}
