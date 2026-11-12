using Windows.UI.Xaml.Media;

namespace Plura.Models
{
    /// <summary>Chip del resumen "Vas a transmitir a".</summary>
    public sealed class SelectionChip
    {
        public string Name { get; set; }
        public SolidColorBrush Accent { get; set; }
        public SolidColorBrush Background { get; set; }
        public SolidColorBrush Foreground { get; set; }
        public SolidColorBrush Border { get; set; }
        public double Opacity { get; set; } = 1.0;
    }
}
