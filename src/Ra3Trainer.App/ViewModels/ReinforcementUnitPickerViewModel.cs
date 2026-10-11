using System.Collections.ObjectModel;
using Microsoft.Win32;
using Ra3Trainer.Core.Features;

namespace Ra3Trainer.App.ViewModels;

public sealed class ReinforcementUnitPickerViewModel : ViewModelBase
{
    private readonly List<ReinforcementUnitEntry> _units;
    private readonly List<ReinforcementUnitEntry> _baseUnits;
    private readonly List<ReinforcementUnitEntry> _imported = new();
    private readonly IReadOnlyList<ReinforcementUnitEntry> _props = ReinforcementUnitCatalog.LoadBuiltInProps();
    private string _searchText = string.Empty;
    private string _selectedCategory = AllCategories;
    private string _selectedSource = AllSources;
    private bool _showProps;
    private bool _showAllModUnits;
    private readonly UnitScope _scope = UnitScope.Permissive;
    private ReinforcementUnitEntry? _selectedUnit;
    private string _statusMessage;

    public ReinforcementUnitPickerViewModel(
        IEnumerable<ReinforcementUnitEntry> units,
        UnitScope? scope = null,
        bool showAllModUnits = false)
    {
        _scope = scope ?? UnitScope.Permissive;
        _showAllModUnits = showAllModUnits;
        _baseUnits = ReinforcementUnitCatalog.Merge(Array.Empty<ReinforcementUnitEntry>(), units).ToList();
        _units = new List<ReinforcementUnitEntry>(_baseUnits);
        _statusMessage = $"共 {_units.Count} 条单位码。";
        FilteredUnits = new ObservableCollection<ReinforcementUnitEntry>();
        RebuildSelectors();
        System.Windows.Data.CollectionViewSource.GetDefaultView(FilteredUnits)
            .GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ReinforcementUnitEntry.Category)));
        ConfirmCommand = new RelayCommand(Confirm, () => SelectedUnit is not null);
        CancelCommand = new RelayCommand(Cancel);
        ImportCommand = new RelayCommand(ImportFromFile);
        RefreshFilter();
    }

    public event Action<bool?>? RequestClose;

    public ObservableCollection<ReinforcementUnitEntry> FilteredUnits { get; }

    /// <summary>"全部" + 按阵营 · 类型去重后的分类（官方库 630 条 → 十来个分类）。</summary>
    public ObservableCollection<string> Categories { get; } = new();

    public const string AllCategories = "全部分类";

    public const string AllSources = "全部来源";

    /// <summary>是否把地图杂物（箱子/信标/中立设施/平民建筑）也列出来，默认关。</summary>
    public bool ShowProps
    {
        get => _showProps;
        set
        {
            if (_showProps == value)
            {
                return;
            }

            _showProps = value;
            OnPropertyChanged();
            RebuildUnitSet();
        }
    }

    /// <summary>
    /// 是否放出**其他 mod** 的单位（默认关）：关 = 只显当前 mod + 原版通用；开 = 全部（带来源标注）。
    /// 与面板【选单位】同一个口径（<see cref="UnitModScope"/>）。
    /// </summary>
    public bool ShowAllModUnits
    {
        get => _showAllModUnits;
        set
        {
            if (_showAllModUnits == value)
            {
                return;
            }

            _showAllModUnits = value;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    /// <summary>来源筛选：通用（换 mod 也有效）/ 日冕（神州专属）/ 待核对。</summary>
    public ObservableCollection<string> ModSources { get; } = new();

    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? AllSources : value;
            if (string.Equals(_selectedSource, next, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSource = next;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? AllCategories : value;
            if (string.Equals(_selectedCategory, next, StringComparison.Ordinal))
            {
                return;
            }

            _selectedCategory = next;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public IReadOnlyList<ReinforcementUnitEntry> Units => _units;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = value ?? string.Empty;
            OnPropertyChanged();
            RefreshFilter();
        }
    }

    public ReinforcementUnitEntry? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (EqualityComparer<ReinforcementUnitEntry?>.Default.Equals(_selectedUnit, value))
            {
                return;
            }

            _selectedUnit = value;
            OnPropertyChanged();
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(_statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public RelayCommand ImportCommand { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void LoadFromFile(string path)
    {
        var imported = ReinforcementUnitCatalog.Load(path);
        if (imported.Count == 0)
        {
            StatusMessage = "没有读取到可用单位码。";
            return;
        }

        var beforeCount = _units.Count;
        foreach (var entry in imported)
        {
            if (_units.Any(item => item.Code == entry.Code))
            {
                continue;
            }

            _imported.Add(entry);
            _units.Add(entry);
        }

        RebuildSelectors();
        RefreshFilter();
        var addedCount = _units.Count - beforeCount;
        StatusMessage = addedCount > 0
            ? $"已导入 {addedCount} 条单位码，当前共 {_units.Count} 条。"
            : $"导入完成，未增加新条目，当前共 {_units.Count} 条。";
    }

    private void ImportFromFile()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择单位代码文件",
                Filter = "文本文件 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFromFile(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入单位代码失败：{ex.Message}";
        }
    }

    private void RebuildUnitSet()
    {
        // 只换"列表内容"，绝不清空下拉项的集合 —— 清空会把两个筛选下拉的选中项一并抹掉（真踩过）
        var next = ReinforcementUnitCatalog.Merge(
            ReinforcementUnitCatalog.Merge(_baseUnits, _imported),
            ShowProps ? _props : Array.Empty<ReinforcementUnitEntry>());

        _units.Clear();
        _units.AddRange(next);
        RefreshFilter();
        StatusMessage = ShowProps
            ? $"已显示地图杂物（+{_props.Count} 条）。"
            : "已隐藏地图杂物。";
    }

    private void RebuildSelectors()
    {
        // 一次把"全部可能的分类/来源"都建出来（含杂物，即使当前不显示），
        // 这样开关切换时不用清空集合，ComboBox 的选中项自然不会被清成空白。
        var all = ReinforcementUnitCatalog.Merge(_baseUnits, _props);

        ModSources.Clear();
        ModSources.Add(AllSources);
        foreach (var source in all
            .Select(unit => unit.ModSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.Ordinal))
        {
            ModSources.Add(source);
        }

        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (var category in all
            .Select(unit => unit.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal))
        {
            Categories.Add(category);
        }

    }

    private void RefreshFilter()
    {
        var filtered = ReinforcementUnitCatalog.Filter(_units, SearchText)
            .Where(unit => ShowAllModUnits || _scope.Allows(unit.ModSource))
            .Where(unit => SelectedCategory == AllCategories || string.Equals(unit.Category, SelectedCategory, StringComparison.Ordinal))
            .Where(unit => SelectedSource == AllSources || string.Equals(unit.ModSource, SelectedSource, StringComparison.Ordinal))
            .ToList();

        var previouslySelected = SelectedUnit;

        FilteredUnits.Clear();
        foreach (var unit in filtered)
        {
            FilteredUnits.Add(unit);
        }

        if (previouslySelected is not null && !FilteredUnits.Contains(previouslySelected))
        {
            SelectedUnit = null;
        }

        StatusMessage = SelectedCategory == AllCategories
            ? $"共 {FilteredUnits.Count} 条（清单 {_units.Count} 条，已分类）。"
            : $"{SelectedCategory}：{FilteredUnits.Count} 条。";
    }

    private void Confirm()
    {
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
