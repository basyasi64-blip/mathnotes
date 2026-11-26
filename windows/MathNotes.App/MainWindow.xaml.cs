using System.Diagnostics;
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
    private bool _erasing;
    private HashSet<long>? _pendingErase;
    private readonly Stack<(char Kind, long Id)> _undo = new();   // 'D' = Strich gezeichnet, 'E' = Radiergruppe
    private const double EraseRadius = 14;
    private readonly Pix2TexClient _recognizer = new(
        Pix2TexClient.DefaultPythonPath(),
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "recognizer", "recognize.py"));

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

        ReloadCanvas();
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

    private void ReloadCanvas()
    {
        InkSurface.Children.Clear();
        foreach (var (id, st) in _store.Strokes(_page))
        {
            var pl = ToPolyline(st);
            pl.Tag = id;
            InkSurface.Children.Add(pl);
        }
    }

    private void EraseAt(Point pos)
    {
        foreach (var pl in InkSurface.Children.OfType<Polyline>().ToList())
        {
            if (pl.Tag is long id && !_pendingErase!.Contains(id) &&
                StrokeGeometry.Hits(pl.Points.Select(q => (q.X, q.Y)), pos.X, pos.Y, EraseRadius))
            {
                _pendingErase.Add(id);
                InkSurface.Children.Remove(pl);
            }
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) { StatusText.Text = "Nichts zum Rueckgaengigmachen."; return; }
        var (kind, id) = _undo.Pop();
        if (kind == 'D') _store.EraseStrokes(new[] { id });   // gezeichneten Strich wieder ausblenden
        else _store.RestoreGroup(id);                          // Radieren rueckgaengig
        ReloadCanvas();
        StatusText.Text = "Rueckgaengig gemacht.";
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var ids = _store.Strokes(_page).Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        _undo.Push(('E', _store.EraseStrokes(ids)));
        ReloadCanvas();
        StatusText.Text = "Alle Striche ausgeblendet. Mit 'Rueckgaengig' holst du sie zurueck (Originale bleiben gespeichert).";
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
        var pp0 = e.GetCurrentPoint(InkSurface);
        _erasing = EraserToggle.IsChecked == true || pp0.Properties.IsEraser;   // auch Stift-Radierende
        if (_erasing)
        {
            _pendingErase = new HashSet<long>();
            EraseAt(pp0.Position);
            return;
        }
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
        if (_erasing) { EraseAt(e.GetCurrentPoint(InkSurface).Position); return; }
        if (_current is null) return;
        AddPoint(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_erasing)
        {
            _erasing = false;
            InkSurface.ReleasePointerCapture(e.Pointer);
            if (_pendingErase is { Count: > 0 })
            {
                _undo.Push(('E', _store.EraseStrokes(_pendingErase.ToList())));
                StatusText.Text = $"{_pendingErase.Count} Strich(e) ausgeblendet. Mit 'Rueckgaengig' wiederherstellbar.";
            }
            _pendingErase = null;
            return;
        }
        if (_current is null) return;
        AddPoint(e);
        InkSurface.ReleasePointerCapture(e.Pointer);
        var newId = _store.AddStroke(_page, new StrokeData("#000000", 2.0, _current));
        _currentLine!.Tag = newId;
        _undo.Push(('D', newId));
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

    private async void OnRecognize(object sender, RoutedEventArgs e)
    {
        var strokes = _store.Strokes(_page);
        if (strokes.Count == 0) { StatusText.Text = "Keine Striche zum Erkennen."; return; }
        if (!_recognizer.IsInstalled)
        {
            StatusText.Text = "Die Erkennung ist noch nicht eingerichtet. Bitte 'Erkennung einrichten...' waehlen.";
            return;
        }
        RecognizeButton.IsEnabled = false;
        StatusText.Text = "Erkennung laeuft (lokal, beim ersten Mal kann das einige Sekunden dauern) ...";
        try
        {
            var result = await _recognizer.RecognizeAsync(
                strokes.Select(s => s.Stroke).ToList(), TimeSpan.FromMinutes(3));
            if (!result.Recognized)
            {
                StatusText.Text = "Keine Erkennung: " + (result.Error ?? "leeres Ergebnis");
                return;
            }
            _store.SaveRecognition(_page, strokes.Select(s => s.Id).ToList(), result);
            SuggestionBox.Text = result.Candidates[0].Latex;
            AcceptButton.IsEnabled = true;
            StatusText.Text = $"Vorschlag von {result.Engine} {result.EngineVersion} (ungeprueft, ohne Konfidenzwert). " +
                              "Bitte pruefen, uebernehmen oder korrigieren. Deine Striche bleiben unveraendert.";
        }
        finally
        {
            RecognizeButton.IsEnabled = true;
        }
    }

    private void OnAcceptSuggestion(object sender, RoutedEventArgs e)
    {
        LatexBox.Text = SuggestionBox.Text;
        StatusText.Text = "Vorschlag uebernommen. Mit 'Korrektur speichern' bestaetigst du ihn.";
    }

    private async void OnSetupRecognizer(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Erkennung einrichten",
            Content = "Es wird eine lokale Python-Umgebung angelegt und pix2tex installiert.\n\n" +
                      "Downloads (einmalig, einige hundert MB bis GB):\n" +
                      "- Python-Pakete von pypi.org (u. a. PyTorch)\n" +
                      "- Modellgewichte von github.com\n\n" +
                      "Es werden keine Notizen oder Striche uebertragen. Die Erkennung selbst laeuft offline.",
            PrimaryButtonText = "Einrichten",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var script = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "recognizer", "setup-recognizer.ps1");
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
        });
        StatusText.Text = "Einrichtung laeuft in einem eigenen Fenster. Danach 'Formel erkennen' waehlen.";
    }
}
