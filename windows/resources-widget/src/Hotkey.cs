using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ResourcesWidget
{
    // Глобальное сочетание клавиш. Сообщения приходят в служебное невидимое окно,
    // поэтому сочетание работает, даже когда карточка скрыта.
    sealed class Hotkey : IDisposable
    {
        const int Id = 0x5257;
        readonly HwndSource source;
        readonly Action action;
        readonly bool registered;

        public readonly string Text;    // "Ctrl+Alt+R" или null, если сочетание не задано
        public readonly string Error;   // почему не удалось зарегистрировать

        public Hotkey(string spec, Action action)
        {
            this.action = action;
            if (string.IsNullOrWhiteSpace(spec)) return;
            uint mods, vk;
            string text;
            if (!TryParse(spec, out mods, out vk, out text))
            {
                Error = "не понято сочетание «" + spec + "» в config.json";
                return;
            }
            var p = new HwndSourceParameters("ResourcesWidget.Hotkey");
            p.ParentWindow = new IntPtr(-3);   // HWND_MESSAGE: окно только для сообщений
            p.WindowStyle = 0;
            source = new HwndSource(p);
            source.AddHook(WndProc);
            registered = Native.RegisterHotKey(source.Handle, Id, mods | Native.MOD_NOREPEAT, vk);
            if (registered) Text = text;
            else
            {
                int err = Marshal.GetLastWin32Error();
                Error = err == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "сочетание " + text + " уже занято другой программой"
                    : "не удалось зарегистрировать " + text + " (код " + err + ")";
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        // "ctrl + alt + r" -> MOD_CONTROL|MOD_ALT, 'R', "Ctrl+Alt+R"
        public static bool TryParse(string spec, out uint mods, out uint vk, out string text)
        {
            mods = 0;
            vk = 0;
            text = null;
            var names = new List<string>();
            string keyName = null;
            foreach (string raw in spec.Split('+'))
            {
                string part = raw.Trim();
                if (part.Length == 0) return false;
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= Native.MOD_CONTROL; continue;
                    case "alt": mods |= Native.MOD_ALT; continue;
                    case "shift": mods |= Native.MOD_SHIFT; continue;
                    case "win":
                    case "windows": mods |= Native.MOD_WIN; continue;
                }
                if (keyName != null) return false;   // две обычные клавиши
                if (part.Length == 1 && char.IsLetterOrDigit(part[0]) && part[0] < 128)
                {
                    vk = char.ToUpperInvariant(part[0]);
                    keyName = part.ToUpperInvariant();
                    continue;
                }
                Key key;
                int ignored;
                if (int.TryParse(part, out ignored) || !Enum.TryParse(part, true, out key)) return false;
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return false;
                keyName = key.ToString();
            }
            if (keyName == null || mods == 0) return false;
            if ((mods & Native.MOD_CONTROL) != 0) names.Add("Ctrl");
            if ((mods & Native.MOD_ALT) != 0) names.Add("Alt");
            if ((mods & Native.MOD_SHIFT) != 0) names.Add("Shift");
            if ((mods & Native.MOD_WIN) != 0) names.Add("Win");
            names.Add(keyName);
            text = string.Join("+", names);
            return true;
        }

        public void Dispose()
        {
            if (source == null) return;
            if (registered) Native.UnregisterHotKey(source.Handle, Id);
            source.Dispose();
        }
    }
}
