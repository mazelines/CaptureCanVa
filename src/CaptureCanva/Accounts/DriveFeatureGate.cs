namespace CaptureCanva.Accounts;

/// <summary>
/// Single switch for the temporarily hidden Google Drive integration. All runtime paths that
/// would touch Drive (checkbox state, job creation, account-panel refresh) check this flag, so
/// a stale <c>UploadDrive=true</c> setting or an existing Drive token can never activate the
/// hidden feature. Code, uploaders and their offline checks stay intact for future re-enable.
/// To bring Drive back: set <see cref="Enabled"/> to true AND set Visibility="Visible" on the
/// Drive checkbox (MainWindow.xaml) and the Drive section (SettingsWindow.xaml).
/// </summary>
internal static class DriveFeatureGate
{
    /// <summary>false = Drive UI hidden and every Drive execution path disabled.</summary>
    internal static bool Enabled = false;
}
