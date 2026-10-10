using System.Windows;
using System.Windows.Input;
using CaptureCanva.Interop;

namespace CaptureCanva.Preferences;

/// <summary>Edits the shortcut pair; the main window owns registration and persistence.</summary>
public partial class SettingsWindow : Window
{
    public HotkeySetting? PendingRecordHotkey { get; private set; }
    public HotkeySetting? PendingPauseHotkey { get; private set; }
    private HotkeySetting? _recordHotkey;
    private HotkeySetting? _pauseHotkey;
    private bool _capturing;
    private bool _hotkeyDirty;
    private bool _applying;
    private readonly Func<HotkeySetting?, HotkeySetting?, string?> _tryApply;
    private readonly Action<bool> _setSuspended;

    public SettingsWindow(HotkeySetting? recordHotkey, HotkeySetting? pauseHotkey,
        Func<HotkeySetting?, HotkeySetting?, string?>? tryApply = null,
        Action<bool>? setSuspended = null)
    {
        InitializeComponent();
        _tryApply = tryApply ?? ((_, _) => null);
        _setSuspended = setSuspended ?? (_ => { });
        _recordHotkey = PendingRecordHotkey = recordHotkey;
        _pauseHotkey = PendingPauseHotkey = pauseHotkey;
        RefreshHotkeyBoxes();
        Closed += (_, _) => _setSuspended(false);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
