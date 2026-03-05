using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace BiomeDatasetCollector.Content;

public sealed class DatasetUiSystem : ModSystem
{
    private UserInterface? _userInterface;
    private DatasetUiState? _uiState;
    private bool _pendingOpenOnWorldLoad;

    internal static DatasetUiSystem? Instance { get; private set; }

    public static bool IsVisible => Instance?._userInterface?.CurrentState is not null;

    public override void Load()
    {
        if (Main.dedServ)
        {
            return;
        }

        Instance = this;
        _userInterface = new UserInterface();
        _uiState = null;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        if (_pendingOpenOnWorldLoad)
        {
            _pendingOpenOnWorldLoad = false;
            try
            {
                SetVisible(true);
            }
            catch (Exception ex)
            {
                try
                {
                    ModContent.GetInstance<BiomeDatasetCollector>().Logger.Warn($"Failed to open dataset panel on world load: {ex.Message}");
                }
                catch
                {
                }
            }
        }

        _userInterface?.Update(gameTime);
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text", StringComparison.Ordinal));
        if (mouseTextIndex < 0)
        {
            return;
        }

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "BiomeDatasetCollector: DatasetPanel",
            () =>
            {
                if (_userInterface?.CurrentState is not null)
                {
                    _userInterface.Draw(Main.spriteBatch, new GameTime());
                }

                return true;
            },
            InterfaceScaleType.UI));
    }

    public static void TogglePanel()
    {
        SetVisible(!IsVisible);
    }

    public static void NotifyDatasetMutated()
    {
        DatasetUiState.MarkDatasetDirty();
    }

    public static void NotifyCaptureSaved(string biome)
    {
        if (string.IsNullOrWhiteSpace(biome))
        {
            return;
        }

        Instance?._uiState?.ApplyOptimisticCapture(biome.Trim());
    }

    public static void SetVisible(bool visible)
    {
        if (Instance?._userInterface is null)
        {
            return;
        }

        try
        {
            if (visible && !Instance.EnsureUiState())
            {
                return;
            }

            Instance._userInterface.SetState(visible ? Instance._uiState : null);
            if (visible)
            {
                Instance._uiState?.RefreshImmediate();
            }
        }
        catch (Exception ex)
        {
            try
            {
                ModContent.GetInstance<BiomeDatasetCollector>().Logger.Warn($"Failed to change dataset panel visibility: {ex.Message}");
            }
            catch
            {
            }

            try
            {
                Instance._userInterface.SetState(null);
            }
            catch
            {
            }
        }
    }

    public override void OnWorldLoad()
    {
        if (Main.dedServ)
        {
            return;
        }

        bool openOnWorldLoad = true;
        try
        {
            openOnWorldLoad = ModContent.GetInstance<CaptureConfig>().OpenStatusPanelOnWorldLoad;
        }
        catch
        {
        }

        if (openOnWorldLoad)
        {
            _pendingOpenOnWorldLoad = true;
        }
    }

    public override void OnWorldUnload()
    {
        _pendingOpenOnWorldLoad = false;
        SetVisible(false);
    }

    public override void Unload()
    {
        _userInterface = null;
        _uiState = null;
        _pendingOpenOnWorldLoad = false;
        Instance = null;
    }

    private bool EnsureUiState()
    {
        if (_uiState is not null)
        {
            return true;
        }

        _uiState = new DatasetUiState();
        _uiState.Activate();
        return true;
    }
}

public sealed class DatasetUiState : UIState
{
    private const int StatusRefreshIntervalTicks = 300;
    private const int BiomeRows = 7;

    private static int _datasetDirtyVersion;

    private UIPanel? _panel;
    private bool _dragging;
    private Vector2 _dragOffset;

    private UIText? _title;
    private UITextPanel<string>? _closeButton;
    private UIText? _summaryLine;
    private UIText? _biomeHeaderLine;
    private readonly UIText?[] _biomeCountLines = new UIText?[BiomeRows];
    private UIText? _hintLinePrimary;
    private UIText? _hintLineSecondary;

    private UITextPanel<string>? _statusButton;
    private UITextPanel<string>? _zipButton;
    private UITextPanel<string>? _mergeLatestButton;
    private UITextPanel<string>? _cleanButton;
    private UITextPanel<string>? _syncButton;
    private UITextPanel<string>? _cleanConfirmButton;

    private int _refreshTimer;
    private int _appliedDirtyVersion;
    private int _optimisticCaptureVersion;
    private int _snapshotTaskOptimisticVersion;
    private Task<DatasetStatusSnapshot>? _snapshotTask;
    private DatasetStatusSnapshot _snapshot = DatasetStatusSnapshot.Missing(string.Empty);

    internal static void MarkDatasetDirty()
    {
        Interlocked.Increment(ref _datasetDirtyVersion);
    }

    internal void ApplyOptimisticCapture(string biome)
    {
        _optimisticCaptureVersion++;
        _snapshot = _snapshot.WithOptimisticCapture(biome);
        ApplyLocalizedTextAndSnapshot();
    }

    public override void OnInitialize()
    {
        _panel = new UIPanel
        {
            BackgroundColor = new Color(22, 34, 64) * 0.9f,
            BorderColor = new Color(80, 116, 193) * 0.95f,
        };
        _panel.Width.Set(420f, 0f);
        _panel.Height.Set(482f, 0f);
        _panel.Left.Set(24f, 0f);
        _panel.Top.Set(220f, 0f);
        _panel.SetPadding(10f);
        Append(_panel);

        _appliedDirtyVersion = Volatile.Read(ref _datasetDirtyVersion);

        UIElement dragBar = new();
        dragBar.Width.Set(0f, 1f);
        dragBar.Height.Set(30f, 0f);
        dragBar.OnLeftMouseDown += OnDragStart;
        dragBar.OnLeftMouseUp += OnDragEnd;
        _panel.Append(dragBar);

        _title = new UIText(string.Empty);
        _title.Left.Set(8f, 0f);
        _title.Top.Set(6f, 0f);
        _title.TextColor = new Color(245, 245, 255);
        dragBar.Append(_title);

        _closeButton = new UITextPanel<string>(string.Empty, 0.8f, false);
        _closeButton.Width.Set(50f, 0f);
        _closeButton.Height.Set(24f, 0f);
        _closeButton.Left.Set(-56f, 1f);
        _closeButton.Top.Set(3f, 0f);
        _closeButton.OnLeftClick += (_, _) => DatasetUiSystem.TogglePanel();
        dragBar.Append(_closeButton);

        _summaryLine = CreateStatusLine(54f, new Color(214, 232, 255));
        _biomeHeaderLine = CreateStatusLine(82f, new Color(224, 232, 246));
        for (int row = 0; row < _biomeCountLines.Length; row++)
        {
            _biomeCountLines[row] = CreateStatusLine(106f + (row * 20f), new Color(224, 224, 234));
        }

        _statusButton = CreateActionButton(0f, 0f, -3f, 0.5f, 254f, DatasetCommand.RunStatusFromUi);
        _zipButton = CreateActionButton(3f, 0.5f, -3f, 0.5f, 254f, DatasetCommand.RunZipFromUi);
        _mergeLatestButton = CreateActionButton(0f, 0f, -3f, 0.5f, 294f, MergeLatestArchive);
        _cleanButton = CreateActionButton(3f, 0.5f, -3f, 0.5f, 294f, DatasetCommand.RunCleanFromUi);
        _syncButton = CreateActionButton(0f, 0f, 0f, 1f, 334f, DatasetCommand.RunSyncFromUi);
        _cleanConfirmButton = CreateActionButton(0f, 0f, 0f, 1f, 374f, DatasetCommand.RunCleanConfirmFromUi, true);

        _hintLinePrimary = CreateStatusLine(424f, new Color(212, 212, 222));
        _hintLineSecondary = CreateStatusLine(444f, new Color(212, 212, 222));

    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (_panel is not null && _panel.ContainsPoint(Main.MouseScreen) && Main.LocalPlayer is not null)
        {
            Main.LocalPlayer.mouseInterface = true;
        }

        if (_dragging && _panel is not null)
        {
            _panel.Left.Set(Main.mouseX - _dragOffset.X, 0f);
            _panel.Top.Set(Main.mouseY - _dragOffset.Y, 0f);
            _panel.Recalculate();
        }

        if (!Main.mouseLeft)
        {
            _dragging = false;
        }

        if (_snapshotTask is not null && _snapshotTask.IsCompleted)
        {
            bool shouldApplySnapshot = _snapshotTaskOptimisticVersion == _optimisticCaptureVersion;
            try
            {
                if (shouldApplySnapshot)
                {
                    _snapshot = _snapshotTask.Result;
                }
            }
            catch (Exception ex)
            {
                if (shouldApplySnapshot)
                {
                    _snapshot = DatasetStatusSnapshot.FromError(CaptureConfig.ResolveOutputRootDirectory(), ex.Message);
                }
            }

            _snapshotTask = null;
            if (shouldApplySnapshot)
            {
                ApplyLocalizedTextAndSnapshot();
            }
            else
            {
                _refreshTimer = Math.Max(_refreshTimer, StatusRefreshIntervalTicks - 60);
            }
        }

        int currentDirtyVersion = Volatile.Read(ref _datasetDirtyVersion);
        if (currentDirtyVersion != _appliedDirtyVersion && _snapshotTask is null)
        {
            _appliedDirtyVersion = currentDirtyVersion;
            _refreshTimer = 0;
            StartSnapshotRefresh();
        }

        _refreshTimer++;
        if (_refreshTimer >= StatusRefreshIntervalTicks)
        {
            _refreshTimer = 0;
            StartSnapshotRefresh();
        }

        UpdateButtonStyles();
    }

    public void RefreshImmediate()
    {
        _refreshTimer = 0;
        StartSnapshotRefresh();
        ApplyLocalizedTextAndSnapshot();
    }

    private UIText CreateStatusLine(float top, Color color)
    {
        UIText text = new(string.Empty)
        {
            TextColor = color,
        };
        text.Left.Set(6f, 0f);
        text.Top.Set(top, 0f);
        _panel?.Append(text);
        return text;
    }

    private UITextPanel<string> CreateActionButton(
        float leftPixels,
        float leftPercent,
        float widthPixels,
        float widthPercent,
        float top,
        Action onClick,
        bool warning = false)
    {
        UITextPanel<string> button = new(string.Empty, 0.82f, false)
        {
            BorderColor = warning ? new Color(201, 107, 87) * 0.95f : new Color(109, 148, 230) * 0.9f,
            BackgroundColor = warning ? new Color(130, 62, 54) * 0.85f : new Color(60, 92, 162) * 0.82f,
        };
        button.Width.Set(widthPixels, widthPercent);
        button.Height.Set(34f, 0f);
        button.Left.Set(leftPixels, leftPercent);
        button.Top.Set(top, 0f);
        button.OnLeftClick += (_, _) =>
        {
            onClick();
            StartSnapshotRefresh();
        };
        _panel?.Append(button);

        if (warning)
        {
            button.TextColor = new Color(255, 234, 230);
        }

        return button;
    }

    private void StartSnapshotRefresh()
    {
        if (_snapshotTask is not null)
        {
            return;
        }

        _snapshotTaskOptimisticVersion = _optimisticCaptureVersion;
        _snapshotTask = Task.Run(BuildSnapshotSafe);
    }

    private static DatasetStatusSnapshot BuildSnapshotSafe()
    {
        string root = CaptureConfig.ResolveOutputRootDirectory();
        try
        {
            if (!Directory.Exists(root))
            {
                return DatasetStatusSnapshot.Missing(root);
            }

            Dictionary<string, int> biomeCounts = DatasetBiomes.CreateEmptyCounts();
            int totalImages = 0;
            long totalBytes = 0;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileInfo info = new(file);
                    totalBytes += info.Length;
                    if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        totalImages++;
                        string biome = ResolveBiomeFromFilePath(root, file);
                        if (biome.Length > 0)
                        {
                            biomeCounts.TryGetValue(biome, out int current);
                            biomeCounts[biome] = current + 1;
                        }
                    }
                }
                catch
                {
                }
            }

            int? csvRows = null;
            try
            {
                csvRows = CsvLogger.ReadAll().Count;
            }
            catch
            {
            }

            double totalMb = totalBytes / (1024d * 1024d);
            return DatasetStatusSnapshot.Ready(root, totalImages, csvRows, totalMb, biomeCounts);
        }
        catch (Exception ex)
        {
            return DatasetStatusSnapshot.FromError(root, ex.Message);
        }
    }

    private static string ResolveBiomeFromFilePath(string root, string filePath)
    {
        try
        {
            string relative = Path.GetRelativePath(root, filePath);
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0)
            {
                separator = relative.IndexOf(Path.AltDirectorySeparatorChar);
            }

            return separator <= 0 ? string.Empty : relative[..separator];
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ApplyLocalizedTextAndSnapshot()
    {
        _title?.SetText(T("Title"));
        _closeButton?.SetText(T("CloseButton"));
        _statusButton?.SetText(T("StatusButton"));
        _zipButton?.SetText(T("ZipButton"));
        _mergeLatestButton?.SetText(T("MergeLatestButton"));
        _cleanButton?.SetText(T("CleanButton"));
        _syncButton?.SetText(T("SyncButton"));
        _cleanConfirmButton?.SetText(T("CleanConfirmButton"));
        string captureKey = GetPrimaryAssignedKey(BiomeDatasetCollector.CaptureKeybind, "F9");
        string autoCaptureKey = GetPrimaryAssignedKey(BiomeDatasetCollector.ToggleAutoKeybind, "F10");
        _hintLinePrimary?.SetText(T("HintLinePrimary", captureKey, autoCaptureKey));
        _hintLineSecondary?.SetText(T("HintLineSecondary", "/dataset ui"));
        _biomeHeaderLine?.SetText(T("LiveBiomeHeader"));

        if (_snapshot.HasError)
        {
            _summaryLine?.SetText(T("LiveSummaryErrorLine", _snapshot.ErrorMessage));
            SetBiomeRows(T("LiveBiomeUnavailable"));
        }
        else if (!_snapshot.HasDataset)
        {
            _summaryLine?.SetText(T("LiveNoDatasetLine"));
            SetBiomeRows(T("LiveBiomeNone"));
        }
        else
        {
            string csvRowsText = _snapshot.CsvRows.HasValue
                ? _snapshot.CsvRows.Value.ToString(CultureInfo.InvariantCulture)
                : T("LiveCsvUnknown");
            _summaryLine?.SetText(T("LiveSummaryLine", _snapshot.TotalImages, csvRowsText, _snapshot.TotalMegabytes.ToString("F2", CultureInfo.InvariantCulture)));
            SetBiomeRows(BuildBiomeDisplayRows(_snapshot.BiomeCounts));
        }
    }

    private IEnumerable<string> BuildBiomeDisplayRows(IReadOnlyDictionary<string, int> counts)
    {
        List<string> entries = new(DatasetBiomes.Ordered.Length);
        foreach (string biome in DatasetBiomes.Ordered)
        {
            counts.TryGetValue(biome, out int count);
            entries.Add(T("LiveBiomeEntry", biome, count));
        }

        int row = 0;
        for (int i = 0; i < entries.Count && row < _biomeCountLines.Length; i += 2)
        {
            string left = entries[i];
            string right = i + 1 < entries.Count ? entries[i + 1] : string.Empty;
            yield return right.Length > 0 ? T("LiveBiomePairEntry", left, right) : left;
            row++;
        }
    }

    private void SetBiomeRows(string singleLine)
    {
        if (_biomeCountLines.Length == 0)
        {
            return;
        }

        _biomeCountLines[0]?.SetText(singleLine);
        for (int i = 1; i < _biomeCountLines.Length; i++)
        {
            _biomeCountLines[i]?.SetText(string.Empty);
        }
    }

    private void SetBiomeRows(IEnumerable<string> lines)
    {
        int index = 0;
        foreach (string line in lines)
        {
            if (index >= _biomeCountLines.Length)
            {
                break;
            }

            _biomeCountLines[index]?.SetText(line);
            index++;
        }

        for (; index < _biomeCountLines.Length; index++)
        {
            _biomeCountLines[index]?.SetText(string.Empty);
        }
    }

    private void UpdateButtonStyles()
    {
        UpdateButtonStyle(_closeButton, false, true);
        UpdateButtonStyle(_statusButton, false, false);
        UpdateButtonStyle(_zipButton, false, false);
        UpdateButtonStyle(_mergeLatestButton, false, false);
        UpdateButtonStyle(_cleanButton, true, false);
        UpdateButtonStyle(_syncButton, false, false);
        UpdateButtonStyle(_cleanConfirmButton, true, false);
    }

    private static void UpdateButtonStyle(UITextPanel<string>? button, bool warning, bool small)
    {
        if (button is null)
        {
            return;
        }

        bool hovered = button.IsMouseHovering;
        float bgScale = hovered ? 1f : 0.83f;
        float borderScale = hovered ? 1f : 0.9f;

        Color neutralBackground = small
            ? new Color(66, 80, 135)
            : new Color(60, 92, 162);
        Color neutralBorder = small
            ? new Color(112, 138, 230)
            : new Color(109, 148, 230);

        Color warningBackground = small
            ? new Color(131, 64, 55)
            : new Color(130, 62, 54);
        Color warningBorder = small
            ? new Color(208, 110, 88)
            : new Color(201, 107, 87);

        button.BackgroundColor = (warning ? warningBackground : neutralBackground) * bgScale;
        button.BorderColor = (warning ? warningBorder : neutralBorder) * borderScale;
    }

    private void MergeLatestArchive()
    {
        try
        {
            string searchDirectory = GetArchiveSearchDirectory();
            string latestArchive = FindLatestArchivePath(searchDirectory);
            if (latestArchive.Length == 0)
            {
                Main.NewText(T("NoArchiveFound", searchDirectory));
                return;
            }

            Main.NewText(T("MergeLatestSelected", latestArchive));
            DatasetCommand.RunMergeFromUi(latestArchive);
        }
        catch (Exception ex)
        {
            Main.NewText(T("UiOperationError", ex.Message));
        }
    }

    private static string GetArchiveSearchDirectory()
    {
        string captureRoot = CaptureConfig.ResolveOutputRootDirectory();
        return Directory.GetParent(Path.GetFullPath(captureRoot))?.FullName ?? captureRoot;
    }

    private static string FindLatestArchivePath(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        string[] candidates = Directory.GetFiles(directory, "BiomeCaptures_*.zip", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 0)
        {
            return string.Empty;
        }

        string bestPath = string.Empty;
        DateTime bestTime = DateTime.MinValue;
        foreach (string path in candidates)
        {
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            if (bestPath.Length == 0 || timestamp > bestTime)
            {
                bestPath = path;
                bestTime = timestamp;
            }
        }

        return bestPath;
    }

    private static string GetPrimaryAssignedKey(ModKeybind? keybind, string fallback)
    {
        if (keybind is null)
        {
            return fallback;
        }

        try
        {
            IList<string> assigned = keybind.GetAssignedKeys(InputMode.Keyboard);
            if (assigned.Count > 0 && !string.IsNullOrWhiteSpace(assigned[0]))
            {
                return assigned[0];
            }
        }
        catch
        {
        }

        return fallback;
    }

    private void OnDragStart(UIMouseEvent evt, UIElement listeningElement)
    {
        if (_panel is null)
        {
            return;
        }

        _dragging = true;
        _dragOffset = evt.MousePosition - new Vector2(_panel.Left.Pixels, _panel.Top.Pixels);
    }

    private void OnDragEnd(UIMouseEvent evt, UIElement listeningElement)
    {
        _dragging = false;
        _panel?.Recalculate();
    }

    private static string T(string key, params object[] args)
    {
        string fullKey = $"Mods.BiomeDatasetCollector.UI.Panel.{key}";
        return args.Length == 0 ? Language.GetTextValue(fullKey) : Language.GetTextValue(fullKey, args);
    }

    private readonly struct DatasetStatusSnapshot
    {
        private static readonly IReadOnlyDictionary<string, int> EmptyBiomeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public string RootPath { get; }

        public bool HasDataset { get; }

        public int TotalImages { get; }

        public int? CsvRows { get; }

        public double TotalMegabytes { get; }

        public IReadOnlyDictionary<string, int> BiomeCounts { get; }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public string ErrorMessage { get; }

        private DatasetStatusSnapshot(string rootPath, bool hasDataset, int totalImages, int? csvRows, double totalMegabytes, IReadOnlyDictionary<string, int>? biomeCounts, string errorMessage)
        {
            RootPath = rootPath ?? string.Empty;
            HasDataset = hasDataset;
            TotalImages = totalImages;
            CsvRows = csvRows;
            TotalMegabytes = totalMegabytes;
            BiomeCounts = biomeCounts ?? EmptyBiomeCounts;
            ErrorMessage = errorMessage ?? string.Empty;
        }

        public static DatasetStatusSnapshot Missing(string rootPath)
        {
            return new DatasetStatusSnapshot(rootPath, false, 0, null, 0d, DatasetBiomes.CreateEmptyCounts(), string.Empty);
        }

        public static DatasetStatusSnapshot Ready(string rootPath, int totalImages, int? csvRows, double totalMegabytes, IReadOnlyDictionary<string, int> biomeCounts)
        {
            return new DatasetStatusSnapshot(rootPath, true, totalImages, csvRows, totalMegabytes, biomeCounts, string.Empty);
        }

        public static DatasetStatusSnapshot FromError(string rootPath, string errorMessage)
        {
            return new DatasetStatusSnapshot(rootPath, false, 0, null, 0d, DatasetBiomes.CreateEmptyCounts(), errorMessage);
        }

        public DatasetStatusSnapshot WithOptimisticCapture(string biome)
        {
            if (string.IsNullOrWhiteSpace(biome))
            {
                return this;
            }

            Dictionary<string, int> nextCounts = DatasetBiomes.CreateEmptyCounts();
            foreach (KeyValuePair<string, int> pair in BiomeCounts)
            {
                nextCounts[pair.Key] = pair.Value;
            }

            nextCounts.TryGetValue(biome, out int current);
            nextCounts[biome] = current + 1;

            int nextTotalImages = HasDataset ? TotalImages + 1 : 1;
            int? nextCsvRows = CsvRows.HasValue ? CsvRows.Value + 1 : (HasDataset ? null : 1);
            string nextRoot = RootPath.Length > 0 ? RootPath : CaptureConfig.ResolveOutputRootDirectory();
            return new DatasetStatusSnapshot(nextRoot, true, nextTotalImages, nextCsvRows, TotalMegabytes, nextCounts, string.Empty);
        }
    }
}
