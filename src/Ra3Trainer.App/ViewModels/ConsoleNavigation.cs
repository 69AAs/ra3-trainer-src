namespace Ra3Trainer.App.ViewModels;

/// <summary>Window-local navigation only; pages remain alive and retain their controls and scroll offsets.</summary>
public sealed class ConsoleNavigation : ViewModelBase
{
    /// <summary>首页＝每天用的主操作；默认停在这一页。</summary>
    public const string DefaultSection = "启动";

    /// <summary>
    /// 游戏内面板的页签 —— 与主窗**故意不同**。
    /// 面板只在游戏里能看见，所以只放"进了游戏还做的事"：
    /// 进了游戏装载早就完成了，"启动/连接游戏/开启修改"放在这里是自相矛盾（需求原话：
    /// "我都已经是进游戏的状态了，为什么还有启动这一栏"）。诊断也是桌面低频活。
    /// </summary>
    public const string DefaultPanelSection = "功能";

    public static IReadOnlyList<string> PanelSections { get; } = new[] { "功能", "增援", "选单位" };

    public static bool IsPanelSection(string? value) => value is not null && PanelSections.Contains(value);

    private string _selected = DefaultSection;

    /// <summary>
    /// 四个页签，名字必须与内容对得上（用户为此发过火）：
    /// 启动=每天用的主操作，增援=发兵，功能=32 项开关，诊断=排查与取证。
    /// </summary>
    public static IReadOnlyList<string> Sections { get; } = new[] { "启动", "增援", "功能", "诊断" };

    public static bool IsSection(string? value) => value is not null && Sections.Contains(value);

    public string Selected
    {
        get => _selected;
        set
        {
            if (!Sections.Contains(value) || value == _selected) return;
            _selected = value;
            OnPropertyChanged();
        }
    }

    public System.Windows.Input.ICommand SelectCommand { get; }
    public ConsoleNavigation() => SelectCommand = new NavigationCommand(this);
    private sealed class NavigationCommand(ConsoleNavigation owner) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? value) => value is string section && Sections.Contains(section);
        public void Execute(object? value) { if (CanExecute(value)) owner.Selected = (string)value!; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
