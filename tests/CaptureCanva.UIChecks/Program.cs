using System.IO;
using System.Net.Http;
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

            // ---- missing OAuth client UI: no client file anywhere -> setup message ----
            TokenStore.Delete(GoogleService.YouTube); // simulate signed-out state for this group
            typeof(GoogleClientConfig).GetProperty("CandidatePathOverride",BindingFlags.Static|BindingFlags.NonPublic)!
                .SetValue(null,new[]{Path.Combine(isolated,"no-such-client.json")});
            typeof(GoogleClientConfig).GetMethod("ResetForTests",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);
            var disconnected=new SettingsWindow(null,null,null,null);
            Call(disconnected,"RefreshAccountUi");
            Check(Control<TextBlock>(disconnected,"YouTubeStatusText").Text.Contains("연결되지 않음"),"disconnected YouTube panel reports not connected");
            Call(disconnected,"OnYouTubeConnect",new object?[]{ disconnected, new RoutedEventArgs() });
            Check(Control<TextBlock>(disconnected,"AccountStatusText").Text.Contains("클라이언트가 설정되지 않았습니다"),"missing client shows setup guidance instead of opening a browser");
            // YouTube stays unusable in the main window while no client exists.
            Call(main,"RefreshUploadUi");
            Check(!Control<CheckBox>(main,"UploadYouTubeCheck").IsEnabled,"YouTube upload stays disabled without an OAuth client");
            typeof(GoogleClientConfig).GetProperty("CandidatePathOverride",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);
            typeof(GoogleClientConfig).GetMethod("ResetForTests",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);

            // ---- stale availability after grant loss during upload (reproduced UI bug) ----
            // Re-grant YouTube, re-enable the UI, then narrow the saved token mid-flow and run
            // the real UploadRecordingAsync. The scope refusal must happen before any HTTP and
            // the post-upload refresh must leave the checkbox disabled and unchecked.
            typeof(GoogleClientConfig).GetProperty("CandidatePathOverride",BindingFlags.Static|BindingFlags.NonPublic)!
                .SetValue(null,new[]{Path.Combine(isolated,"client.json")});
            typeof(GoogleClientConfig).GetMethod("ResetForTests",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);
            var granted=new GoogleTokens { AccessToken="ok",RefreshToken="r",AccessTokenExpiry=DateTime.UtcNow.AddHours(1),Scopes=[GoogleOAuthClient.ScopeYouTube] };
            TokenStore.Save(GoogleService.YouTube,granted);
            Call(main,"RefreshUploadUi");
            Check(Control<CheckBox>(main,"UploadYouTubeCheck").IsEnabled,"precondition: YouTube available while grant is valid");
            Control<CheckBox>(main,"UploadYouTubeCheck").IsChecked=true;
            // Narrow the SAVED grant exactly like a mid-flight refresh would, and PROVE the stored
            // token lost the upload scope BEFORE the app method runs. Without this the upload
            // would reach the network with fake credentials (fixture defect, now fixed).
            var narrowed=new GoogleTokens { AccessToken="ok",RefreshToken="r",AccessTokenExpiry=DateTime.UtcNow.AddHours(1),Scopes=["openid","email"] };
            TokenStore.Save(GoogleService.YouTube,narrowed);
            var storedNow=TokenStore.Load(GoogleService.YouTube);
            Check(storedNow!=null && !GoogleOAuthClient.HasScope(storedNow,GoogleOAuthClient.ScopeYouTube),
                "precondition proved: saved token lacks youtube.upload before app invocation");
            string clipPath=Path.Combine(isolated,"clip.mp4");
            File.WriteAllBytes(clipPath,[1,2,3,4]);
            string savedStatus="2026-10-10 12:00:00 - clip.mp4 저장 완료";
            Control<TextBlock>(main,"StatusText").Text=savedStatus;
            // UploadRecordingAsync touches UI directly; start it on the window's Dispatcher like
            // the production async-void handler does, then pump frames with a hard 10s bound so a
            // broken fixture can never hang the suite again.
            var uploadTask=(Task)main.Dispatcher.Invoke(new Func<Task>(()=>(Task)Call(main,"UploadRecordingAsync",clipPath,savedStatus)!))!;
            var pumpDeadline=DateTime.UtcNow.AddSeconds(10);
            while (!uploadTask.IsCompleted && DateTime.UtcNow<pumpDeadline)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(delegate { }));
                uploadTask.Wait(50);
            }
            Check(uploadTask.IsCompleted,"upload method completes within the 10s dispatcher-pump bound");
            uploadTask.GetAwaiter().GetResult();
            // The saved token now carries only openid/email: the upload must have been refused
            // before any Google request and availability must be re-evaluated by the app itself.
            Check(!Control<CheckBox>(main,"UploadYouTubeCheck").IsEnabled,"checkbox disabled after grant loss during upload");
            Check(Control<CheckBox>(main,"UploadYouTubeCheck").IsChecked!=true,"checkbox unchecked after grant loss during upload");
            Check(Control<TextBlock>(main,"StatusText").Text.Contains("업로드 실패"),"upload refusal is reported against the service");
            Check(Control<TextBlock>(main,"StatusText").Text.Contains(savedStatus),"local saved-file status is retained");
            Check(typeof(MainWindow).GetField("_uploadCancellation",Private)!.GetValue(main)==null,"no cancellation state leaks after refusal");
            Check(Control<FrameworkElement>(main,"UploadCancelPanel").Visibility==Visibility.Collapsed,"busy panel cleared after refusal");

            // ---- completion phase (real production helper) with userinfo HTTP error ----
            // The settings dialog must still save a valid YouTube grant when the account-label
            // lookup fails, and report connected with a generic label.
            var dialog2=new SettingsWindow("offline-test-only",null,null,null);
            var savedBefore=TokenStore.Load(GoogleService.YouTube);
            string? savedEmailBefore=savedBefore?.Email;
            var badUserinfo=new HttpFake(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            { Content=new StringContent("userinfo down",System.Text.Encoding.UTF8,"text/plain") });
            using var completionOauth=new GoogleOAuthClient("offline-test-only",handler:badUserinfo);
            var grant=new GoogleTokens { AccessToken="tk",RefreshToken="rf",AccessTokenExpiry=DateTime.UtcNow.AddHours(1),Scopes=[GoogleOAuthClient.ScopeYouTube] };
            var completionTask=dialog2.Dispatcher.Invoke(new Func<Task>(()=>(Task)Call(dialog2,"CompleteConnectionForTestAsync",GoogleService.YouTube,completionOauth,grant,CancellationToken.None)!))!;
            var completionDeadline=DateTime.UtcNow.AddSeconds(10);
            while (!completionTask.IsCompleted && DateTime.UtcNow<completionDeadline)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(delegate { }));
                completionTask.Wait(50);
            }
            Check(completionTask.IsCompleted,"completion phase finishes within the 10s bound");
            completionTask.GetAwaiter().GetResult();
            var savedAfter=TokenStore.Load(GoogleService.YouTube);
            Check(savedAfter!=null && GoogleOAuthClient.HasScope(savedAfter,GoogleOAuthClient.ScopeYouTube)
                && savedAfter.AccessToken=="tk","valid YouTube grant is persisted despite userinfo failure");
            Check(Control<TextBlock>(dialog2,"AccountStatusText").Text.Contains("연결 완료"),"dialog reports connected");
            Check(Control<TextBlock>(dialog2,"AccountStatusText").Text.Contains("가져오지 못했습니다"),"label-fetch failure note is preserved in confirmation text");
            // Cancellation during the label fetch must propagate and preserve the existing token.
            var cancelCts2=new CancellationTokenSource(); cancelCts2.Cancel();
            var hungOauth=new GoogleOAuthClient("offline-test-only",handler:new HungHandler());
            try
            {
                var cancelled=dialog2.Dispatcher.Invoke(new Func<Task>(()=>(Task)Call(dialog2,"CompleteConnectionForTestAsync",GoogleService.YouTube,hungOauth,grant,cancelCts2.Token)!))!;
                var cd2=DateTime.UtcNow.AddSeconds(10);
                while (!cancelled.IsCompleted && DateTime.UtcNow<cd2)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        System.Windows.Threading.DispatcherPriority.Background,new Action(delegate { }));
                    cancelled.Wait(50);
                }
                bool propagated=false;
                try { cancelled.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { propagated=true; }
                var preserved=TokenStore.Load(GoogleService.YouTube);
                Check(propagated && preserved!=null && preserved.AccessToken=="tk",
                    "label-fetch cancellation propagates and keeps the previously saved grant");
            }
            catch (TaskCanceledException)
            {
                // SetCanceled surfaces as TaskCanceledException (subclass of OCE) — same contract.
                var preserved=TokenStore.Load(GoogleService.YouTube);
                Check(preserved!=null && preserved.AccessToken=="tk",
                    "label-fetch cancellation propagates and keeps the previously saved grant");
            }

            Console.WriteLine($"Drive hidden checks passed: {passed}. No recording or network.");
            return 0;
        }
        finally{Directory.Delete(isolated,true);}
    }
}

/// <summary>Local fake handler for completion-phase tests (userinfo errors, hung calls).</summary>
internal sealed class HttpFake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

/// <summary>Handler whose HTTP calls never complete (simulates a hung userinfo endpoint).</summary>
internal sealed class HungHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}