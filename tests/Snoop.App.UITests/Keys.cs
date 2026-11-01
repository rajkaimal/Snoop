using FlaUI.Core.WindowsAPI;

namespace Snoop.App.UITests;

/// <summary>Virtual-key codes by their Windows values, so the tests do not depend on FlaUI's enum names.</summary>
public static class Keys
{
    public static readonly VirtualKeyShort Shift = (VirtualKeyShort)0x10;
    public static readonly VirtualKeyShort Control = (VirtualKeyShort)0x11;
    public static readonly VirtualKeyShort Alt = (VirtualKeyShort)0x12;
    public static readonly VirtualKeyShort Enter = (VirtualKeyShort)0x0D;
    public static readonly VirtualKeyShort Escape = (VirtualKeyShort)0x1B;
    public static readonly VirtualKeyShort Left = (VirtualKeyShort)0x25;
    public static readonly VirtualKeyShort Right = (VirtualKeyShort)0x27;
    public static readonly VirtualKeyShort Delete = (VirtualKeyShort)0x2E;
    public static readonly VirtualKeyShort F3 = (VirtualKeyShort)0x72;

    /// <summary>A letter or digit key: Key('L'), Key('2').</summary>
    public static VirtualKeyShort Key(char c) => (VirtualKeyShort)char.ToUpperInvariant(c);
}
