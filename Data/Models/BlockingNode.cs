/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    public class BlockingNode
    {
        public int Spid { get; set; }
        public string DatabaseName { get; set; } = "";
        public string LockType { get; set; } = "";
        public int? BlockerSpid { get; set; }
        public string WaitType { get; set; } = "";
        public long WaitDurationMs { get; set; }
        public string Statement { get; set; } = "";
        public int Level { get; set; }
    }
}
