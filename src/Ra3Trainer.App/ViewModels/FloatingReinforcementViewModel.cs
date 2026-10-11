using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Ra3Trainer.Core.Features;

namespace Ra3Trainer.App.ViewModels;

/// <summary>Float-only navigation; game parameters and dispatch remain owned by the shared VM.</summary>
public sealed class FloatingReinforcementViewModel : ViewModelBase, IDisposable
{
    // ★ 用户 2026-10-07：「分类左边固定住，没必要点一下单位就消失」★
    // 所以选兵区**常驻**、分类列**常驻**：左列一直是分类，右列一直是当前分类下的兵种。
    // 以前是"二选一"的开关（ShowCategories / HasCategory 互为反），点一下分类左边那一列就整块消失 ——
    // 选了分类反而看不到分类，完全说不通。
    // 常驻之后 IsChoosing 就是**死状态**（永远是 true、没人能改），OCR 复核点名了这一点 ⇒ 整个删掉，
    // XAML 里那个 Visibility 绑定也一起去掉：少一个"看起来能关、其实关不掉"的开关。
    private bool _disposed;
    private string? _selectedCategory;
    private ReinforcementUnitEntry? _selectedUnit;
    private string _feedback = "快捷键只设置数量，点呼叫增援才发兵。";

    public string CountEvidenceText => $"实测上限：证据不足。1–{ReinforcementSettings.MaxCount} 仅旧参数范围，非已验证容量；51–100 未开放。";

    public FloatingReinforcementViewModel(MainViewModel main)
    {
        Main = main;
        CallCommand = main.Groups.SelectMany(group => group.Features)
            .Single(feature => feature.Feature.RawName == "We Need Back").Command;
        // 「选择单位」是**进入**选择，不是开关：默认已经展开时再按一下不该把它关掉
        // （点开关会让人误以为要关；关的时机只有一个 —— 选中兵种后自动收，见 CommitUnitCommand）。
        // 「选择单位」= 把左列跳回**当前兵种所在的分类**（常用兵一眼找到）。选兵区本来就在，不是展开/收起。
        ToggleUnitsCommand = new RelayCommand(() =>
        {
            SelectedCategory = Main.SelectedReinforcementUnit?.Category;
        });
        // 点一个兵种就是选中它；**选兵区不收**（用户 2026-10-07：点一下不该让分类列消失）。
        // 反馈在上面的「兵种」行：名字立刻换成刚点的这个。
        CommitUnitCommand = new RelayCommand(() =>
        {
            if (SelectedUnit is null) return;
            Main.SelectedReinforcementUnit = SelectedUnit;
        });
        DecreaseCountCommand = new RelayCommand(() => Adjust(false, -1), () => CanAdjust(false, -1));
        IncreaseCountCommand = new RelayCommand(() => Adjust(false, 1), () => CanAdjust(false, 1));
        SetCountCommand = new RelayCommand(value =>
        {
            if (!int.TryParse(value?.ToString(), out var count)) return;
            if (count < ReinforcementSettings.MinCount || count > ReinforcementSettings.MaxCount)
            {
                Feedback = $"{count} 个尚未开放：缺批次完成数量及帧耗时证据；数量未改。";
                return;
            }
            Main.ReinforcementCountText = count.ToString();
        });
        WheelCountCommand = new RelayCommand(value => Adjust(false, Convert.ToInt32(value) * 5));
        DecreaseRankCommand = new RelayCommand(() => Adjust(true, -1), () => CanAdjust(true, -1));
        IncreaseRankCommand = new RelayCommand(() => Adjust(true, 1), () => CanAdjust(true, 1));
        Main.ReinforcementUnits.CollectionChanged += OnCatalogChanged;
        Main.PropertyChanged += OnMainChanged;
        RefreshCatalog();
    }

    public MainViewModel Main { get; }
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<ReinforcementUnitEntry> Units { get; } = new();
    public RelayCommand CallCommand { get; }
    public RelayCommand ToggleUnitsCommand { get; }
    public RelayCommand CommitUnitCommand { get; }
    public RelayCommand DecreaseCountCommand { get; }
    public RelayCommand IncreaseCountCommand { get; }
    public RelayCommand SetCountCommand { get; }
    public RelayCommand WheelCountCommand { get; }
    public RelayCommand DecreaseRankCommand { get; }
    public RelayCommand IncreaseRankCommand { get; }
    public string Feedback { get => _feedback; private set { _feedback = value; OnPropertyChanged(); } }
    /// <summary>右列有没有东西可列：选了分类才列兵种（左列分类永远在）。</summary>
    public bool HasCategory => SelectedCategory is not null;
    public string? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            _selectedCategory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCategory));
            RefreshUnits();
        }
    }
    // Keep browsing selection separate: filtering must never clear the shared game parameter.
    public ReinforcementUnitEntry? SelectedUnit
    {
        get => _selectedUnit;
        set { _selectedUnit = value; OnPropertyChanged(); }
    }

    private void RefreshUnits()
    {
        Units.Clear();
        foreach (var unit in Main.ReinforcementUnits.Where(unit => unit.Category == SelectedCategory))
            Units.Add(unit);
        SelectedUnit = Units.FirstOrDefault(unit => unit.Code == Main.SelectedReinforcementUnit?.Code);
    }

    private void RefreshCatalog()
    {
        var category = SelectedCategory;
        Categories.Clear();
        foreach (var name in Main.ReinforcementUnits.Select(unit => unit.Category).Distinct())
            Categories.Add(name);
        // 没选过分类就落在**当前兵种所在的分类**上：右列一进页就有内容，
        // 而不是空着一块 + 一句"左边先点一个分类"（分类列本来就是常驻的，默认停在他的兵上最省事）。
        SelectedCategory = category is not null && Categories.Contains(category)
            ? category
            : Main.SelectedReinforcementUnit?.Category is { } current && Categories.Contains(current) ? current : null;
    }

    private void OnCatalogChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;      // 已经排队在 dispatcher 上的通知不许再改一个已释放的 VM
        RefreshCatalog();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(Main.SelectedReinforcementUnit))
            SelectedUnit = Units.FirstOrDefault(unit => unit.Code == Main.SelectedReinforcementUnit?.Code);
        if (args.PropertyName is nameof(Main.ReinforcementCountText) or nameof(Main.ReinforcementRankText))
        {
            DecreaseCountCommand.RaiseCanExecuteChanged();
            IncreaseCountCommand.RaiseCanExecuteChanged();
            DecreaseRankCommand.RaiseCanExecuteChanged();
            IncreaseRankCommand.RaiseCanExecuteChanged();
            Feedback = $"数量 {Main.ReinforcementCountText} · 星级 {Main.ReinforcementRankText}；点呼叫增援才发兵。";
        }
    }

    private bool CanAdjust(bool rank, int delta)
    {
        var text = rank ? Main.ReinforcementRankText : Main.ReinforcementCountText;
        var min = rank ? 0 : 1;
        var max = rank ? ReinforcementSettings.MaxRank : ReinforcementSettings.MaxCount;
        // 非法/超范围的输入**不放行**：以前返回 true，按钮亮着、点一下会"顺手帮你校正"一个你没要的值
        // （OCR 复核抓到）。禁用比悄悄改用户的数诚实 —— 输入框里就是他自己敲的东西。
        if (!int.TryParse(text, out var current) || current < min || current > max) return false;
        return delta < 0 ? current > min : current < max;
    }

    private void Adjust(bool rank, int delta)
    {
        var text = rank ? Main.ReinforcementRankText : Main.ReinforcementCountText;
        var min = rank ? 0 : 1;
        var max = rank ? ReinforcementSettings.MaxRank : ReinforcementSettings.MaxCount;
        var valid = int.TryParse(text, out var current) && current >= min && current <= max;
        // 输入读不成合法值时按**文档默认值**校正（数量 8 / 星级 3），再按按键方向走一步 ——
        // 原来写的是 Math.Clamp(current=0, …)：星级那条 min 就是 0，于是"按 + 校正为 0"，看着像死键（OCR 复核抓到）。
        var basis = ReinforcementSettings.DefaultCount;
        if (rank) basis = ReinforcementSettings.DefaultRank;
        var next = valid ? Math.Clamp(current + delta, min, max) : Math.Clamp(basis + delta, min, max);
        if (rank) Main.ReinforcementRankText = next.ToString();
        else Main.ReinforcementCountText = next.ToString();
        var label = rank ? "星级" : "数量";
        Feedback = !valid ? $"{label}已校正为 {next}（范围 {min}–{max}）。"
            : next == current ? $"{label}已到{(delta < 0 ? "下" : "上")}限 {next}。"
            : $"{label}已设为 {next}；点击「呼叫增援（J）」才会发兵。";
    }

    public void Dispose()
    {
        if (_disposed) return;      // 同一实例会被 dispose 两次（FloatingWindow 的 catch 块 + OnClosed）
        _disposed = true;
        Main.ReinforcementUnits.CollectionChanged -= OnCatalogChanged;
        Main.PropertyChanged -= OnMainChanged;
    }
}
