/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using SQLTriage.Data.Parser;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    public class MappingResolverTests
    {
        [Fact]
        public void Empty_resolver_has_all_three_null()
        {
            using var r = MappingResolver.Empty();
            Assert.Null(r.ControlMappings);
            Assert.Null(r.RoadmapMapping);
            Assert.Null(r.RoadmapAliases);
        }

        [Fact]
        public void Loads_only_present_files()
        {
            var bundle = new FakeBundleAccessor();
            bundle.PutFile("Config/roadmap-aliases.json",
                "{\"_comment\": \"test\", \"key\": \"value\"}");

            using var r = new MappingResolver(bundle);
            // ControlMappings and RoadmapMapping are absent -> null
            Assert.Null(r.ControlMappings);
            Assert.Null(r.RoadmapMapping);
            // RoadmapAliases was present in the bundle
            Assert.NotNull(r.RoadmapAliases);
            Assert.Equal("value", r.RoadmapAliases!.RootElement.GetProperty("key").GetString());
        }
    }
}
