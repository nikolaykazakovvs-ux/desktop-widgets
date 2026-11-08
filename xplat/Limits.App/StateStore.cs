using System;
using System.Collections.Generic;
using System.IO;
using DesktopWidgets.Limits.Core;

namespace DesktopWidgets.Limits.App
{
    // Положение и флаги карточки. Отдельный файл прототипа: state.json WPF-версии не читается и не меняется.
    public sealed class StateStore
    {
        readonly string path;
        public int? Left, Top;
        public bool Topmost = true, Locked, Hidden;

        public StateStore(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "state.json");
            var o = Json.TryParse(File.Exists(path) ? File.ReadAllText(path) : null);
            Left = (int?)Json.Num(Json.Get(o, "left"));
            Top = (int?)Json.Num(Json.Get(o, "top"));
            Topmost = Json.Bool(Json.Get(o, "topmost")) ?? true;
            Locked = Json.Bool(Json.Get(o, "locked")) ?? false;
            Hidden = Json.Bool(Json.Get(o, "hidden")) ?? false;
        }

        public void SavePosition(int x, int y) { Left = x; Top = y; Save(); }

        public void Save()
        {
            try
            {
                var d = new Dictionary<string, object> { { "left", Left }, { "top", Top }, { "topmost", Topmost }, { "locked", Locked }, { "hidden", Hidden } };
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(d, true));
                File.Move(tmp, path, true);
            }
            catch { }
        }
    }
}
