using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CaptureCanva;
using CaptureCanva.Accounts;
internal static class Program
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int passed;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label); passed++; Console.WriteLine("PASS "+label);}
    static object? Call(object target,string method,params object?[] args)=>target.GetType().GetMethod(method,Private)!.Invoke(target,args);
    static T Control<T>(FrameworkElement window,string name)=>(T)window.FindName(name);
    [STAThread] static int Main()
    {
        string isolated=Path.Combine(Path.GetTempPath(),"CaptureCanva-drive-hidden-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        string config=Path.Combine(isolated,"client.json");
        File.WriteAllText(config,"{\"client_id\":\"offline-test-only\"}");
        typeof(TokenStore).GetProperty("DirectoryOverride",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,isolated);
        typeof(HotkeySettings).GetProperty("DirectoryOverride",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,isolated);
        typeof(GoogleClientConfig).GetProperty("CandidatePathOverride",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,new[]{config});
        var storePath=(string)typeof(TokenStore).GetMethod("FilePath",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{GoogleService.Drive})!;
        Check(Path.GetFullPath(storePath).StartsWith(Path.GetFullPath(isolated)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"effective Drive token path is isolated before write");
        var settingsOverride=typeof(AppSettings).GetProperty("DirectoryOverride",BindingFlags.Static|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppSettings isolation hook must exist before MainWindow is constructed.");
        settingsOverride.SetValue(null,isolated);
        string settingsPath=(string)typeof(AppSettings).GetProperty("FilePath",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        Check(Path.GetFullPath(settingsPath).StartsWith(Path.GetFullPath(isolated)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"app settings load path is isolated before UI construction");
        var app=new CaptureCanva.App(); app.InitializeComponent();
        var main=new MainWindow(); var dialog=new SettingsWindow("offline-test-only",null,null,null);
        var settings=(AppSettings)typeof(MainWindow).GetField("_settings",Private)!.GetValue(main)!;
        settings.UploadDrive=true;
        settings.Mode=CaptureCanva.Capture.CaptureMode.Monitor;
        var driveTokens=new GoogleTokens { AccessToken="fake",RefreshToken="fake",Scopes=[GoogleOAuthClient.ScopeDrive],AccessTokenExpiry=DateTime.UtcNow.AddHours(1) };
        TokenStore.Save(GoogleService.Drive,driveTokens);
        var bytes=File.ReadAllBytes(storePath);
        try
        {
            using(var locked=new FileStream(storePath,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                Call(main,"ApplySettingsToControls");
                Check(Control<CheckBox>(main,"UploadDriveCheck").Visibility==Visibility.Collapsed,"Drive upload checkbox stays hidden");
                Check(Control<CheckBox>(main,"UploadDriveCheck").IsChecked!=true,"old UploadDrive=true does not check hidden UI");
                Check(!Control<CheckBox>(main,"UploadDriveCheck").IsEnabled,"hidden Drive checkbox is disabled");
                Check(!Control<TextBlock>(main,"StatusText").Text.Contains("Drive"),"locked dormant token does not create main UI errors");
                Call(dialog,"RefreshAccountUi");
                Check(Control<StackPanel>(dialog,"DriveSection").Visibility==Visibility.Collapsed,"Drive account section stays hidden");
                Check(!Control<TextBlock>(dialog,"DriveStatusText").Text.Contains("파일"),"account refresh skips locked dormant token");
                Call(main,"SetRecordingUi",true); Call(main,"SetRecordingUi",false);
                Check(!Control<CheckBox>(main,"UploadDriveCheck").IsEnabled,"recording state transitions do not enable hidden Drive");
                Control<CheckBox>(main,"UploadYouTubeCheck").IsChecked=false;
                Control<CheckBox>(main,"UploadDriveCheck").IsChecked=true;
                Control<TextBlock>(main,"StatusText").Text="local files preserved";
                var upload=(Task)Call(main,"UploadRecordingAsync",Path.Combine(isolated,"missing.mp4"),"local files preserved")!;
                upload.GetAwaiter().GetResult();
                Check(Control<TextBlock>(main,"StatusText").Text=="local files preserved","forced stale Drive selection does not create an upload job");
                Check(typeof(MainWindow).GetField("_uploadCancellation",Private)!.GetValue(main)==null,"disabled upload leaves no cancellation state");
                Check(Control<FrameworkElement>(main,"UploadCancelPanel").Visibility==Visibility.Collapsed,"disabled upload leaves no busy panel");
            }
            Check(File.ReadAllBytes(storePath).SequenceEqual(bytes),"dormant Drive token bytes are preserved");
            var youtube=new GoogleTokens { AccessToken="fake",RefreshToken="fake",Scopes=[GoogleOAuthClient.ScopeYouTube],AccessTokenExpiry=DateTime.UtcNow.AddHours(1) };
            TokenStore.Save(GoogleService.YouTube,youtube);
            Call(main,"RefreshUploadUi"); Call(dialog,"RefreshAccountUi");
            Check(Control<CheckBox>(main,"UploadYouTubeCheck").IsEnabled,"YouTube with required scope remains available");
            Check(Control<TextBlock>(dialog,"YouTubeStatusText").Text.Contains("연결됨"),"YouTube account status remains functional");
            Check(!Control<CheckBox>(main,"UploadDriveCheck").IsEnabled && Control<CheckBox>(main,"UploadDriveCheck").IsChecked!=true,"YouTube refresh still clears and disables dormant Drive");
            Console.WriteLine($"Drive hidden checks passed: {passed}. No recording or network.");
            return 0;
        }
        finally{Directory.Delete(isolated,true);}
    }
}