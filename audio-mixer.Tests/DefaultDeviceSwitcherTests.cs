using audio_mixer;
using Xunit;

namespace audio_mixer.Tests;

public class DefaultDeviceSwitcherTests
{
    private static PolicyBackend Backend(string name, Func<int> result, List<string>? calls = null) =>
        new(name, (id, role) => { calls?.Add($"{name}:{id}:{role}"); return result(); });

    private static PolicyBackend Throwing(string name, List<string>? calls = null) =>
        new(name, (id, role) => { calls?.Add(name); throw new InvalidCastException("E_NOINTERFACE"); });

    [Fact]
    public void UsesTheFirstBackendThatSucceeds()
    {
        var calls = new List<string>();
        var sw = new DefaultDeviceSwitcher(new[] { Backend("A", () => 0, calls), Backend("B", () => 0, calls) }, _ => { });

        Assert.True(sw.TrySet("dev1", ERole.Console));
        Assert.Equal(new[] { "A:dev1:Console" }, calls);
        Assert.Null(sw.LastError);
    }

    [Fact]
    public void FallsBackWhenTheFirstInterfaceIsMissing()
    {
        var calls = new List<string>();
        var sw = new DefaultDeviceSwitcher(new[] { Throwing("A", calls), Backend("B", () => 0, calls) }, _ => { });

        Assert.True(sw.TrySet("dev1", ERole.Multimedia));
        Assert.Equal(new[] { "A", "B:dev1:Multimedia" }, calls);
    }

    [Fact]
    public void FallsBackWhenTheFirstReturnsAFailureHresult()
    {
        var sw = new DefaultDeviceSwitcher(new[] { Backend("A", () => unchecked((int)0x80004002)), Backend("B", () => 0) }, _ => { });
        Assert.True(sw.TrySet("dev1", ERole.Communications));
    }

    [Fact]
    public void RemembersTheBackendThatWorked()
    {
        var calls = new List<string>();
        var sw = new DefaultDeviceSwitcher(new[] { Throwing("A", calls), Backend("B", () => 0, calls) }, _ => { });

        sw.TrySet("dev1", ERole.Console);
        calls.Clear();
        sw.TrySet("dev1", ERole.Multimedia);

        Assert.Equal(new[] { "B:dev1:Multimedia" }, calls); // A is not retried first
    }

    [Fact]
    public void FailureIsReportedLoudlyWhenEveryBackendFails()
    {
        var logged = new List<string>();
        var sw = new DefaultDeviceSwitcher(new[] { Throwing("A"), Backend("B", () => unchecked((int)0x80070057)) }, logged.Add);

        Assert.False(sw.TrySet("dev1", ERole.Console));
        Assert.Single(logged);
        Assert.Contains("A: InvalidCastException", logged[0]);
        Assert.Contains("B: HRESULT 0x80070057", logged[0]);
        Assert.Equal(logged[0], sw.LastError);
    }

    [Fact]
    public void EmptyDeviceIdFailsWithoutCallingAnyBackend()
    {
        var calls = new List<string>();
        var logged = new List<string>();
        var sw = new DefaultDeviceSwitcher(new[] { Backend("A", () => 0, calls) }, logged.Add);

        Assert.False(sw.TrySet("", ERole.Console));
        Assert.Empty(calls);
        Assert.Single(logged);
    }

    [Fact]
    public void ASuccessClearsThePreviousError()
    {
        bool fail = true;
        var sw = new DefaultDeviceSwitcher(new[] { Backend("A", () => fail ? 1 : 0) }, _ => { });
        Assert.False(sw.TrySet("dev1", ERole.Console));
        Assert.NotNull(sw.LastError);
        fail = false;
        Assert.True(sw.TrySet("dev1", ERole.Console));
        Assert.Null(sw.LastError);
    }

    [Fact]
    public void RealPolicyConfigInterfacesAreReachableOnThisMachine()
    {
        // A bogus id makes SetDefaultEndpoint fail with an HRESULT (nothing changes). Getting an HRESULT back at all proves
        // the CLSID/IID/vtable pairing works; a wrong IID throws InvalidCastException (E_NOINTERFACE) instead.
        int? Probe(PolicyBackend b)
        {
            try { return b.SetDefaultEndpoint("{not-a-real-endpoint}", ERole.Multimedia); }
            catch (Exception) { return null; }
        }

        var modern = Probe(DefaultDeviceSwitcher.PolicyConfig);
        var vista = Probe(DefaultDeviceSwitcher.PolicyConfigVista);

        Assert.True(modern != null || vista != null, "neither IPolicyConfig nor IPolicyConfigVista could be called");
        if (modern != null) Assert.NotEqual(0, modern.Value);
        if (vista != null) Assert.NotEqual(0, vista.Value);
    }

    [Fact]
    public void AudioEngineReportsFailureForAnUnknownDevice()
    {
        var engine = new AudioEngine();
        Assert.False(engine.SetDefaultDevice("{not-a-real-endpoint}", ERole.Multimedia));
        Assert.False(engine.SetDefaultDeviceAllRoles("{not-a-real-endpoint}"));
        Assert.NotNull(engine.LastDeviceError);
    }
}