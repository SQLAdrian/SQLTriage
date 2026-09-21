/* In the name of God, the Merciful, the Compassionate */
/*
 * An availability-group replica and the role it currently holds.
 *
 * Unknown is a first-class value, not a parse failure: sys.dm_hadr_availability_replica_states
 * has no row for a replica that is down, and "we do not know which side is live" is a materially
 * different answer from "this is a secondary". Anything that writes to a replica must refuse on
 * Unknown rather than fall back to a default.
 */

namespace SQLTriage.Data.Models.Jobs
{
    public enum AgRole { Unknown, Primary, Secondary }

    public class AgReplicaRole
    {
        public string AgName { get; set; } = string.Empty;
        public string ReplicaServerName { get; set; } = string.Empty;
        public AgRole Role { get; set; } = AgRole.Unknown;
        public bool IsLocal { get; set; }
    }
}
