/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    public class DashboardFilter
    {
        public DateTime TimeFrom { get; set; } = DateTime.Now.AddHours(-1);
        public DateTime TimeTo { get; set; } = DateTime.Now;
        public string[] Instances { get; set; } = Array.Empty<string>();
        public string? Database { get; set; }
        public string WaitGrouping { get; set; } = "Category"; // "Category" or "Type"
        public int AggregationMinutes { get; set; } = 1;
    }
}
