using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CompilePalX
{
    /// <summary>True (passed) -> green, false (failed) -> red. Used for the pass/fail circle next to each tool.</summary>
    public class BoolToPassFailBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(46, 160, 67));
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(214, 48, 41));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? PassBrush : FailBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Maps a 0-100 percent value to a color: white at 0%, green at 25%, yellow at 50%, red at
    /// 100%, linearly interpolated between those stops. Used for the BSP limits report.
    /// </summary>
    public class PercentToColorConverter : IValueConverter
    {
        private static readonly Color White = Color.FromRgb(255, 255, 255);
        private static readonly Color Green = Color.FromRgb(67, 176, 42);
        private static readonly Color Yellow = Color.FromRgb(232, 196, 24);
        private static readonly Color Red = Color.FromRgb(214, 48, 41);

        public static Color GetColor(double percent)
        {
            if (percent <= 0) return White;
            if (percent >= 100) return Red;

            if (percent <= 25)
                return Lerp(White, Green, percent / 25.0);
            if (percent <= 50)
                return Lerp(Green, Yellow, (percent - 25) / 25.0);

            return Lerp(Yellow, Red, (percent - 50) / 50.0);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = value is double d ? d : 0;
            return new SolidColorBrush(GetColor(percent));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
