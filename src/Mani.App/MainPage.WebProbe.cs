using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Mani_App;

public sealed partial class MainPage
{
    private Task _webCommandTask = Task.CompletedTask;
    private bool _webCheckAcceptsCommands;
    private int _webGeneration;
    private string _webVersion = "";
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? _webMessageHandler;

    private async void WebProbe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        if (_web is not null)
        {
            CloseWebProbe();
            await _webCommandTask;
            LabResults.Text = "WebView2 closed and detached.";
        }
        else
        {
            await OpenWebProbeAsync(cancellation);
            LabResults.Text = "Use Pause / Play in the web overlay; type in its field, resize, change DPI, then close it.";
        }
    });

    private async Task OpenWebProbeAsync(CancellationToken cancellation)
    {
        CloseWebProbe();
        await _webCommandTask;
        var web = new WebView2();
        _web = web;
        int generation = _webGeneration;
        WebProbeContainer.Children.Add(web);
        WebProbePanel.Visibility = Visibility.Visible;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "WebView2"),
                new CoreWebView2EnvironmentOptions { Language = "en-US" }).AsTask(token);
            await web.EnsureCoreWebView2Async(environment).AsTask(token);
            _webVersion = environment.BrowserVersionString;
            Trace("web-initialized", new { _webVersion });
            token.ThrowIfCancellationRequested();
            if (_closing) throw new OperationCanceledException(token);
            var core = web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var navigationPolicy = new WebProbeNavigation();
            core.NavigationStarting += (_, args) =>
            {
                bool allowed = generation == _webGeneration && navigationPolicy.Starting(args.Uri, args.NavigationId);
                args.Cancel = !allowed;
                Trace("web-navigation-starting", new { args.NavigationId, args.Uri, args.IsRedirected, args.Cancel });
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            // Bind lifetime checks to the surface generation and captured core, independent of managed reference comparisons.
            _webMessageHandler = (sender, args) => Web_MessageReceived(core, args, generation, ReferenceEquals(sender, core));
            core.WebMessageReceived += _webMessageHandler;
            core.NavigationCompleted += (_, args) =>
            {
                if (generation != _webGeneration) return;
                bool expectedDocument = navigationPolicy.IsCurrent(args.NavigationId);
                Trace("web-navigation", new { args.NavigationId, args.IsSuccess, error = args.WebErrorStatus.ToString(),
                    args.HttpStatusCode, source = core.Source, expectedDocument });
                if (!expectedDocument) return;
                _webReady = args.IsSuccess;
                if (args.IsSuccess) navigation.TrySetResult();
                else navigation.TrySetException(new InvalidOperationException($"Local WebView2 navigation failed: {args.WebErrorStatus} (HTTP {args.HttpStatusCode}, navigation {args.NavigationId})."));
            };
            core.ProcessFailed += (_, args) =>
            {
                if (generation != _webGeneration) return;
                _webReady = false;
                var error = new InvalidOperationException("WebView2 process failed: " + args.ProcessFailedKind);
                navigation.TrySetException(error);
                Trace("web-process-failed", new { kind = args.ProcessFailedKind.ToString() });
            };
            // Resolve the fixed HTTPS origin to packaged assets, without a local server or network fetch.
            core.SetVirtualHostNameToFolderMapping(WebProbeProtocol.HostName,
                Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "if (location.href === " + JsonSerializer.Serialize(WebProbeProtocol.DocumentUri) +
                ") Object.defineProperty(window, '__maniSessionToken', { value: " + JsonSerializer.Serialize(_webToken) + " });").AsTask(token);
            core.Navigate(WebProbeProtocol.DocumentUri);
            await navigation.Task.WaitAsync(token);
            string bridgeReady = await core.ExecuteScriptAsync(
                "Boolean(typeof window.__maniSessionToken === 'string' && window.maniProbe && document.getElementById('pause') && document.getElementById('play'))").AsTask(token);
            Trace("web-document-ready", new { source = core.Source, bridgeReady = bridgeReady == "true" });
            if (bridgeReady != "true") throw new InvalidOperationException("Local WebView2 page loaded without its command bridge.");
        }
        catch { CloseWebProbe(); throw; }
    }

    private void Web_MessageReceived(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs args, int generation, bool senderReferenceMatches)
    {
        try
        {
            string? reason = WebProbeProtocol.DispatchRejection(generation, _webGeneration,
                _closing, _webReady, _m1Busy, _webCheckAcceptsCommands, _session is not null && _web is not null);
            Trace("web-message-received", new { source = args.Source, generation, currentGeneration = _webGeneration,
                senderReferenceMatches, acceptedByLifecycle = reason is null, reason });
            if (reason is not null) return;
            if (!WebProbeProtocol.TryParse(args.Source, args.WebMessageAsJson, _webToken, out var request))
            {
                Trace("web-message-rejected", new { source = args.Source, reason = "Invalid command envelope" });
                return;
            }
            // Serialize requests. Closing/replacing the web surface invalidates pending messages.
            _webCommandTask = ApplyWebPauseAsync(_webCommandTask, core, request, generation);
        }
        catch (Exception error) { Trace("web-message-error", new { error = error.ToString() }); }
    }

    private async Task ApplyWebPauseAsync(Task previous, CoreWebView2 sender, PauseRequest request, int generation)
    {
        await previous;
        if (WebProbeProtocol.DispatchRejection(generation, _webGeneration,
            _closing, _webReady, _m1Busy, _webCheckAcceptsCommands, _session is not null && _web is not null) is not null) return;
        try
        {
            await _session!.SetAsync("pause", request.Paused ? "yes" : "no");
            Trace("web-command", new { request.RequestId, request.Paused });
            if (!_closing && generation == _webGeneration && _web is not null)
            {
                sender.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "response", requestId = request.RequestId, success = true }));
                PublishWebState(await _session.ReadAsync());
            }
        }
        catch (Exception error) { Trace("web-command-error", new { error = error.ToString() }); }
    }

    private void PublishWebState(PlaybackSnapshot state)
    {
        if (!_webReady || _web?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", paused = state.Paused, position = state.Position, duration = state.Duration, idle = state.Idle })); }
        catch (Exception error) { _webReady = false; Trace("web-publish-error", new { error = error.ToString() }); }
    }

    private void CloseWebProbe()
    {
        _webGeneration++;
        _webReady = false;
        var web = _web;
        _web = null;
        var messageHandler = _webMessageHandler;
        _webMessageHandler = null;
        try
        {
            if (web is not null)
            {
                if (web.CoreWebView2 is not null && messageHandler is not null) web.CoreWebView2.WebMessageReceived -= messageHandler;
                web.Close();
            }
        }
        finally
        {
            WebProbeContainer.Children.Clear();
            WebProbePanel.Visibility = Visibility.Collapsed;
        }
    }
}
