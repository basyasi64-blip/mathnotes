using MathNotes.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace MathNotes.App;

public sealed partial class MainWindow : Window
{
    private readonly NotebookStore _store;
    private readonly long _page;
    private List<PointData>? _current;
    private Polyline? _currentLine;
    private long _startTicks;
    private bool _previewReady;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MathNotes";
        var dbPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MathNotes", "notebook.db");
        _store = new NotebookStore(dbPath);
        var nb = _store.FirstNotebook() ?? _store.CreateNotebook("Mein Notizbuch");
        _page = _store.FirstPage(nb);
        Closed += (_, _) => _store.Dispose();

        foreach (var (_, s) in _store.Strokes(_page))
            InkSurface.Children.Add(ToPolyline(s));
        LatexBox.Text = _store.EditedFormula(_page) ?? "";
        LatexBox.TextChanged += async (_, _) => await RenderPreviewAsync();
        _ = InitPreviewAsync();
        StatusText.Text = $"Datenbank: {dbPath}. Jeder Strich wird sofort gespeichert. " +
                          "Keine Handschrifterkennung aktiv: LaTeX bitte selbst eingeben.";
    }

    private async Task InitPreviewAsync()
    {
        try
        {
            await Preview.EnsureCoreWebView2Async();
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "preview");
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "mathnotes.local", folder, CoreWebView2HostResourceAccessKind.Allow);
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.NavigationCompleted += async (_, _) =>
            {
                _previewReady = true;
                await RenderPreviewAsync();
            };
            Preview.Source = new Uri("https://mathnotes.local/preview.html");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Formelvorschau nicht verfuegbar: " + ex.Message;
        }
    }

    private async Task RenderPreviewAsync()
    {
        if (!_previewReady) return;
        var js = "render(" + System.Text.Json.JsonSerializer.Serialize(LatexBox.Text) + ")";
        await Preview.CoreWebView2.ExecuteScriptAsync(js);
    }

    private static Polyline ToPolyline(StrokeData s)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = s.Width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        foreach (var p in s.Points) line.Points.Add(new Point(p.X, p.Y));
        return line;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        InkSurface.CapturePointer(e.Pointer);
        _current = new List<PointData>();
        _startTicks = Environment.TickCount64;
        _currentLine = new Polyline
        {
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = 2.0,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        InkSurface.Children.Add(_currentLine);
        AddPoint(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_current is null) return;
        AddPoint(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_current is null) return;
        AddPoint(e);
        InkSurface.ReleasePointerCapture(e.Pointer);
        _store.AddStroke(_page, new StrokeData("#000000", 2.0, _current));
        _current = null;
        _currentLine = null;
    }

    private void AddPoint(PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(InkSurface);
        var pos = pp.Position;
        _current!.Add(new PointData(pos.X, pos.Y, pp.Properties.Pressure, Environment.TickCount64 - _startTicks));
        _currentLine!.Points.Add(pos);
    }

    private void OnSaveFormula(object sender, RoutedEventArgs e)
    {
        _store.SaveEditedFormula(_page, LatexBox.Text);
        StatusText.Text = "Korrektur gespeichert (Verlauf bleibt erhalten).";
    }
}
