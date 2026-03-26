/* In the name of God, the Merciful, the Compassionate */

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Broadcasts Radzen UI theme changes across the component tree.
    /// </summary>
    public class ThemeService
    {
        private string _theme = "dark";

        public string CurrentTheme => _theme;

        public event Action? OnThemeChanged;

        public void SetTheme(string theme)
        {
            _theme = theme;
            OnThemeChanged?.Invoke();
        }
    }
}
