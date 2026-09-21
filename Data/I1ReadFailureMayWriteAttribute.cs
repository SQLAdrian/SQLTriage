/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data
{
    /// <summary>
    /// Declares, AT THE SITE, that this method may reach a file-replacing write from a handler that
    /// caught a failed config read — and says why that is not the silent replacement invariant I1
    /// forbids.
    ///
    /// <para><b>(I1) A customer's configuration must never be silently replaced by a built-in
    /// default.</b></para>
    ///
    /// <para><b>This attribute is not documentation; it is the allow-list, and it is the ONLY one.</b>
    /// <c>ConfigStoreI1CensusTests.A_failed_config_read_never_reaches_a_replacing_write</c> enumerates
    /// the offending methods from the COMPILED ASSEMBLY — metadata plus IL, never source text — and
    /// compares that set against the methods carrying this attribute. A new method that writes after a
    /// failed read turns the census RED with nothing for anyone to remember to update; and because the
    /// declaration lives on the method rather than in a list in another file, exempting one is a
    /// deliberate, reviewable line in the diff at the place it applies.</para>
    ///
    /// <para><b>Why the list lives here rather than in the test.</b> On 2026-08-04 seven config stores
    /// were guarded against exactly this and the boundary was declared held. A cold gate then found
    /// three stores that were not on the hand-kept list at all — <c>ServerConnectionManager</c>,
    /// <c>ScheduledTaskDefinitionService</c> and <c>AlertDefinitionService</c> — and destroyed an
    /// operator's data through each of them. The list was not wrong; a hand-kept list was the wrong
    /// instrument. See <see cref="StoreWriteIntent"/>, which records that incident in full.</para>
    ///
    /// <para><b>⚠ What carrying this attribute does NOT mean.</b> It does not mean the write is safe.
    /// It means a human looked at this method, in this lane, and recorded a reason. The census proves
    /// the SET is complete; only the reason proves the member is sound, and only for as long as the
    /// method still does what the reason says.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor,
                    AllowMultiple = false, Inherited = false)]
    public sealed class I1ReadFailureMayWriteAttribute : Attribute
    {
        /// <param name="why">
        /// What this method writes after a failed read, and why that write is not a built-in default
        /// landing on an operator's configuration. State the PRESERVATION, if there is one.
        /// </param>
        public I1ReadFailureMayWriteAttribute(string why) => Why = why;

        /// <summary>The recorded reason. Read by the census only to require that one exists.</summary>
        public string Why { get; }
    }
}
