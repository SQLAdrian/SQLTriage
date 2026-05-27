/* In the name of God, the Merciful, the Compassionate */

using MessagePack;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// MessagePack-serializable representation of a DataTable column schema.
    /// </summary>
    [MessagePackObject]
    public class CacheDataColumn
    {
        [Key(0)]
        public string Name { get; set; } = "";

        [Key(1)]
        public string DataTypeName { get; set; } = "";
    }

    /// <summary>
    /// MessagePack-serializable representation of a DataTable for cache storage.
    /// Replaces JSON string blobs with binary MessagePack for faster serialization,
    /// type preservation, and smaller storage.
    /// </summary>
    [MessagePackObject]
    public class CacheDataTableDto
    {
        [Key(0)]
        public CacheDataColumn[] Columns { get; set; } = System.Array.Empty<CacheDataColumn>();

        /// <summary>
        /// Rows as arrays of boxed values. DBNull is represented as null.
        /// </summary>
        [Key(1)]
        public object?[][] Rows { get; set; } = System.Array.Empty<object?[]>();
    }
}
