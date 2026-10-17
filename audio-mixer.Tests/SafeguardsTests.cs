using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using audio_mixer;
using Xunit;

namespace audio_mixer.Tests;

public class BoundedCacheTests
{
    [Fact]
    public void NeverHoldsMoreThanCapacity()
    {
        var cache = new BoundedCache<string, string>(3);
        for (int i = 0; i < 100; i++) cache.Set("k" + i, "v" + i);

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGetValue("k0", out _));
        Assert.True(cache.TryGetValue("k99", out var v));
        Assert.Equal("v99", v);
    }

    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var cache = new BoundedCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGetValue("a", out _)); // a is now most recent
        cache.Set("c", 3);                          // evicts b

        Assert.True(cache.TryGetValue("a", out _));
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("c", out _));
    }

    [Fact]
    public void OverwritingAKeyDoesNotGrowTheCache()
    {
        var cache = new BoundedCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("a", 2);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetValue("a", out var v));
        Assert.Equal(2, v);
    }

    [Fact]
    public void AudioEngineIconCacheIsBounded()
    {
        // Feed more distinct exe paths than the cap through the real engine code path.
        var files = Directory.EnumerateFiles(Environment.SystemDirectory, "*.exe").Take(AudioEngine.IconCacheCapacity + 40).ToList();
        if (files.Count <= AudioEngine.IconCacheCapacity) return; // unusual machine; nothing to prove

        var engine = new AudioEngine();
        var get = typeof(AudioEngine).GetMethod("GetIconFromCacheOrExtract", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var file in files) get.Invoke(engine, new object[] { file });

        var cache = typeof(AudioEngine).GetField("_iconCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        int count = (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
        Assert.True(count <= AudioEngine.IconCacheCapacity, $"icon cache held {count} entries");
    }
}

public class ComReleaseTests
{
    private static object? NewComObject()
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        return type == null ? null : Activator.CreateInstance(type);
    }

    private sealed class Wrapper
    {
        public object? Com;
        public Wrapper? Child;
        public Wrapper? Cycle;
        public Action? Handler;
    }

    [Fact]
    public void ReleasesComObjectsReachableThroughWrapperFields()
    {
        var com1 = NewComObject();
        var com2 = NewComObject();
        if (com1 == null || com2 == null) return; // no scripting runtime on this machine

        var root = new Wrapper { Com = com1, Child = new Wrapper { Com = com2 } };
        root.Child.Cycle = root; // cycles must not loop forever

        int released = ComRelease.Release(root, _ => true);

        Assert.Equal(2, released);
        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(com1));
        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(com2));
    }

    [Fact]
    public void ReleasesARootThatIsItselfAComObject()
    {
        var com = NewComObject();
        if (com == null) return;
        Assert.Equal(1, ComRelease.Release(com));
        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(com));
    }

    [Fact]
    public void DoesNotWalkTypesTheFilterRejects()
    {
        var com = NewComObject();
        if (com == null) return;
        var root = new Wrapper { Com = com };

        Assert.Equal(0, ComRelease.Release(root, _ => false));
        Marshal.GetIUnknownForObject(com); // still alive
        Marshal.FinalReleaseComObject(com);
    }

    [Fact]
    public void IgnoresNullAndPlainManagedObjects()
    {
        Assert.Equal(0, ComRelease.Release(null));
        Assert.Equal(0, ComRelease.Release(new Wrapper { Handler = () => { } }, _ => true));
    }

    [Fact]
    public void ComScopeReleasesTrackedObjectsOnDispose()
    {
        var com = NewComObject();
        if (com == null) return;
        using (var scope = new ComScope())
        {
            scope.Track(com);
            Marshal.GetIUnknownForObject(com); // alive inside the scope
        }
        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(com));
    }

    [Fact]
    public void RepeatedAudioReadsDoNotAccumulateHandles()
    {
        var engine = new AudioEngine();
        if (engine.GetDevices(playbackOnly: true).Count == 0) return; // no audio endpoints (CI)

        for (int i = 0; i < 20; i++) { engine.GetSessions(); engine.GetDevices(); engine.GetMicMute(); } // warm up
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        int before = proc.HandleCount;

        // Without the GC running (as in the tray app, which allocates very little) the unreleased COM
        // objects used to hold roughly one registry-key handle per call.
        for (int i = 0; i < 300; i++) { engine.GetSessions(); engine.GetDevices(); engine.GetMicMute(); }

        proc.Refresh();
        int growth = proc.HandleCount - before;
        Assert.True(growth < 100, $"handle count grew by {growth} over 300 audio reads");
    }
}

public class TimeLimitedRunnerTests
{
    [Fact]
    public void ReturnsResultOfAFastCall()
    {
        var runner = new TimeLimitedRunner(maxStuck: 2);
        Assert.True(runner.TryRun(() => 42, TimeSpan.FromSeconds(5), out int result));
        Assert.Equal(42, result);
    }

    [Fact]
    public void ReturnsFalseWhenTheCallThrows()
    {
        var runner = new TimeLimitedRunner(maxStuck: 2);
        Assert.False(runner.TryRun<int>(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), out _));
        Assert.Equal(0, runner.Stuck);
    }

    [Fact]
    public void GivesUpOnABlockedCallWithoutWaitingForIt()
    {
        var runner = new TimeLimitedRunner(maxStuck: 2);
        using var release = new ManualResetEventSlim();

        var sw = Stopwatch.StartNew();
        bool ok = runner.TryRun(() => { release.Wait(); return 1; }, TimeSpan.FromMilliseconds(200), out int _);
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
        Assert.Equal(1, runner.Stuck);

        release.Set(); // let the worker finish; the stuck counter drains
        SpinWait.SpinUntil(() => runner.Stuck == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, runner.Stuck);
    }

    [Fact]
    public void FailsFastOnceTooManyCallsAreStuck()
    {
        var runner = new TimeLimitedRunner(maxStuck: 2);
        using var release = new ManualResetEventSlim();
        for (int i = 0; i < 2; i++)
            Assert.False(runner.TryRun(() => { release.Wait(); return 1; }, TimeSpan.FromMilliseconds(100), out int _));

        int executed = 0;
        var sw = Stopwatch.StartNew();
        bool ok = runner.TryRun(() => { Interlocked.Increment(ref executed); return 1; }, TimeSpan.FromSeconds(5), out int _);
        sw.Stop();

        Assert.False(ok);
        Assert.Equal(0, executed);
        Assert.True(sw.ElapsedMilliseconds < 500);
        release.Set();
    }
}

public class ShutdownWatchdogTests
{
    [Fact]
    public void ExitsAfterTheGracePeriodAndNotBefore()
    {
        using var exited = new ManualResetEventSlim();
        int exitCode = -1;
        var dog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5),
            code => { exitCode = code; exited.Set(); }, () => { });

        Assert.True(dog.Arm());
        Assert.False(exited.Wait(100)); // still inside the grace period
        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void KillsTheProcessWhenExitItselfHangs()
    {
        using var killed = new ManualResetEventSlim();
        using var neverSet = new ManualResetEventSlim();
        var dog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200),
            _ => neverSet.Wait(), // simulates Environment.Exit blocked on a stuck thread
            () => killed.Set());

        dog.Arm();
        Assert.True(killed.Wait(TimeSpan.FromSeconds(5)), "kill action was not invoked");
    }

    [Fact]
    public void ArmingTwiceOnlyStartsOneCountdown()
    {
        int exits = 0;
        var dog = new ShutdownWatchdog(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50),
            _ => Interlocked.Increment(ref exits), () => { });

        Assert.True(dog.Arm());
        Assert.False(dog.Arm());
        Assert.True(dog.IsArmed);
        Thread.Sleep(500);
        Assert.Equal(1, exits);
    }
}