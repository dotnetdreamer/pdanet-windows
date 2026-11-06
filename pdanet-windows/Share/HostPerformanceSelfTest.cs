namespace PdaNetPortable.Share;

/// <summary>Deterministic checks for capture work retained while a slow link drains.</summary>
internal static class HostPerformanceSelfTest
{
    public static string PendingChanges()
    {
        const int width = 1920, height = 1080, limit = 48;
        var whole = new Dirty(0, 0, width, height);
        var pending = new List<Dirty>();
        var changes = new List<Dirty>();

        // Small updates must survive until conversion, including at the exact limit.
        for (int i = 0; i < limit; i++)
        {
            var change = new Dirty(i * 16, i * 8, 16, 8);
            changes.Clear();
            changes.Add(change);
            HostPipeline.AccumulateChanges(pending, changes, width, height);
            Require(pending.Count == i + 1 && pending[i] == change,
                "a small update was lost before conversion");
            Require(changes.Count == 1 && changes[0] == change,
                "accumulation modified the capture's change list");
        }

        // Once metadata fills, a full-frame conversion must cover all earlier changes.
        changes.Clear();
        changes.Add(new Dirty(1000, 700, 16, 16));
        HostPipeline.AccumulateChanges(pending, changes, width, height);
        RequireWhole(pending, whole, "the first update beyond the limit");

        // A long encode or network stall must not keep appending capture metadata.
        for (int i = 0; i < 20_000; i++)
        {
            changes[0] = new Dirty(i % (width - 16), i % (height - 16), 16, 16);
            HostPipeline.AccumulateChanges(pending, changes, width, height);
            RequireWhole(pending, whole, "updates after the full-frame sentinel");
        }

        // One capture can itself exceed the limit, for example after moving many windows.
        pending.Clear();
        changes.Clear();
        for (int i = 0; i < 10_000; i++) changes.Add(new Dirty(i % width, i % height, 1, 1));
        HostPipeline.AccumulateChanges(pending, changes, width, height);
        RequireWhole(pending, whole, "one large capture batch");

        // A real full-screen update also subsumes earlier and later small rectangles.
        pending.Clear();
        pending.Add(new Dirty(16, 16, 32, 32));
        changes.Clear();
        changes.Add(new Dirty(64, 64, 16, 16));
        changes.Add(whole);
        changes.Add(new Dirty(96, 96, 16, 16));
        HostPipeline.AccumulateChanges(pending, changes, width, height);
        RequireWhole(pending, whole, "an explicit full-screen update");

        changes.Clear();
        HostPipeline.AccumulateChanges(pending, changes, width, height);
        RequireWhole(pending, whole, "an empty capture after a full-screen update");

        // Once conversion consumes pending work, the next capture starts a fresh batch.
        pending.Clear();
        var next = new Dirty(width - 16, height - 16, 16, 16);
        changes.Add(next);
        HostPipeline.AccumulateChanges(pending, changes, width, height);
        Require(pending.Count == 1 && pending[0] == next,
            "a new conversion batch retained the previous full-screen sentinel");

        return "20,000 delayed captures stay bounded; small changes survive, large batches become one full-screen conversion";
    }

    private static void RequireWhole(List<Dirty> pending, Dirty whole, string scenario) =>
        Require(pending.Count == 1 && pending[0] == whole,
            $"{scenario} did not retain exactly one full-screen conversion");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
