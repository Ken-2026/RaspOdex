using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Renombra.Core;

namespace Renombra;

public partial class MainWindow : Window
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Brush ErrorColor = new SolidColorBrush(Color.FromRgb(156, 33, 51));
    private static readonly Brush ReadyColor = new SolidColorBrush(Color.FromRgb(24, 111, 79));
    private static readonly Brush NeutralColor = new SolidColorBrush(Color.FromRgb(82, 98, 124));

    private readonly List<FileItem> _files = [];
    private readonly Stack<IReadOnlyList<RenameMove>> _history = new();
    private const int MetadataBatchSize = 128;
    private static readonly FileMetadata EmptyMetadata = new();
    private static readonly TemplatePreset ChoosePreset = new("Elegir una plantilla", "");

    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _inspectorTimer;
    private IReadOnlyList<TemplatePreset> _savedPresets = [];
    private string? _presetProblem;
    private CancellationTokenSource? _inspectorCancellation;
    private string? _inspectorPath;
    private bool _updatingGrid;
    private IReadOnlyList<RenameRow> _preview = [];
    private CancellationTokenSource? _previewCancellation;
    private bool _ready;
    private bool _busy;
    private bool _previewReady;

    public MainWindow()
    {
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += OnPreviewTick;
        _inspectorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _inspectorTimer.Tick += OnInspectorTick;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
            return;

        _ready = true;
        ExifStatus.Text = ExifMetadataReader.IsAvailable ? "ExifTool disponible" : "ExifTool no encontrado";
        ExifTokenList.ItemsSource = ExifFields.All
            .Select(field => new ExifDisplay(field.Token, field.Label, "")).ToArray();
        LoadPresets();
        UpdateButtons();
        SetStatus(_presetProblem ?? "Añade archivos para preparar una tanda.", _presetProblem is not null);
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Length > 0)
            await AddPathsAsync(arguments);
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Añadir archivos a Renombra",
            Filter = "Todos los archivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync(dialog.FileNames);
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Añadir carpeta a Renombra" };
        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync([dialog.FolderName]);
    }

    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (_busy)
            return;

        var recursive = SubfoldersCheck.IsChecked == true;
        CancelPreview();
        SetBusy(true);
        SetStatus("Buscando archivos…");
        var added = 0;
        var skipped = 0;
        try
        {
            var result = await Task.Run(() => ScanPaths(paths, recursive));
            skipped = result.Skipped;
            var known = new HashSet<string>(_files.Select(file => file.FullPath), PathComparer);
            foreach (var file in result.Files)
            {
                if (known.Add(file.FullPath))
                {
                    _files.Add(file);
                    added++;
                }
            }
            ShowCurrentNames();
        }
        catch (Exception ex)
        {
            ShowError("Error al añadir archivos", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (added == 0)
        {
            SetStatus(skipped > 0
                ? "No se pudieron abrir una o más rutas."
                : "No se encontraron archivos nuevos.", skipped > 0);
        }

        RequestPreview(added == 0 ? StatusText.Text : skipped > 0 ? "Algunas rutas no se pudieron abrir." : null);
    }

    private static (List<FileItem> Files, int Skipped) ScanPaths(IReadOnlyList<string> paths, bool recursive)
    {
        var files = new List<FileItem>();
        var skipped = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.EnumerateFiles(fullPath, "*", options))
                        AddFile(child);
                }
                else
                {
                    AddFile(fullPath);
                }
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                skipped++;
            }
        }

        files.Sort((left, right) => PathComparer.Compare(left.FullPath, right.FullPath));
        return (files, skipped);

        void AddFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    files.Add(new FileItem(info.FullName, info.LastWriteTime, info.CreationTime));
                else
                    skipped++;
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                skipped++;
            }
        }
    }

    private static bool IsPathException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await AddPathsAsync(paths);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var selected = SelectedPaths();
        if (selected.Count == 0)
            return;

        _files.RemoveAll(file => selected.Contains(file.FullPath));
        ShowCurrentNames();
        RequestPreview();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        ShowCurrentNames();
        RequestPreview();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy)
            return;

        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnAddFiles(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OnAddFolder(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && FilesGrid.IsKeyboardFocusWithin)
        {
            OnRemove(this, e);
            e.Handled = true;
        }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGrid)
            return;

        UpdateButtons();
        RefreshInspector();
    }

    private void OnRuleChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && sender == TemplateBox && PresetBox.SelectedItem is TemplatePreset preset &&
            !ReferenceEquals(preset, ChoosePreset) && preset.Template != TemplateBox.Text)
            PresetBox.SelectedIndex = 0;

        RequestPreview();
    }
    private void OnRuleClick(object sender, RoutedEventArgs e) => RequestPreview();

    private void OnToken(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string token })
            return;

        InsertToken(token);
    }

    private void OnExifValue(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string token } && !_busy)
            InsertToken(token, separate: true);
    }

    private void InsertToken(string token, bool separate = false)
    {
        var index = TemplateBox.SelectionStart;
        if (separate && index > 0 && index == TemplateBox.Text.Length &&
            TemplateBox.Text[index - 1] is not ('_' or '-' or ' ' or '.'))
            token = "_" + token;

        TemplateBox.SelectedText = token;
        TemplateBox.Focus();
        TemplateBox.Select(index + token.Length, 0);
    }

    private void LoadPresets()
    {
        try
        {
            _savedPresets = TemplatePresets.Read();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _presetProblem = ex.Message;
            SavePresetButton.ToolTip = ex.Message;
        }
        FillPresets();
    }

    private void FillPresets(TemplatePreset? selected = null)
    {
        PresetBox.ItemsSource = new[] { ChoosePreset }.Concat(TemplatePresets.BuiltIn)
            .Concat(_savedPresets).ToArray();
        PresetBox.SelectedItem = selected ?? ChoosePreset;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PresetBox.SelectedItem is not TemplatePreset preset || ReferenceEquals(preset, ChoosePreset))
            return;

        _ready = false;
        TemplateBox.Text = preset.Template;
        StartBox.Text = preset.Start.ToString(CultureInfo.InvariantCulture);
        DigitsBox.Text = preset.Digits.ToString(CultureInfo.InvariantCulture);
        _ready = true;
        RequestPreview();
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (!TryReadRules(out var options))
            return;

        var name = AskPresetName();
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (TemplatePresets.BuiltIn.Any(preset => PathComparer.Equals(preset.Name, name)) ||
            PathComparer.Equals(ChoosePreset.Name, name))
        {
            ShowError("Nombre en uso", "Elige otro nombre para guardar la plantilla.");
            return;
        }

        var presets = _savedPresets.ToList();
        var index = presets.FindIndex(preset => PathComparer.Equals(preset.Name, name));
        var saved = new TemplatePreset(name, options.Template, options.Start, options.Digits);
        if (index >= 0)
        {
            if (MessageBox.Show(this, $"¿Sustituir la plantilla «{name}»?", "Guardar plantilla",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            presets[index] = saved;
        }
        else
        {
            presets.Add(saved);
        }

        try
        {
            TemplatePresets.Save(presets);
            _savedPresets = presets;
            FillPresets(saved);
            SetStatus($"Se guardó la plantilla «{name}».");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            ShowError("No se pudo guardar la plantilla", ex.Message);
        }
    }

    private string? AskPresetName()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Guardar plantilla",
            Icon = Icon,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var input = new TextBox { MaxLength = 80, Margin = new Thickness(0, 6, 0, 12) };
        var confirm = new Button { Content = "Guardar", IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(0, 0, 7, 0) };
        confirm.Style = (Style)FindResource("PrimaryButton");
        confirm.Click += (_, _) => dialog.DialogResult = true;
        input.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.Loaded += (_, _) => input.Focus();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new Label { Content = "Nombre de la plantilla", Target = input, Padding = new Thickness(0) });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void OnResetRules(object sender, RoutedEventArgs e)
    {
        ResetRules();
        RequestPreview();
    }

    private void ResetRules()
    {
        _ready = false;
        SearchBox.Clear();
        ReplacementBox.Clear();
        TemplateBox.Clear();
        PresetBox.SelectedIndex = 0;
        RegexCheck.IsChecked = false;
        MatchCaseCheck.IsChecked = false;
        AllMatchesCheck.IsChecked = true;
        ExtensionCheck.IsChecked = false;
        CaseBox.SelectedIndex = 0;
        NumberCheck.IsChecked = false;
        StartBox.Text = "1";
        DigitsBox.Text = "3";
        _ready = true;
    }

    private bool UsesNumbering => NumberCheck.IsChecked == true ||
        RenameEngine.TemplateUses(TemplateBox.Text, "n");

    private bool TryReadRules(out RenameOptions options)
    {
        var start = 1;
        var digits = 3;
        options = new RenameOptions();

        if (UsesNumbering && (!int.TryParse(StartBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
            !int.TryParse(DigitsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out digits) ||
            digits is < 1 or > 12))
        {
            SetStatus("Introduce un número inicial desde 0 y entre 1 y 12 dígitos.", true);
            return false;
        }

        options = new RenameOptions(
            Search: SearchBox.Text,
            Replacement: ReplacementBox.Text,
            Regex: RegexCheck.IsChecked == true,
            MatchCase: MatchCaseCheck.IsChecked == true,
            ReplaceAll: AllMatchesCheck.IsChecked == true,
            EditExtension: ExtensionCheck.IsChecked == true,
            Template: TemplateBox.Text,
            Casing: (TextCase)Math.Max(CaseBox.SelectedIndex, 0),
            Number: NumberCheck.IsChecked == true,
            Start: start,
            Digits: digits);
        return true;
    }

    private string? _previewNotice;

    private void RequestPreview(string? notice = null)
    {
        if (!_ready || _busy)
            return;

        CancelPreview();
        _previewNotice = notice;
        UpdateButtons();

        if (_files.Count == 0)
        {
            SetStatus(notice ?? "Añade archivos para preparar una tanda.");
            return;
        }

        SetStatus("Actualizando vista previa…");
        _previewTimer.Start();
    }

    private async void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (!TryReadRules(out var options))
        {
            ShowCurrentNames();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var notice = _previewNotice;
        try
        {
            if (ExifFields.IsUsedIn(options.Template) && _files.Any(file => file.Metadata is null))
            {
                if (ExifMetadataReader.IsAvailable)
                    await LoadBatchMetadataAsync(cancellation.Token);
                else
                    notice = "No se encontró ExifTool para leer metadatos.";
            }

            cancellation.Token.ThrowIfCancellationRequested();
            var snapshot = _files.ToArray();
            var preview = await Task.Run(() => RenameEngine.Preview(snapshot, options, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ShowRows(preview);
            _previewReady = true;
            var ready = preview.Count(row => row.Status == RenameStatus.Ready);
            var errors = preview.Count(row => row.Status == RenameStatus.Error);
            if (errors > 0)
            {
                var first = preview.First(row => row.Status == RenameStatus.Error).Detail;
                SetStatus(notice ?? first, true);
            }
            else if (ready > 0)
            {
                SetStatus(notice ?? (ready == 1 ? "1 archivo listo para renombrar." : $"{ready} archivos listos para renombrar."));
            }
            else
            {
                SetStatus(notice ?? "Ningún nombre cambia con las reglas actuales.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowCurrentNames();
            SetStatus(ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;
            UpdateButtons();
        }
    }

    private async Task LoadBatchMetadataAsync(CancellationToken cancellationToken)
    {
        var missing = Enumerable.Range(0, _files.Count).Where(index => _files[index].Metadata is null).ToArray();
        var processed = 0;
        foreach (var batch in missing.Chunk(MetadataBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = batch.Where(index => _files[index].Metadata is null).ToArray();
            if (pending.Length > 0)
            {
                SetStatus($"Leyendo metadatos · {processed} de {missing.Length}…");
                var paths = pending.Select(index => _files[index].FullPath).ToArray();
                var result = await ExifMetadataReader.ReadAsync(paths, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var index in pending)
                {
                    var file = _files[index];
                    _files[index] = file with { Metadata = result.GetValueOrDefault(file.FullPath) ?? EmptyMetadata };
                }
                RefreshInspector();
            }
            processed += batch.Length;
        }
    }

    private void OnExifToggle(object sender, RoutedEventArgs e) => RefreshInspector();

    private void RefreshInspector()
    {
        if (!_ready || _updatingGrid)
            return;

        var file = !_busy && ExifExpander.IsExpanded && FilesGrid.SelectedItem is DisplayRow selected
            ? _files.Find(item => PathComparer.Equals(item.FullPath, selected.Row.File.FullPath))
            : null;

        if (!PathComparer.Equals(_inspectorPath, file?.FullPath))
        {
            CancelInspector();
            _inspectorPath = file?.FullPath;
        }

        if (file is null)
        {
            ExifFileName.Text = "";
            ExifFileName.ToolTip = null;
            ExifValueList.ItemsSource = null;
            InspectorStatus.Text = "Selecciona una foto para ver sus datos.";
            return;
        }

        ExifFileName.Text = Path.GetFileName(file.FullPath);
        ExifFileName.ToolTip = file.FullPath;
        if (file.Metadata is not null)
        {
            RenderMetadata(file.Metadata);
        }
        else if (ExifMetadataReader.IsAvailable)
        {
            ExifValueList.ItemsSource = null;
            InspectorStatus.Text = "Leyendo metadatos…";
            if (_inspectorCancellation is null)
                _inspectorTimer.Start();
        }
        else
        {
            ExifValueList.ItemsSource = null;
            InspectorStatus.Text = "Falta ExifTool. Añádelo para consultar los metadatos.";
        }
    }

    private async void OnInspectorTick(object? sender, EventArgs e)
    {
        _inspectorTimer.Stop();
        if (_inspectorPath is not { } path)
            return;

        var file = _files.Find(item => PathComparer.Equals(item.FullPath, path));
        if (file is null)
            return;
        if (file.Metadata is not null)
        {
            RenderMetadata(file.Metadata);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _inspectorCancellation = cancellation;
        try
        {
            var metadata = await ExifMetadataReader.ReadAsync([path], cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var index = _files.FindIndex(item => PathComparer.Equals(item.FullPath, path));
            if (index >= 0)
            {
                var value = _files[index].Metadata ?? metadata.GetValueOrDefault(path) ?? EmptyMetadata;
                _files[index] = _files[index] with { Metadata = value };
                RenderMetadata(value);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                InspectorStatus.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_inspectorCancellation, cancellation))
                _inspectorCancellation = null;
        }
    }

    private void RenderMetadata(FileMetadata metadata)
    {
        var fields = ExifFields.All.Select(field => new ExifDisplay(field.Token, field.Label, field.ReadValue(metadata)))
            .Where(field => !string.IsNullOrWhiteSpace(field.Value)).ToArray();
        ExifValueList.ItemsSource = fields;
        InspectorStatus.Text = fields.Length == 0
            ? "Este archivo no tiene metadatos que se puedan usar en la plantilla."
            : "Pulsa un dato para añadirlo a la plantilla.";
    }

    private void CancelInspector()
    {
        _inspectorTimer.Stop();
        _inspectorCancellation?.Cancel();
        _inspectorCancellation = null;
    }

    private void CancelPreview()
    {
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        _previewReady = false;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled)
            return;

        var preview = _preview;
        SetBusy(true);
        SetStatus("Renombrando archivos…");
        var failed = false;
        try
        {
            var moves = await Task.Run(() => RenameEngine.Apply(preview));
            _history.Push(moves);
            UpdatePaths(moves.ToDictionary(move => move.Source, move => move.Destination, PathComparer));
            ResetRules();
            ShowCurrentNames();
            SetStatus(moves.Count == 1 ? "Se renombró 1 archivo." : $"Se renombraron {moves.Count} archivos.");
        }
        catch (Exception ex)
        {
            failed = true;
            ShowError("Error al renombrar", ex.Message);
        }
        finally
        {
            _previewReady = false;
            SetBusy(false);
        }

        if (failed)
            RequestPreview();
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (!UndoButton.IsEnabled || !_history.TryPeek(out var moves))
            return;

        CancelPreview();
        SetBusy(true);
        SetStatus("Restaurando nombres originales…");
        var failed = false;
        try
        {
            await Task.Run(() => RenameEngine.Undo(moves));
            _history.Pop();
            UpdatePaths(moves.ToDictionary(move => move.Destination, move => move.Source, PathComparer));
            ResetRules();
            ShowCurrentNames();
            SetStatus(moves.Count == 1 ? "Se restauró 1 nombre." : $"Se restauraron {moves.Count} nombres.");
        }
        catch (Exception ex)
        {
            failed = true;
            ShowError("Error al deshacer", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (failed)
            RequestPreview();
    }

    private void UpdatePaths(IReadOnlyDictionary<string, string> names)
    {
        for (var index = 0; index < _files.Count; index++)
        {
            var file = _files[index];
            if (names.TryGetValue(file.FullPath, out var newPath))
                _files[index] = file with { FullPath = newPath };
        }
    }

    private void ShowCurrentNames() => ShowRows(_files.Select(file =>
        new RenameRow(file, Path.GetFileName(file.FullPath), RenameStatus.Unchanged, "")).ToArray());

    private void ShowRows(IReadOnlyList<RenameRow> rows)
    {
        var selected = SelectedPaths();
        _preview = rows;
        var display = rows.Select(row => new DisplayRow(row)).ToArray();
        _updatingGrid = true;
        try
        {
            FilesGrid.ItemsSource = display;
            foreach (var row in display)
            {
                if (selected.Contains(row.Row.File.FullPath))
                    FilesGrid.SelectedItems.Add(row);
            }
            if (FilesGrid.SelectedItems.Count == 0 && display.Length > 0)
                FilesGrid.SelectedIndex = 0;
        }
        finally
        {
            _updatingGrid = false;
        }

        var ready = rows.Count(row => row.Status == RenameStatus.Ready);
        var errors = rows.Count(row => row.Status == RenameStatus.Error);
        var counts = new List<string> { rows.Count == 1 ? "1 archivo" : $"{rows.Count} archivos" };
        if (ready > 0)
            counts.Add(ready == 1 ? "1 cambio" : $"{ready} cambios");
        if (errors > 0)
            counts.Add(errors == 1 ? "1 error" : $"{errors} errores");
        CountText.Text = string.Join(" · ", counts);
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesGrid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = FilesGrid.Visibility;
        UpdateButtons();
        RefreshInspector();
    }

    private HashSet<string> SelectedPaths() => FilesGrid.SelectedItems.OfType<DisplayRow>()
        .Select(row => row.Row.File.FullPath).ToHashSet(PathComparer);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
        RefreshInspector();
    }

    private void UpdateButtons()
    {
        if (!_ready)
            return;

        FilesToolbar.IsEnabled = !_busy;
        RulesPanel.IsEnabled = !_busy;
        NumberPanel.IsEnabled = UsesNumbering;
        InspectorPanel.IsEnabled = !_busy;
        SavePresetButton.IsEnabled = _presetProblem is null && !string.IsNullOrWhiteSpace(TemplateBox.Text);
        RemoveButton.IsEnabled = FilesGrid.SelectedItems.Count > 0;
        ClearButton.IsEnabled = _files.Count > 0;
        UndoButton.IsEnabled = !_busy && _history.Count > 0;

        var changes = _previewReady ? _preview.Count(row => row.Status == RenameStatus.Ready) : 0;
        ApplyButton.IsEnabled = !_busy && changes > 0 && !_preview.Any(row => row.Status == RenameStatus.Error);
        ApplyButton.Content = changes == 0 ? "Renombrar" : $"Renombrar ({changes})";
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? ErrorColor : NeutralColor;
    }

    private void ShowError(string title, string detail)
    {
        SetStatus(detail, true);
        MessageBox.Show(this, detail, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_busy)
            e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelPreview();
        CancelInspector();
        base.OnClosed(e);
    }

    private sealed record ExifDisplay(string Token, string Label, string Value)
    {
        public string Insertion => $"{{{Token}}}";
        public string Hint => $"{Label} · {Value} · Insertar {Insertion}";
    }

    private sealed record DisplayRow(RenameRow Row)
    {
        public string OriginalName => Path.GetFileName(Row.File.FullPath);
        public string StatusText => Row.Status switch
        {
            RenameStatus.Ready => "Listo",
            RenameStatus.Error => string.IsNullOrWhiteSpace(Row.Detail) ? "Error" : Row.Detail,
            _ => "Sin cambios"
        };
        public Brush StatusColor => Row.Status switch
        {
            RenameStatus.Ready => ReadyColor,
            RenameStatus.Error => ErrorColor,
            _ => NeutralColor
        };
    }
}
