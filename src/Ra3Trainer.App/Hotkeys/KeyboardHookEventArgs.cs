using Ra3Trainer.Core.Hotkeys;

namespace Ra3Trainer.App.Hotkeys;

public sealed class KeyboardHookEventArgs : EventArgs
{
    public KeyboardHookEventArgs(int virtualKey, HotkeyModifiers modifiers, bool isExtended = false)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        IsExtended = isExtended;
    }

    public int VirtualKey { get; }

    /// <summary>
    /// True for the extended scan-code form: the dedicated Insert/arrows/Home/End/Del cluster. The
    /// numpad's own keys are never extended, which is the only way to tell them apart - with NumLock
    /// off the numpad sends the very same key codes as those dedicated keys.
    /// </summary>
    public bool IsExtended { get; }

    public HotkeyModifiers Modifiers { get; }

    public bool Handled { get; set; }
}
