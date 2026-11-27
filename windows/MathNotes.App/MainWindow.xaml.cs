using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
using Windows.ApplicationModel.DataTransfer;
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

    // Praesentation
    private PresentationWindow? _presentation;
    private double _lectureScale = 1.0;

    // Scrollen: die Seite ist laenger als das Fenster; die Striche liegen in Seitenkoordinaten
    private readonly TranslateTransform _scroll = new();
    private double _scrollY;
    private double _contentBottom;

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
            _presentation?.Close();
            _service.Dispose();
            _store.Dispose();
        };

        InkSurface.RenderTransform = _scroll;
        OverlaySurface.RenderTransform = _scroll;
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

    private string DefaultKind => ModeBox.SelectedIndex switch { 1 => "text", 2 => "graph", _ => "formula" };

    private Task PushLectureAsync() =>
        _presentation is null
            ? Task.CompletedTask
            : _presentation.PushAsync(LectureBuilder.Build(_groups, _formulas, Math.Max(Viewport.ActualWidth, 1), _lectureScale));

    private async Task RefreshPreviewAsync()
    {
        _ = PushLectureAsync();
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
                    ["kind"] = f.Kind,
                    ["props"] = f.Props.ToNode(),
                    ["align"] = i < _groups.Count
                        ? BlockAlignment.Resolve(f.Align, _groups[i].Box, Math.Max(Viewport.ActualWidth, 1))
                        : "center",
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
        SelectedLabel.Text = $"Block {idx + 1} ({(f.Kind == "text" ? "Text" : f.Kind == "graph" ? "Graph" : "Formel")})" +
            (f.EditedLatex is not null ? " (von dir korrigiert)"
             : f.RecognizedLatex is not null ? " (erkannt, ungeprueft)" : "");
        KindBox.IsEnabled = true;
        AlignBox.IsEnabled = true;
        KindBox.SelectedIndex = f.Kind switch { "text" => 1, "graph" => 2, _ => 0 };
        var pr = f.Props;
        var isText = f.Kind == "text";
        SizeBox.IsEnabled = true;
        SizeBox.SelectedIndex = NearestScaleIndex(pr.Scale);
        BoldToggle.IsEnabled = ItalicToggle.IsEnabled = UnderlineToggle.IsEnabled = isText;
        BoldToggle.IsChecked = pr.Bold;
        ItalicToggle.IsChecked = pr.Italic;
        UnderlineToggle.IsChecked = pr.Underline;
        GraphPanel.Visibility = f.Kind == "graph" ? Visibility.Visible : Visibility.Collapsed;
        XMinBox.Text = pr.XMin?.ToString(CultureInfo.InvariantCulture) ?? "";
        XMaxBox.Text = pr.XMax?.ToString(CultureInfo.InvariantCulture) ?? "";
        YMinBox.Text = pr.YMin?.ToString(CultureInfo.InvariantCulture) ?? "";
        YMaxBox.Text = pr.YMax?.ToString(CultureInfo.InvariantCulture) ?? "";
        AlignBox.SelectedIndex = f.Align switch { "left" => 1, "center" => 2, "right" => 3, _ => 0 };
        LatexBox.PlaceholderText = f.Kind == "text" ? "Text des Blocks" : f.Kind == "graph" ? "Funktion, z. B. f(x)=x^2 oder \\sin x (mehrere mit Komma)" : "LaTeX der gewaehlten Formel";
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
        SelectedLabel.Text = "Kein Block gewaehlt (in der Liste oben anklicken)";
        KindBox.IsEnabled = false;
        AlignBox.IsEnabled = false;
        SizeBox.IsEnabled = false;
        BoldToggle.IsEnabled = false;
        ItalicToggle.IsEnabled = false;
        UnderlineToggle.IsEnabled = false;
        GraphPanel.Visibility = Visibility.Collapsed;
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

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _selectedId is not long id) return;
        var kind = KindBox.SelectedIndex switch { 1 => "text", 2 => "graph", _ => "formula" };
        var cur = _formulas.FirstOrDefault(x => x.Id == id);
        if (cur is null || cur.Kind == kind) return;
        _store.SetKind(id, kind);   // Inhalt wird geleert, der Verlauf bleibt erhalten
        _failed.RemoveWhere(k => k.StartsWith(id + "|"));
        _draftActive = false;
        var rec = _store.GetFormula(id);
        if (rec is not null) ReplaceFormula(rec);
        OnStrokesChanged();
        StatusText.Text = "Typ geaendert. Der Block wird neu erkannt.";
    }

    private async void OnAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _selectedId is not long id) return;
        string? align = AlignBox.SelectedIndex switch { 1 => "left", 2 => "center", 3 => "right", _ => null };
        _store.SetAlign(id, align);
        var rec = _store.GetFormula(id);
        if (rec is not null) ReplaceFormula(rec);
        await RefreshPreviewAsync();
    }

    private async void OnOpenPresentation(object sender, RoutedEventArgs e)
    {
        if (_presentation is null)
        {
            _presentation = new PresentationWindow();
            _presentation.Closed += (_, _) => _presentation = null;
        }
        _presentation.Activate();
        await PushLectureAsync();
        StatusText.Text = "Praesentationsfenster geoeffnet. Auf den Projektor ziehen und 'Vollbild' (oder F11) waehlen.";
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => _presentation?.ToggleFullScreen();

    private async void OnScaleUp(object sender, RoutedEventArgs e)
    {
        _lectureScale = Math.Clamp(_lectureScale + 0.1, 0.5, 2.5);
        await PushLectureAsync();
    }

    private async void OnScaleDown(object sender, RoutedEventArgs e)
    {
        _lectureScale = Math.Clamp(_lectureScale - 0.1, 0.5, 2.5);
        await PushLectureAsync();
    }

    private static int NearestScaleIndex(double scale)
    {
        var best = 0;
        for (var i = 1; i < BlockProps.ScaleSteps.Length; i++)
            if (Math.Abs(BlockProps.ScaleSteps[i] - scale) < Math.Abs(BlockProps.ScaleSteps[best] - scale)) best = i;
        return best;
    }

    private async Task UpdatePropsAsync(Func<BlockProps, BlockProps> change)
    {
        if (_selectedId is not long id) return;
        var f = _formulas.FirstOrDefault(x => x.Id == id);
        if (f is null) return;
        _store.SetProps(id, change(f.Props));
        var rec = _store.GetFormula(id);
        if (rec is not null) ReplaceFormula(rec);
        await RefreshPreviewAsync();
    }

    private async void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || SizeBox.SelectedIndex < 0) return;
        var scale = BlockProps.ScaleSteps[SizeBox.SelectedIndex];
        await UpdatePropsAsync(p => p with { Scale = scale });
    }

    private async void OnBoldClick(object sender, RoutedEventArgs e)
    {
        var v = BoldToggle.IsChecked == true;
        await UpdatePropsAsync(p => p with { Bold = v });
    }

    private async void OnItalicClick(object sender, RoutedEventArgs e)
    {
        var v = ItalicToggle.IsChecked == true;
        await UpdatePropsAsync(p => p with { Italic = v });
    }

    private async void OnUnderlineClick(object sender, RoutedEventArgs e)
    {
        var v = UnderlineToggle.IsChecked == true;
        await UpdatePropsAsync(p => p with { Underline = v });
    }

    private async void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        double? Parse(TextBox b) =>
            double.TryParse(b.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var x0 = Parse(XMinBox);
        var x1 = Parse(XMaxBox);
        var y0 = Parse(YMinBox);
        var y1 = Parse(YMaxBox);
        await UpdatePropsAsync(p => p with { XMin = x0, XMax = x1, YMin = y0, YMax = y1 });
    }

    private async void OnCopyTikz(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not long id || !_previewReady) return;
        var f = _formulas.FirstOrDefault(x => x.Id == id);
        if (f is null || f.Kind != "graph") return;
        try
        {
            var latex = _draftActive ? LatexBox.Text : f.CurrentLatex ?? "";
            var js = $"JSON.stringify(graphTikzResult({JsonSerializer.Serialize(latex)}, {f.Props.ToNode().ToJsonString()}))";
            var raw = await Preview.CoreWebView2.ExecuteScriptAsync(js);   // liefert einen JSON-String
            var res = JsonNode.Parse(JsonSerializer.Deserialize<string>(raw)!)!.AsObject();
            if (res["ok"]!.GetValue<bool>())
            {
                var package = new DataPackage();
                package.SetText(res["text"]!.GetValue<string>());
                Clipboard.SetContent(package);
                StatusText.Text = "TikZ-Code kopiert. In LaTeX einfuegen; benoetigt \\usepackage{pgfplots} und \\pgfplotsset{compat=1.18}.";
            }
            else
            {
                StatusText.Text = "TikZ nicht moeglich: " + res["error"]!.GetValue<string>();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "TikZ-Fehler: " + ex.Message;
        }
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
        _formulas = _store.SyncFormulas(_page, _groups.Select(g => g.Ids).ToList(), DefaultKind);
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
            StatusText.Text = $"{_groups.Count} Block/Bloecke. Alles erkannt (ungeprueft). Anklicken zum Pruefen oder Korrigieren.";
            return;
        }

        var byId = visible.ToDictionary(s => s.Id, s => s.Stroke);
        var needsSetup = false;
        foreach (var f in todo)
        {
            if (gen != _generation) return;   // es wurde weitergeschrieben: der Timer startet eine neue Analyse
            var strokes = f.StrokeIds.Where(byId.ContainsKey).Select(i => byId[i]).ToList();

            RecognitionOutcome outcome;
            if (f.Kind == "text")
            {
                _states[f.Id] = "Text wird erkannt ...";
                await RefreshPreviewAsync();
                outcome = await InkTextRecognizer.RecognizeAsync(strokes);   // Windows, lokal, ohne Einrichtung
            }
            else if (!_service.IsInstalled)
            {
                needsSetup = true;
                _failed.Add(FailKey(f));
                _states[f.Id] = "Formel-Erkennung noch nicht eingerichtet";
                continue;
            }
            else
            {
                _states[f.Id] = "wird erkannt ...";
                StatusText.Text = "Erkennung laeuft (lokal) ...";
                await RefreshPreviewAsync();
                outcome = await _service.RecognizeAsync(strokes);
            }

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
        await RefreshPreviewAsync();
        StatusText.Text = needsSetup
            ? "Formeln brauchen die Formel-Erkennung: bitte 'Erkennung einrichten...' waehlen. (Text funktioniert ohne Einrichtung.)"
            : "Erkennung fertig (ungeprueft). Block in der Liste anklicken, um ihn zu pruefen oder zu korrigieren.";
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

    // ---------------------------------------------------------------- Scrollen (Seite wandert nach oben)

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private double MaxScroll() => Math.Max(0, _contentBottom + 0.6 * Viewport.ActualHeight - Viewport.ActualHeight);

    private void ApplyScroll()
    {
        _scroll.Y = -_scrollY;
        _ = RefreshPreviewAsync();   // haelt die Ausrichtung (Seitenbreite) aktuell
    }

    private void ScrollManual(double y)
    {
        _scrollY = Math.Clamp(y, 0, MaxScroll());
        ApplyScroll();
    }

    private void OnScrollUp(object sender, RoutedEventArgs e) => ScrollManual(_scrollY - 0.7 * Viewport.ActualHeight);

    private void OnScrollDown(object sender, RoutedEventArgs e) => ScrollManual(_scrollY + 0.7 * Viewport.ActualHeight);

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;   // +120 pro Raste nach oben
        ScrollManual(_scrollY - delta);
        e.Handled = true;
    }

    /// <summary>Wird unten geschrieben, rueckt die Seite nach oben, damit unten wieder Platz ist.</summary>
    private void AutoScrollIfNeeded(double lowestPageY)
    {
        var h = Viewport.ActualHeight;
        if (h <= 0) return;
        if (lowestPageY - _scrollY > 0.82 * h)
        {
            _scrollY += 0.45 * h;
            _scroll.Y = -_scrollY;
        }
    }

    private Point PagePos(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Viewport).Position;
        return new Point(p.X, p.Y + _scrollY);   // Seitenkoordinaten (unabhaengig vom aktuellen Scroll)
    }

    // ---------------------------------------------------------------- Zeichnen, Radieren, Rueckgaengig

    private void ReloadCanvas()
    {
        InkSurface.Children.Clear();
        _contentBottom = 0;
        foreach (var (id, st) in _store.Strokes(_page))
        {
            var pl = ToPolyline(st);
            pl.Tag = id;
            InkSurface.Children.Add(pl);
            if (st.Points.Count > 0) _contentBottom = Math.Max(_contentBottom, st.Points.Max(p => p.Y));
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
        _scrollY = 0;
        _scroll.Y = 0;
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
        Viewport.CapturePointer(e.Pointer);
        var pp0 = e.GetCurrentPoint(Viewport);
        _erasing = EraserToggle.IsChecked == true || pp0.Properties.IsEraser;   // auch Stift-Radierende
        if (_erasing)
        {
            _pendingErase = new HashSet<long>();
            EraseAt(PagePos(e));
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
        if (_erasing) { EraseAt(PagePos(e)); return; }
        if (_current is null) return;
        AddPoint(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_erasing)
        {
            _erasing = false;
            Viewport.ReleasePointerCapture(e.Pointer);
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
        Viewport.ReleasePointerCapture(e.Pointer);
        var newId = _store.AddStroke(_page, new StrokeData("#000000", 2.0, _current));
        _currentLine!.Tag = newId;
        _undo.Push(('D', newId));
        var lowest = _current.Max(p => p.Y);
        _contentBottom = Math.Max(_contentBottom, lowest);
        _current = null;
        _currentLine = null;
        AutoScrollIfNeeded(lowest);
        OnStrokesChanged();
    }

    private void AddPoint(PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(Viewport);
        var pos = new Point(pp.Position.X, pp.Position.Y + _scrollY);
        _current!.Add(new PointData(pos.X, pos.Y, pp.Properties.Pressure, Environment.TickCount64 - _startTicks));
        _currentLine!.Points.Add(pos);
    }
}
