/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    public class StatStackValue
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Color { get; set; } = "#4caf50"; // default green
        public string Instance { get; set; } = "";
    }
}
