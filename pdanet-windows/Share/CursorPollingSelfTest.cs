namespace PdaNetPortable.Share;

/// <summary>Pointer scheduling and lifetime checks without capture, a GPU or a network.</summary>
internal static class CursorPollingSelfTest
{
    public static string Run()
    {
        CaptureStallDoesNotStopPointer();
        ScreenChangesCoalesceAndReset();
        LostFinalPointerRepairsWhileIdle();
        return "pointer keeps polling during a 250 ms capture stall; idle position/visibility repeat every 250 ms; screen changes and shutdown keep one owner";
    }

    private static void LostFinalPointerRepairsWhileIdle()
    {
        var updates = new PointerUpdates();
        var first = updates.Next(100, 200, true, 10_000);
        Require(first != null, "the initial pointer was not sent");
        // Model the unreliable channel losing this last movement: no further input arrives.
        Require(updates.Next(100, 200, true, 10_249) == null, "an idle pointer flooded the link");
        var repair = updates.Next(100, 200, true, 10_250);
        Require(repair != null && repair.AsSpan().SequenceEqual(first),
            "the lost final pointer was not repeated while the screen stayed idle");
        var movement = updates.Next(101, 201, true, 10_251);
        Require(movement != null && movement.AsSpan().SequenceEqual(MsgWriter.Pointer(101, 201, true)),
            "the pointer heartbeat delayed a fresh movement");
        var hidden = updates.Next(101, 201, false, 10_252);
        Require(hidden != null && hidden.AsSpan().SequenceEqual(MsgWriter.Pointer(101, 201, false)),
            "a changed visibility waited for the heartbeat");
        Require(updates.Next(101, 201, false, 10_501) == null, "hidden-pointer heartbeats exceeded their rate");
        var hiddenRepair = updates.Next(101, 201, false, 10_502);
        Require(hiddenRepair != null && hiddenRepair.AsSpan().SequenceEqual(hidden),
            "a lost final visibility was never repaired");
        updates.Reset();
        Require(updates.Next(101, 201, false, 10_503) != null,
            "the pointer was not resent after a capture reopen");
    }

    private static void CaptureStallDoesNotStopPointer()
    {
        int polls = 0, active = 0, overlap = 0, worker = 0;
        using var first = new ManualResetEventSlim();
        var cursor = new CursorPolling(_ =>
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Exchange(ref overlap, 1);
            Interlocked.Exchange(ref worker, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref polls);
            first.Set();
            Interlocked.Decrement(ref active);
        }, () => { });
        using (cursor)
        {
            cursor.Start();
            cursor.SetScreen(Screen("first"));
            Require(first.Wait(2000), "the pointer worker did not start");
            int before = Volatile.Read(ref polls);
            // The caller models the capture/encode thread blocked on a GPU or a pipe write.
            Thread.Sleep(250);
            Require(Volatile.Read(ref polls) - before >= 3,
                "a 250 ms blocked capture thread held back the pointer");
            Require(Volatile.Read(ref worker) != Environment.CurrentManagedThreadId,
                "the pointer was polled on the capture thread");
        }
        int stoppedAt = Volatile.Read(ref polls);
        cursor.SetScreen(Screen("after shutdown"));
        cursor.Start();
        Thread.Sleep(40);
        Require(Volatile.Read(ref polls) == stoppedAt, "pointer polling continued after shutdown");
        Require(Volatile.Read(ref overlap) == 0 && Volatile.Read(ref active) == 0,
            "pointer polls overlapped or remained active after shutdown");
    }

    private static void ScreenChangesCoalesceAndReset()
    {
        var firstScreen = Screen("first");
        var skipped = Screen("skipped");
        var latest = Screen("latest");
        int resets = 0, polls = 0, owner = 0, owners = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var switched = new ManualResetEventSlim();
        using var restarted = new ManualResetEventSlim();
        bool sawSkipped = false, resetBeforeSwitch = false;
        using var cursor = new CursorPolling(screen =>
        {
            int thread = Environment.CurrentManagedThreadId;
            int previous = Interlocked.CompareExchange(ref owner, thread, 0);
            if (previous != 0 && previous != thread)
                Interlocked.Exchange(ref owners, 1);
            int call = Interlocked.Increment(ref polls);
            if (ReferenceEquals(screen, firstScreen) && call == 1)
            {
                entered.Set();
                release.Wait(2000);
            }
            if (ReferenceEquals(screen, skipped)) sawSkipped = true;
            if (ReferenceEquals(screen, latest))
            {
                resetBeforeSwitch = Volatile.Read(ref resets) >= 2;
                switched.Set();
                if (Volatile.Read(ref resets) >= 3) restarted.Set();
            }
        }, () => Interlocked.Increment(ref resets));
        cursor.Start();
        cursor.SetScreen(firstScreen);
        try
        {
            Require(entered.Wait(2000), "the first screen was never polled");
            cursor.SetScreen(skipped);
            cursor.SetScreen(latest);
        }
        finally { release.Set(); }
        Require(switched.Wait(2000), "the latest screen was never polled");
        Require(!sawSkipped && resetBeforeSwitch, "screen changes did not coalesce and reset before polling");
        // A capture reopen may retain an equal screen. It still needs the complete cursor shape.
        cursor.SetScreen(latest);
        Require(restarted.Wait(2000), "reopening the same screen did not reset the pointer");
        Require(Volatile.Read(ref owners) == 0, "CursorWatch state had more than one thread owner");
    }

    private static ShareScreen Screen(string name) => new(name, 0, 0, 1920, 1080, true);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
