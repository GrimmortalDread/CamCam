using System;
using System.Collections.Generic;

namespace CamCam;

/// <summary>
/// Single source of truth for every key name CamCam understands - used
/// both to populate the settings window's key dropdowns and to resolve a
/// saved key name to a Windows virtual-key code. Previously these were
/// two separate things (a free-text field plus a hand-written switch
/// statement), which meant a typo'd or unsupported key name would just
/// silently do nothing - a real, likely explanation for keybinds that
/// "didn't seem to work." A dropdown can only ever offer names that are
/// actually resolvable.
/// </summary>
public static class KeyCatalog
{
    public static readonly (string Name, int Vk)[] Keys;

    /// <summary>"(None)" first, then every key name alphabetically - for the settings window's dropdowns.</summary>
    public static readonly string[] DisplayNames;

    static KeyCatalog()
    {
        var list = new List<(string Name, int Vk)>();

        for (char c = 'A'; c <= 'Z'; c++) list.Add((c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) list.Add((c.ToString(), c));

        for (int i = 0; i <= 9; i++) list.Add(($"Numpad{i}", 0x60 + i));
        list.Add(("Numpad+", 0x6B));
        list.Add(("Numpad-", 0x6D));
        list.Add(("Numpad*", 0x6A));
        list.Add(("Numpad/", 0x6F));
        list.Add(("Numpad.", 0x6E));

        list.Add(("Up", 0x26));
        list.Add(("Down", 0x28));
        list.Add(("Left", 0x25));
        list.Add(("Right", 0x27));
        list.Add(("Page Up", 0x21));
        list.Add(("Page Down", 0x22));
        list.Add(("Home", 0x24));
        list.Add(("End", 0x23));
        list.Add(("Insert", 0x2D));
        list.Add(("Delete", 0x2E));
        list.Add(("Backspace", 0x08));
        list.Add(("Caps Lock", 0x14));
        list.Add(("Escape", 0x1B));
        list.Add(("Space", 0x20));
        list.Add(("Tab", 0x09));
        list.Add(("Shift", 0x10));
        list.Add(("Ctrl", 0x11));
        list.Add(("Alt", 0x12));
        list.Add(("Scroll Lock", 0x91));

        for (int i = 1; i <= 12; i++) list.Add(($"F{i}", 0x6F + i)); // F1=0x70 ... F12=0x7B

        list.Add((",", 0xBC));
        list.Add((".", 0xBE));
        list.Add((";", 0xBA));
        list.Add(("'", 0xDE));
        list.Add(("/", 0xBF));
        list.Add(("[", 0xDB));
        list.Add(("]", 0xDD));
        list.Add(("-", 0xBD));
        list.Add(("=", 0xBB));

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Keys = list.ToArray();

        var names = new string[Keys.Length + 1];
        names[0] = "(None)";
        for (int i = 0; i < Keys.Length; i++)
            names[i + 1] = Keys[i].Name;
        DisplayNames = names;
    }

    public static int Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        foreach (var k in Keys)
            if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                return k.Vk;
        return 0;
    }
}
