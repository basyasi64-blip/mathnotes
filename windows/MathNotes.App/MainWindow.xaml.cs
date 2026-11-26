using System.Diagnostics;
using System.Text.Json.Nodes;
using MathNotes.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
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
    private const double EraseRadius = 14;

    private readonly NotebookStore _store;
    private readonly long _page;
    private readonly RecognizerService _service;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _analysisTimer;

    // Zeichnen / Radieren
    private List<PointData>? _current;
    private Polyline? _currentLine;
    private long _startTicks;
    private bool _erasing;
    private HashSet<long>? _pendingErase;
    private readonly Stack<(char Kind, long Id)> _undo = new();   // 'D' = Strich gezeichnet, 'E' = Radiergruppe

    // Formeln / Erkennung
    private int _generation;
    private bool _analyzing;
    private bool _rerun;
    private bool _forceRecognize;
    private bool _previewReady;
    private bool _updatingUi;
    private bool _draftActive;
    private List<StrokeGroup> _groups = new();
    private List<FormulaRecord> _formulas = new();
    private long? _selectedId;
    private readonly Dictionary<long, string> _states = new();
    private readonly HashSet<string> _failed = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "MathNotes";
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        var dbPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MathNotes", "notebook.db");
        _store = new NotebookStore(dbPath);
        var nb = _store.FirstNotebook() ?? _store.CreateNotebook("Mein Notizbuch");
        _page = _store.FirstPage(nb);

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _service = new RecognizerService(
            Pix2TexClient.DefaultPythonPath(),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "recognizer", "recognize.py"));
        _service.StatusChanged += msg => _dispatcher.TryEnqueue(() => StatusText.Text = msg);

        _analysisTimer = _dispatcher.CreateTimer();
        _analysisTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _analysisTimer.IsRepeating = false;
        _analysisTimer.Tick += async (_, _) => await AnalyzeAsync();

        Closed += (_, _) =>
        {
            _analysisTimer.Stop();
            _service.Dispose();
            _store.Dispose();
        };

        ReloadCanvas();
        LiveToggle.IsOn = true;
        LiveToggle.Toggled += OnLiveToggled;
        _ = InitPreviewAsync();
        StatusText.Text = $"Datenbank: {dbPath}. Jeder Strich wird sofort gespeichert. " +
                          "Schreibe mehrere Formeln neben- und untereinander (mit etwas Abstand).";
        OnStrokesChanged();
    }

    // ---------------------------------------------------------------- Vorschau (KaTeX in WebView2)

    private async Task InitPreviewAsync()
    {
        try
        {
            await Preview.EnsureCoreWebView2Async();
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "preview");
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "mathnotes.local", folder, CoreWebView2HostResourceAccessKind.Allow);
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (long.TryParse(args.TryGetWebMessageAsString(), out var id)) SelectFormula(id);
            };
            Preview.NavigationCompleted += async (_, _) =>
            {
                _previewReady = true;
                await RefreshPreviewAsync();
            };
            Preview.Source = new Uri("https://mathnotes.local/preview.html");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Formelvorschau nicht verfuegbar: " + ex.Message;
        }
    }

    private async Task RefreshPreviewAsync()
    {
        if (!_previewReady) return;
        try
        {
            var items = new JsonArray();
            for (var i = 0; i < _formulas.Count; i++)
            {
                var f = _formulas[i];
                var isSel = _selectedId == f.Id;
                var draft = isSel && _draftActive;
                var latex = draft ? LatexBox.Text : f.CurrentLatex;
                _states.TryGetValue(f.Id, out var running);

                string state;
                if (draft) state = "bearbeitet, noch nicht gespeichert";
                else if (f.EditedLatex is not null) state = "von dir korrigiert";
                else if (f.RecognizedLatex is not null) state = "erkannt (ungeprueft)";
                else state = running ?? "wartet auf Erkennung";
                if (running is not null && f.CurrentLatex is not null && !draft) state += " | " + running;

                items.Add(new JsonObject
                {
                    ["id"] = f.Id,
                    ["n"] = i + 1,
                    ["latex"] = latex ?? "",
                    ["state"] = state,
                    ["selected"] = isSel,
                });
            }
            await Preview.CoreWebView2.ExecuteScriptAsync("renderAll(" + items.ToJsonString() + ")");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Vorschau-Fehler: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- Formel-Auswahl und Bearbeitung

    private void SelectFormula(long id)
    {
        var f = _formulas.FirstOrDefault(x => x.Id == id);
        if (f is null) return;
        _selectedId = id;
        _draftActive = false;
        SetEditorFor(f);
        DrawOverlay();
        _ = RefreshPreviewAsync();
    }

    private void SetEditorFor(FormulaRecord f)
    {
        _updatingUi = true;
        var idx = _formulas.FindIndex(x => x.Id == f.Id);
        SelectedLabel.Text = $"Formel {idx + 1}" +
            (f.EditedLatex is not null ? " (von dir korrigiert)"
             : f.RecognizedLatex is not null ? " (erkannt, ungeprueft)" : "");
        LatexBox.IsEnabled = true;
        SaveButton.IsEnabled = true;
        LatexBox.Text = f.CurrentLatex ?? "";
        CandidateBox.Items.Clear();
        var cands = new[] { f.RecognizedLatex }.Concat(f.Alternatives)
            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        foreach (var c in cands) CandidateBox.Items.Add(c);
        CandidateBox.Visibility = cands.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CandidateBox.SelectedIndex = -1;
        _updatingUi = false;
    }

    private void ClearEditor()
    {
        _updatingUi = true;
        _selectedId = null;
        _draftActive = false;
        SelectedLabel.Text = "Keine Formel gewaehlt (in der Liste oben anklicken)";
        LatexBox.Text = "";
        LatexBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CandidateBox.Items.Clear();
        CandidateBox.Visibility = Visibility.Collapsed;
        _updatingUi = false;
    }

    private async void OnLatexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingUi || _selectedId is null) return;
        var f = _formulas.FirstOrDefault(x => x.Id == _selectedId);
        if (f is null) return;
        _draftActive = LatexBox.Text != (f.CurrentLatex ?? "");   // nur echte Aenderungen sind ein Entwurf
        await RefreshPreviewAsync();
    }

    private void OnCandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || CandidateBox.SelectedItem is not string latex) return;
        LatexBox.Text = latex;
    }

    private async void OnSaveFormula(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not long id) return;
        _store.EditFormula(id, LatexBox.Text);
        _draftActive = false;
        var rec = _store.GetFormula(id);
        if (rec is not null)
        {
            ReplaceFormula(rec);
            SetEditorFor(rec);
        }
        await RefreshPreviewAsync();
        StatusText.Text = "Korrektur gespeichert (Verlauf bleibt erhalten).";
    }

    private void ReplaceFormula(FormulaRecord rec)
    {
        var idx = _formulas.FindIndex(x => x.Id == rec.Id);
        if (idx < 0) return;
        _formulas[idx] = rec;
        if (_selectedId == rec.Id && !_draftActive) SetEditorFor(rec);
    }

    // ---------------------------------------------------------------- Analyse (Gruppieren + Live-Erkennung)

    private void OnStrokesChanged()
    {
        _generation++;
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    private void OnLiveToggled(object sender, RoutedEventArgs e)
    {
        if (LiveToggle.IsOn) OnStrokesChanged();
    }

    private async void OnRecognizeNow(object sender, RoutedEventArgs e)
    {
        _failed.Clear();
        _forceRecognize = true;
        await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_analyzing)
        {
            _rerun = true;
            return;
        }
        _analyzing = true;
        try
        {
            do
            {
                _rerun = false;
                await AnalyzeOnceAsync();
            } while (_rerun);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Analysefehler: " + ex.Message;
        }
        finally
        {
            _analyzing = false;
            _forceRecognize = false;
        }
    }

    private static string FailKey(FormulaRecord f) => $"{f.Id}|{string.Join(",", f.StrokeIds)}";

    private async Task AnalyzeOnceAsync()
    {
        var gen = _generation;
        var visible = _store.Strokes(_page);
        _groups = StrokeGrouping.Group(visible);
        _formulas = _store.SyncFormulas(_page, _groups.Select(g => g.Ids).ToList());
        if (_selectedId is long sel && !_formulas.Any(f => f.Id == sel)) ClearEditor();
        DrawOverlay();
        await RefreshPreviewAsync();

        if (_groups.Count == 0)
        {
            StatusText.Text = "Schreibe eine Formel.";
            return;
        }
        if (!(LiveToggle.IsOn || _forceRecognize))
        {
            StatusText.Text = $"{_groups.Count} Formel(n) gefunden. Live-Erkennung ist aus ('Jetzt erkennen' benutzen).";
            return;
        }

        var todo = _formulas.Where(f => f.NeedsRecognition && !_failed.Contains(FailKey(f))).ToList();
        if (todo.Count == 0)
        {
            StatusText.Text = $"{_groups.Count} Formel(n). Alles erkannt (ungeprueft). Formel in der Liste anklicken zum Pruefen oder Korrigieren.";
            return;
        }
        if (!_service.IsInstalled)
        {
            StatusText.Text = "Die Erkennung ist noch nicht eingerichtet. Bitte 'Erkennung einrichten...' waehlen.";
            return;
        }

        var byId = visible.ToDictionary(s => s.Id, s => s.Stroke);
        foreach (var f in todo)
        {
            if (gen != _generation) return;   // es wurde weitergeschrieben: der Timer startet eine neue Analyse
            _states[f.Id] = "wird erkannt ...";
            StatusText.Text = "Erkennung laeuft (lokal) ...";
            await RefreshPreviewAsync();

            var strokes = f.StrokeIds.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
            var outcome = await _service.RecognizeAsync(strokes);
            if (outcome.Recognized)
            {
                _store.SaveRecognitionFor(f.Id, f.StrokeIds, outcome);
                _states.Remove(f.Id);
                var rec = _store.GetFormula(f.Id);
                if (rec is not null) ReplaceFormula(rec);
            }
            else
            {
                _failed.Add(FailKey(f));
                _states[f.Id] = outcome.Error is null ? "nichts erkannt" : "Fehler: " + outcome.Error;
            }
            await RefreshPreviewAsync();
        }
        StatusText.Text = "Erkennung fertig (ungeprueft). Formel in der Liste anklicken, um sie zu pruefen oder zu korrigieren.";
    }

    private void DrawOverlay()
    {
        OverlaySurface.Children.Clear();
        for (var i = 0; i < _groups.Count; i++)
        {
            var b = _groups[i].Box;
            var isSel = i < _formulas.Count && _formulas[i].Id == _selectedId;
            var brush = new SolidColorBrush(isSel ? Colors.OrangeRed : Colors.DodgerBlue);
            var rect = new Rectangle
            {
                Width = b.MaxX - b.MinX + 12,
                Height = b.MaxY - b.MinY + 12,
                Stroke = brush,
                StrokeThickness = isSel ? 2 : 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 4,
                RadiusY = 4,
            };
            Canvas.SetLeft(rect, b.MinX - 6);
            Canvas.SetTop(rect, b.MinY - 6);
            OverlaySurface.Children.Add(rect);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = brush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            };
            Canvas.SetLeft(label, b.MinX - 6);
            Canvas.SetTop(label, b.MinY - 22);
            OverlaySurface.Children.Add(label);
        }
    }

    // ---------------------------------------------------------------- Einrichtung der Erkennung

    private async void OnSetupRecognizer(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Erkennung einrichten",
            Content = "Es wird eine lokale Python-Umgebung angelegt und die Erkennung TexTeller installiert (auch fuer Handschrift).\n\n" +
                      "Downloads (einmalig, zusammen einige GB):\n" +
                      "- Python-Pakete von pypi.org (u. a. PyTorch, TexTeller)\n" +
                      "- Modellgewichte (ca. 1,2 GB) von huggingface.co\n\n" +
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
        StatusText.Text = "Einrichtung laeuft in einem eigenen Fenster. Danach 'Jetzt erkennen' waehlen.";
    }

    // ---------------------------------------------------------------- Zeichnen, Radieren, Rueckgaengig

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
        OnStrokesChanged();
        StatusText.Text = "Rueckgaengig gemacht.";
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var ids = _store.Strokes(_page).Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        _undo.Push(('E', _store.EraseStrokes(ids)));
        ReloadCanvas();
        OnStrokesChanged();
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
        _analysisTimer.Stop();   // waehrend des Schreibens keine Analyse starten
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
                OnStrokesChanged();
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
        OnStrokesChanged();
    }

    private void AddPoint(PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(InkSurface);
        var pos = pp.Position;
        _current!.Add(new PointData(pos.X, pos.Y, pp.Properties.Pressure, Environment.TickCount64 - _startTicks));
        _currentLine!.Points.Add(pos);
    }
}
