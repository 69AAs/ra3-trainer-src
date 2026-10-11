using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Ra3Trainer.App.ViewModels;

/// <summary>
/// 小键盘按键的**唯一语义**：数字和运算符。至于"这个数字键在这一层干什么"
/// 由 <c>MainViewModel</c> 的按键表决定 —— 面板上每一行都标着自己的键，
/// 按哪个键就执行哪一行，所以不需要"光标"这个概念。
///
/// 这样定义的好处是映射是**全定义域纯函数**：任何按键都只有一个含义，
/// 面板显示的键和真按下去生效的键不可能漂移（以前 Num1/3/7/9 是死键，
/// 按了没反应也没人告诉你）。
/// </summary>
public enum PanelKeyCommand
{
    None,
    Digit0,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
    Plus,
    Minus,
    Times,
    Divide,
    Dot
}

/// <summary>What a panel row does when it is the highlighted row.</summary>
public enum PanelRowKind
{
    /// <summary>Num5 runs this row's command.</summary>
    Action,
    /// <summary>Num+/- and Num*// adjust this row's number.</summary>
    Value,
    /// <summary>Num5 flips this row's setting.</summary>
    Toggle,
    /// <summary>Read-only status; Num5 does nothing (热键是唯一入口).</summary>
    Status,
    /// <summary>The 常驻 面板位置 line: Num8/2/4/6 move the panel by one pixel.</summary>
    Position
}

/// <summary>
/// One visible line of the in-game panel. Values are snapshots taken when the row
/// set is rebuilt, so the panel never reads a half-updated model.
/// </summary>
public sealed class PanelNavRow : ViewModelBase
{
    private bool _isCurrent;

    /// <summary>面板上这一行印的键（"1"/"5"/"+"/"." 等）。空 = 这一行不是按键行。</summary>
    public string KeyLabel { get; init; } = string.Empty;
    /// <summary>
    /// 行上"显示"用的键。KeyLabel 是**派发依据**（面板靠它决定哪个键归哪一行，不能动），
    /// 但口径 B（2026-10-06）行上不再印编号 ⇒ 纯数字的标签只用于派发、不显示。
    /// 数值行（数量/星级）同样**行上不挂键**（HANDOFF-2026-10-07 §3：数值行走「按 5 进调节态 ⇒ 4/6 减/加」，
    /// 不再像旧口径那样在行上印 Num+/- 、Num*//）。KeyLabel 仍保留，派发（KeyLabel.Contains）照旧。
    /// 上一轮我把 KeyLabel 直接清空，结果派发全断（只剩 7/9 有用），用户实机验证过。
    /// </summary>
    public string KeyDisplay =>
        IsAdjustable || (KeyLabel.Length == 1 && char.IsAsciiDigit(KeyLabel[0])) ? string.Empty : KeyLabel;

    /// <summary>这一行是不是"当前项"（导航高亮那一行）。面板用它加白底整行高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set { if (_isCurrent == value) return; _isCurrent = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 这一行**代表的正是当前选中的兵种**（收藏槽用）。和 <see cref="IsCurrent"/>（导航高亮）是两件事，
    /// 不能共用一个标志：IsCurrent 归导航器写（8/2 走到哪），IsCurrentUnit 归行集本身写（当前兵种坐在哪个槽）。
    /// 曾经两者共用一个字段 —— 导航高亮接管 IsCurrent 之后，"当前兵种所在的槽"就再也画不出来了。
    /// </summary>
    public bool IsCurrentUnit { get; init; }

    public PanelRowKind Kind { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public object? Payload { get; init; }
    public ICommand? Command { get; init; }

    /// <summary>
    /// Num+/- 改数量、Num*// 改星级的数值行。**行上不再印自己的键**（KeyDisplay 对可调数值行返回空，
    /// 见 HANDOFF-2026-10-07 §3：按 5 进调节态 ⇒ 4/6 减/加）；KeyLabel（派发依据）照旧保留。
    /// </summary>
    public bool IsAdjustable => Kind is PanelRowKind.Value or PanelRowKind.Position;

    /// <summary>Row-level button caption; empty for rows that must not be clicked.</summary>
    public string ButtonText => Kind switch
    {
        PanelRowKind.Action => "执行",
        PanelRowKind.Toggle => "切换",
        _ => string.Empty
    };

    private bool _isSelected;

    /// <summary>
    /// 导航器自己的"光标"标记。面板现在不用光标了（每一行印自己的键），
    /// 显示高亮走 <see cref="IsCurrent"/>；这个属性留着给导航器与它的测试。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }
}

/// <summary>
/// Pure, total mapping from a virtual-key code to a panel key. Anything the
/// native side does not swallow (WASD, arrows, control groups, F-keys) maps to
/// <see cref="PanelKeyCommand.None"/>, so an unknown code can never fire an action.
/// </summary>
public static class PanelKeyMap
{
    public const int Numpad0 = 0x60, Numpad1 = 0x61, Numpad2 = 0x62, Numpad3 = 0x63, Numpad4 = 0x64;
    public const int Numpad5 = 0x65, Numpad6 = 0x66, Numpad7 = 0x67, Numpad8 = 0x68, Numpad9 = 0x69;
    public const int Multiply = 0x6A, Add = 0x6B, Separator = 0x6C, Subtract = 0x6D, Decimal = 0x6E, Divide = 0x6F;

    // NumLock 关掉时，小键盘发的是这几个"导航键"码（连小键盘 0 都会变成 Insert）。
    public const int PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24;
    public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const int Insert = 0x2D, Delete = 0x2E, Clear = 0x0C;

    public static PanelKeyCommand Map(int virtualKey) => virtualKey switch
    {
        Numpad0 => PanelKeyCommand.Digit0,
        Numpad1 => PanelKeyCommand.Digit1,
        Numpad2 => PanelKeyCommand.Digit2,
        Numpad3 => PanelKeyCommand.Digit3,
        Numpad4 => PanelKeyCommand.Digit4,
        Numpad5 => PanelKeyCommand.Digit5,
        Numpad6 => PanelKeyCommand.Digit6,
        Numpad7 => PanelKeyCommand.Digit7,
        Numpad8 => PanelKeyCommand.Digit8,
        Numpad9 => PanelKeyCommand.Digit9,
        Add => PanelKeyCommand.Plus,
        Subtract => PanelKeyCommand.Minus,
        Multiply => PanelKeyCommand.Times,
        Divide => PanelKeyCommand.Divide,
        Decimal => PanelKeyCommand.Dot,
        Separator => PanelKeyCommand.Dot,   // 某些键盘布局小键盘那个"点"是分隔符键，同一个意思
        // 口径 B：换页不占 4/6，改由 PgUp/PgDn 承担（数字键 7/9 同名同义，都在小键盘上）。
        PageUp => PanelKeyCommand.Digit7,
        PageDown => PanelKeyCommand.Digit9,
        _ => PanelKeyCommand.None
    };

    /// <summary>
    /// NumLock 关掉时，小键盘发的是导航键的码：0→Insert、2→Down、5→Clear…… 翻译回面板上印着的那个编号，
    /// 这样"行上印了编号就按编号直达"在 NumLock 开或关两种情况下都成立。
    /// 真 Insert/方向键/Home/End/PgUp/PgDn/Del 是**扩展键**，小键盘那些不是——只有靠这一位才能分开它们；
    /// 所以扩展键一律返回 None（真 Insert 还要留着开关面板模式）。
    /// 没有这一条的实测后果：NumLock 一关，按小键盘 0 被当成 Ins，面板直接被关掉（21:41 之后再没画过一次）。
    /// </summary>
    public static PanelKeyCommand MapNumpadNavigation(int virtualKey, bool isExtended)
    {
        if (isExtended) return PanelKeyCommand.None;
        return virtualKey switch
        {
            Insert => PanelKeyCommand.Digit0,
            End => PanelKeyCommand.Digit1,
            Down => PanelKeyCommand.Digit2,
            PageDown => PanelKeyCommand.Digit3,
            Left => PanelKeyCommand.Digit4,
            Clear => PanelKeyCommand.Digit5,
            Right => PanelKeyCommand.Digit6,
            Home => PanelKeyCommand.Digit7,
            Up => PanelKeyCommand.Digit8,
            PageUp => PanelKeyCommand.Digit9,
            Delete => PanelKeyCommand.Dot,
            _ => PanelKeyCommand.None
        };
    }

    /// <summary>
    /// 按键在行表里用的标签。行自己写标签、派发时用同一张表查，
    /// 所以"面板上印的键"和"真按生效的键"是同一份数据（不可能对不上）。
    /// </summary>
    public static string Spec(PanelKeyCommand command) => command switch
    {
        PanelKeyCommand.Digit0 => "0",
        PanelKeyCommand.Digit1 => "1",
        PanelKeyCommand.Digit2 => "2",
        PanelKeyCommand.Digit3 => "3",
        PanelKeyCommand.Digit4 => "4",
        PanelKeyCommand.Digit5 => "5",
        PanelKeyCommand.Digit6 => "6",
        PanelKeyCommand.Digit7 => "7",
        PanelKeyCommand.Digit8 => "8",
        PanelKeyCommand.Digit9 => "9",
        PanelKeyCommand.Plus => "+",
        PanelKeyCommand.Minus => "-",
        PanelKeyCommand.Times => "*",
        PanelKeyCommand.Divide => "/",
        PanelKeyCommand.Dot => ".",
        _ => string.Empty
    };
}

/// <summary>
/// Owns the highlighted row of the in-game panel. Selection is always clamped to
/// the current row set: a stale index can never point outside the rows, and an
/// empty row set can never keep a highlight.
/// </summary>
public sealed class PanelNavigator : ViewModelBase
{
    private readonly ObservableCollection<PanelNavRow> _rows = new();
    private int _index = -1;
    // 默认页必须是**面板页**：这里是主窗首页「启动」的话，第一次 SetRows 之前 Page/PageLabel
    // 报的是一个面板里根本不存在的页（OCR 复核抓到）。
    private string _page = ConsoleNavigation.DefaultPanelSection;

    public ObservableCollection<PanelNavRow> Rows => _rows;
    public string Page => _page;
    public int SelectedIndex => _index;
    public int RowCount => _rows.Count;
    public bool HasSelection => Selected is not null;

    public PanelNavRow? Selected =>
        _index >= 0 && _index < _rows.Count ? _rows[_index] : null;

    public PanelNavRow? RowAt(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index] : null;

    public string PageLabel =>
        $"{_page} · {(_index >= 0 && _rows.Count > 0 ? _index + 1 : 0)}/{_rows.Count}";

    public string SelectionLabel => Selected is null ? "未选中 · Num8/2 选行" : Selected.Label;

    /// <summary>Replaces the row set while keeping the highlight on the same index where it still exists.</summary>
    public void SetRows(string page, IEnumerable<PanelNavRow> rows)
    {
        var previousPage = _page;
        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);
        // ★ 必须用 IsPanelSection ★：面板页是 {功能, 增援, 选单位}，而主窗四页是 {启动, 增援, 功能, 诊断}。
        // 原来用 IsSection 校验，"选单位"不在主窗那四页里 ⇒ 被静默换成默认页，曲线救国的"按 7/9 换页"能过、
        // 但页面标签/翻页基准全错（OCR 复核抓到，评为 critical）。
        _page = ConsoleNavigation.IsPanelSection(page) ? page : ConsoleNavigation.DefaultPanelSection;
        if (_index >= _rows.Count) _index = _rows.Count == 0 ? -1 : _rows.Count - 1;
        ApplySelectionFlags();
        MarkCurrent();
        RaiseAll();
        // Page 只在真的换页时通知。视图拿它判定"回到页首"：每次重建行都通知的话，
        // 按一下 Num2 都会被拽回顶部，正好是用户报的"显示不跟随"。
        if (_page != previousPage) OnPropertyChanged(nameof(Page));
    }

    /// <summary>Moves the highlight by <paramref name="delta"/>, clamped to the row set.</summary>
    public bool Move(int delta)
    {
        if (_rows.Count == 0) return ClearSelection();
        var next = _index < 0
            ? (delta >= 0 ? 0 : _rows.Count - 1)
            : Math.Clamp(_index + delta, 0, _rows.Count - 1);
        return Select(next);
    }

    /// <summary>Selects an exact row, clamped; a negative index clears the highlight.</summary>
    public bool Select(int index)
    {
        if (_rows.Count == 0 || index < 0) return ClearSelection();
        var next = Math.Clamp(index, 0, _rows.Count - 1);
        if (next == _index) return false;
        _index = next;
        ApplySelectionFlags();
        MarkCurrent();
        RaiseAll();
        return true;
    }

    /// <summary>
    /// 把"当前行"写回行上。用户报的"我用上下选到哪个我也不知道全靠猜" + "增援/选单位页按了没反应"
    /// 就是这个方法缺了的后果：PanelNavRow.IsCurrent 声明了、注释也写着"面板用它加高亮"，
    /// 但**全项目没有任何地方给它赋值** ⇒ 每一行永远 false ⇒ 高亮画不出来、Num5 也没有执行目标。
    /// </summary>
    private void MarkCurrent()
    {
        foreach (var row in _rows) row.IsCurrent = false;
        if (_index >= 0 && _index < _rows.Count) _rows[_index].IsCurrent = true;
    }

    public bool ClearSelection()
    {
        if (_index < 0) return false;
        _index = -1;
        ApplySelectionFlags();
        MarkCurrent();
        RaiseAll();
        return true;
    }

    /// <summary>Page order movement (Num4/Num6): wraps so both neighbours are always reachable.</summary>
    public static string NeighbourPage(string current, int direction)
    {
        // 面板换页走面板页签（游戏内动词），不是主窗那四页 —— 用错就会出现
        // "按 Num6 页面不动"（算出来的「启动」被面板校验拒掉）。
        // 兜底也必须落在**面板页**上：曾经这里用 ConsoleNavigation.DefaultSection（"启动"），
        // 那是主窗首页、根本不是面板页 ⇒ 传一个不认识的页名时算出来的页面板里不存在。
        var sections = ConsoleNavigation.PanelSections;
        var start = -1;
        for (var i = 0; i < sections.Count; i++) if (sections[i] == current) { start = i; break; }
        if (start < 0)
            for (var i = 0; i < sections.Count; i++) if (sections[i] == ConsoleNavigation.DefaultPanelSection) { start = i; break; }
        if (start < 0) return ConsoleNavigation.DefaultPanelSection;
        var next = ((start + direction) % sections.Count + sections.Count) % sections.Count;
        return sections[next];
    }

    private void ApplySelectionFlags()
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].IsSelected = i == _index;
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(HasSelection));
    }
}
