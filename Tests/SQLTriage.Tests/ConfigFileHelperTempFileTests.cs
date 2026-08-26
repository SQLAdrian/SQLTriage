/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// <see cref="ConfigFileHelper.Save{T}"/> writes atomically: serialize to <c>&lt;file&gt;.tmp</c>,
    /// then move it over the target. The temp file therefore holds a COMPLETE serialized copy of the
    /// config object, and for the portal settings that includes the wrapped intake credential.
    ///
    /// <para>A move that failed used to leave that copy on disk at a predictable name, outside every
    /// path that manages the real file: nothing rotates it, the next successful save does not
    /// overwrite it (the move consumes its own temp), and no caller knows it is there. These tests
    /// drive the failure for real — the target path is a DIRECTORY, so the move cannot succeed — and
    /// measure the disk afterwards.</para>
    /// </summary>
    public class ConfigFileHelperTempFileTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "sqlt-confighelper-" + Guid.NewGuid().ToString("N"));

        public ConfigFileHelperTempFileTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        private sealed class SettingsShape
        {
            public string DailyIntakeSasProtected { get; set; } = "";
        }

        private const string WrappedCredential = "aes:VGhpc0lzTm90QVJlYWxDcmVkZW50aWFsRm9yQVRlc3RPbmx5";

        [Fact]
        public void A_save_whose_move_fails_leaves_no_temp_file_holding_the_credential()
        {
            // The target path EXISTS AS A DIRECTORY, so File.Move onto it cannot succeed. Nothing is
            // mocked: the same File.WriteAllText and File.Move the production path uses are driven.
            string target = Path.Combine(_root, "portal-settings.json");
            Directory.CreateDirectory(target);
            string temp = target + ".tmp";

            var save = () => ConfigFileHelper.Save(target, new SettingsShape { DailyIntakeSasProtected = WrappedCredential });

            // The contract is unchanged: the failure still reaches the caller.
            save.Should().Throw<Exception>();

            // And the copy of the credential is gone.
            File.Exists(temp).Should().BeFalse(
                "the temp file holds a full serialized copy of the settings, credential included");
        }

        [Fact]
        public void The_temp_file_really_would_have_carried_the_credential()
        {
            // The CONTROL for the test above. Without it, "no .tmp on disk" could mean the temp file
            // never held anything worth removing, and the assertion above would be vacuous. Here the
            // save SUCCEEDS, and the bytes that transited the temp path are read back off the target.
            string target = Path.Combine(_root, "ok-settings.json");

            ConfigFileHelper.Save(target, new SettingsShape { DailyIntakeSasProtected = WrappedCredential });

            File.Exists(target).Should().BeTrue();
            File.ReadAllText(target).Should().Contain(WrappedCredential,
                "this is the content the failed-move path would otherwise have left in <file>.tmp");
            File.Exists(target + ".tmp").Should().BeFalse("a successful move consumes its own temp file");
        }
    }
}
