/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// View model for script configuration in UI
    /// </summary>
    public class ScriptConfigurationViewModel
    {
        public ScriptConfiguration Script { get; set; } = new();
        public bool IsSelected { get; set; }
    }
}
