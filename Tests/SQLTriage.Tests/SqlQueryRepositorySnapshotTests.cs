/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// <c>SqlQueryRepository.Snapshot</c> is published to reader threads and never mutated after
    /// publication. Until 2026-08-10 that was true only because the two builders happened not to keep
    /// their dictionaries: <c>IReadOnlyDictionary</c> is a read-only VIEW over a caller-owned
    /// <c>Dictionary</c>, so a builder that retained one could mutate a published snapshot with no
    /// compiler error and no test signal. The comment on the type said exactly that and asked for
    /// immutable collections.
    ///
    /// <para>These tests hold the switch. They reflect over the real published snapshot of a real
    /// repository, because the claim is about what the running type actually carries and not about
    /// what a declaration reads like.</para>
    /// </summary>
    public sealed class SqlQueryRepositorySnapshotTests : IDisposable
    {
        private const string Id = "snapshot-immutability-probe";

        private readonly string _sqlDir;
        private readonly List<string> _tempFiles = new();

        public SqlQueryRepositorySnapshotTests()
        {
            _sqlDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Sql");
            Directory.CreateDirectory(_sqlDir);
        }

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        private async Task<SqlQueryRepository> MakeLoadedRepoAsync()
        {
            var path = Path.Combine(_sqlDir, Id + ".sql");
            await File.WriteAllTextAsync(path, "SELECT 1 AS n");
            _tempFiles.Add(path);

            var bundle = new FakeBundleAccessor().PutFile("Config/queries.json",
                $"{{\"queries\":{{\"{Id}\":{{\"category\":\"Probe\",\"severity\":\"HIGH\",\"status\":\"working\",\"quick\":true}}}}}}");
            var repo = new SqlQueryRepository(NullLogger<SqlQueryRepository>.Instance, bundle);
            await repo.InitializationComplete;
            return repo;
        }

        private static object ReadSnapshot(SqlQueryRepository repo)
        {
            var field = typeof(SqlQueryRepository).GetField("_snapshot",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field.Should().NotBeNull("the published snapshot is the thing under test");
            var snapshot = field!.GetValue(repo);
            snapshot.Should().NotBeNull();
            return snapshot!;
        }

        private static PropertyInfo Member(object snapshot, string name)
        {
            var prop = snapshot.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance
                                                          | BindingFlags.Public);
            prop.Should().NotBeNull($"Snapshot.{name} is what readers take");
            return prop!;
        }

        private static object ReadMember(object snapshot, string name)
        {
            var value = Member(snapshot, name).GetValue(snapshot);
            value.Should().NotBeNull();
            return value!;
        }

        [Fact]
        public async Task The_published_queries_are_a_frozen_dictionary_not_a_view_over_a_mutable_one()
        {
            var repo = await MakeLoadedRepoAsync();
            var snapshot = ReadSnapshot(repo);

            // The DECLARED type is the compiler-held part: this is what any future call site is
            // forced to hand over, and what no reader can widen back to a mutable dictionary.
            Member(snapshot, "Queries").PropertyType
                .Should().Be(typeof(FrozenDictionary<string, SqlQueryDefinition>),
                "an IReadOnlyDictionary over a Dictionary leaves the builder able to mutate a "
                + "published snapshot; a FrozenDictionary has no mutating API at all");

            // And the instance really is one. ToFrozenDictionary returns an internal subclass chosen
            // by key shape, so this is assignability rather than exact type.
            var queries = ReadMember(snapshot, "Queries");
            queries.Should().BeAssignableTo<FrozenDictionary<string, SqlQueryDefinition>>();

            // The negative half: the published object is NOT a Dictionary, so no cast reaches a
            // mutating API. This is the exact hole the old comment described.
            (queries is Dictionary<string, SqlQueryDefinition>).Should().BeFalse();
            (queries is IDictionary<string, SqlQueryDefinition> writable && !writable.IsReadOnly)
                .Should().BeFalse("nothing published may present a writable dictionary interface");
        }

        [Fact]
        public async Task The_published_tag_index_is_frozen_and_so_are_its_values()
        {
            var repo = await MakeLoadedRepoAsync();
            var snapshot = ReadSnapshot(repo);

            // An immutable dictionary of MUTABLE lists is not an immutable index: freezing only the
            // outer map would have moved the same hole one level down. The VALUE type is the
            // assertion, and it is taken off the declaration, which is what holds it.
            Member(snapshot, "TagIndex").PropertyType
                .Should().Be(typeof(FrozenDictionary<string, ImmutableArray<string>>));

            var index = ReadMember(snapshot, "TagIndex");
            index.Should().BeAssignableTo<FrozenDictionary<string, ImmutableArray<string>>>();

            var frozen = (FrozenDictionary<string, ImmutableArray<string>>)index;
            frozen.Should().NotBeEmpty("the probe query carries a category, so the index is populated");
            foreach (var entry in frozen)
                (entry.Value is List<string>).Should().BeFalse(
                    $"tag '{entry.Key}' must not publish a mutable list");
        }

        [Fact]
        public async Task Freezing_changed_no_lookup_result()
        {
            // The control. Immutability is worthless if it altered what the repository answers, and
            // the comparer is where that would happen: FrozenDictionary is built from the source
            // dictionary's own comparer, so the ordinal, case-SENSITIVE behaviour is unchanged.
            var repo = await MakeLoadedRepoAsync();

            repo.Get(Id).Should().NotBeNull();
            repo.Get(Id.ToUpperInvariant()).Should().BeNull("lookups were and remain case-sensitive");
            repo.Get("no-such-query").Should().BeNull();

            repo.GetByTag("probe").Select(q => q.Id).Should().Contain(Id,
                "the tag index lower-cases its keys, as it always did");
            repo.GetByTag("no-such-tag").Should().BeEmpty();
            repo.GetQuickChecks().Select(q => q.Id).Should().Contain(Id);
            repo.GetAll().Should().ContainKey(Id);
        }

        [Fact]
        public async Task A_reload_publishes_a_whole_new_frozen_snapshot()
        {
            // The swap is still a reference swap of one internally consistent object, which is the
            // property the generation-monotonicity and race tests rest on.
            var repo = await MakeLoadedRepoAsync();
            var before = ReadSnapshot(repo);

            await repo.ReloadAsync();
            var after = ReadSnapshot(repo);

            ReferenceEquals(before, after).Should().BeFalse("a load builds a new Snapshot");
            ReadMember(after, "Queries").Should().BeAssignableTo<FrozenDictionary<string, SqlQueryDefinition>>();
            repo.Get(Id).Should().NotBeNull();
        }
    }
}
