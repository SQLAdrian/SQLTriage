/* In the name of God, the Merciful, the Compassionate */

// ── ONE ATTEMPT at the AppUserState / BootstrapEligibilityProof circular type-init race ──────────
//
// See the doc comment on AppUserState's static constructor (Data/Services/AppUserState.cs,
// ~line 307-315): AppUserState's own static ctor forces BootstrapEligibilityProof's static ctor
// via RuntimeHelpers.RunClassConstructor, and BootstrapEligibilityProof's static ctor writes to
// AppUserState's private static field _mintedProof — each type's initialiser touches the other.
// The CLR serialises type initialisation per type with a lock held for the cctor body's duration
// (ECMA-335 §I.8.9.5 / II.10.5.3.3). If thread A enters via AppUserState first and thread B enters
// via BootstrapEligibilityProof first, each can end up waiting on the lock the other is holding —
// classic circular-type-init deadlock.
//
// A type initialiser runs AT MOST ONCE per load context, so this program IS one attempt: run it,
// see whether both threads finish inside the bound, then exit. The driving test spawns a FRESH
// process per iteration, which is what supplies a fresh type-init state each time — see
// Tests/SQLTriage.Tests/AppUserStateTypeInitRaceHarnessTests.cs.
//
// stdout carries one machine-readable "VERDICT:" line so the parent process does not have to
// parse anything else. Exit codes: 0 = OK (both threads completed), 1 = DEADLOCK (at least one
// thread did not complete inside the bound), 2 = ERROR (a thread threw).

using System.Runtime.CompilerServices;
using SQLTriage.Data.Services;

const int BoundMs = 3000;

var barrier = new Barrier(2);
Exception? exceptionFromAppUserStateEntry = null;
Exception? exceptionFromProofEntry = null;

// "Touches an AppUserState static member first" — the same entry path
// Tests/SQLTriage.Tests/BootstrapProofForTests.cs.Steal() uses to guarantee both type
// initialisers have run: forcing AppUserState's OWN static ctor.
var threadViaAppUserState = new Thread(() =>
{
    try
    {
        barrier.SignalAndWait();
        RuntimeHelpers.RunClassConstructor(typeof(AppUserState).TypeHandle);
    }
    catch (Exception ex) { exceptionFromAppUserStateEntry = ex; }
})
{ IsBackground = true, Name = "via-AppUserState" };

// Reaches BootstrapEligibilityProof DIRECTLY, bypassing AppUserState entirely. The nested type is
// public (only its constructor is private), so this needs no reflection or UnsafeAccessor.
var threadViaProof = new Thread(() =>
{
    try
    {
        barrier.SignalAndWait();
        RuntimeHelpers.RunClassConstructor(typeof(AppUserState.BootstrapEligibilityProof).TypeHandle);
    }
    catch (Exception ex) { exceptionFromProofEntry = ex; }
})
{ IsBackground = true, Name = "via-BootstrapEligibilityProof" };

threadViaAppUserState.Start();
threadViaProof.Start();

var joinedAppUserState = threadViaAppUserState.Join(BoundMs);
var joinedProof = threadViaProof.Join(BoundMs);

if (exceptionFromAppUserStateEntry != null || exceptionFromProofEntry != null)
{
    Console.WriteLine("VERDICT:ERROR");
    Console.WriteLine("via-AppUserState exception: " + exceptionFromAppUserStateEntry);
    Console.WriteLine("via-BootstrapEligibilityProof exception: " + exceptionFromProofEntry);
    return 2;
}

if (!joinedAppUserState || !joinedProof)
{
    Console.WriteLine("VERDICT:DEADLOCK");
    Console.WriteLine(
        $"via-AppUserState joined={joinedAppUserState} via-BootstrapEligibilityProof joined={joinedProof} boundMs={BoundMs}");
    // Do not attempt to clean up a wedged thread — there is nothing safe to do with it, and it is
    // IsBackground so it cannot keep the process alive past this return.
    return 1;
}

Console.WriteLine("VERDICT:OK");
return 0;
