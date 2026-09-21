using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CompilePalX.Configuration
{
    public class Settings : ICloneable, IEquatable<GameConfiguration>
    {
        public bool PlaySoundOnCompileCompletion { get; set; } = true;

        // not directly editable by user, set in MainWindow.xaml.cs on shutdown
        public string? MapListHeight { get; set; } = null;

        public object Clone()
        {
            return MemberwiseClone();
        }

        public bool Equals(GameConfiguration? other)
        {
            throw new NotImplementedException();
        }
    }
}
