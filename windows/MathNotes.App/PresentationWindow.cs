using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MathNotes.App;

/// <summary>
/// Praesentationsfenster fuer die Vorlesung: zeigt die Seite gesetzt und gross (Formeln mit KaTeX, Text normal),
/// live aktualisiert. Auf den Projektor ziehen und mit F11 oder dem Knopf im Hauptfenster als Vollbild anzeigen.
/// </summary>
public sealed class PresentationWindow : Window
{
    private readonly WebView2 _web = new();
    private bool _ready;
    private string? _pending;

    public PresentationWindow()
    {
        Title = "MathNotes - Praesentation";
        Content = _web;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "preview");
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "mathnotes.local", folder, CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "key:F11") ToggleFullScreen();
                else if (msg == "key:Escape") ExitFullScreen();
            };
            _web.NavigationCompleted += async (_, _) =>
            {
                _ready = true;
                if (_pending is not null) await PushAsync(_pending);
            };
            _web.Source = new Uri("https://mathnotes.local/lecture.html");
        }
        catch
        {
            // Praesentation ist optional: Fehler duerfen das Schreiben nie blockieren
        }
    }

    public async Task PushAsync(string json)
    {
        _pending = json;
        if (!_ready) return;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync("setLecture(" + json + ")");
        }
        catch
        {
            // egal
        }
    }

    public void ToggleFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ExitFullScreen();
        else AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    public void ExitFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
    }
}
