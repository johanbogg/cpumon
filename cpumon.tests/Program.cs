using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    static int Main()
    {
        try
        {
            TestReceiveChunkCompletesAndValidatesOffsets();
            TestReceiveChunkReplacesDuplicateTransfer();
            TestLineLengthLimitedStream();
            TestUpdateIntegrity();
            TestSendPacerWakesOnModeChange();
            TestSendPacerWakesOnDemand();
            TestApprovedClientAliasPersists();
            TestApprovedClientForgetPersists();
            TestClientNeedsUpdate();
            TestServerEngineInitialState();
            TestServerEngineRegenerateToken();
            TestServerEnginePendingApprovalMissing();
            TestVersionComparisonAcrossMinor();
            Console.WriteLine("cpumon smoke tests passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static void TestReceiveChunkCompletesAndValidatesOffsets()
    {
        using var td = new TempDir();
        var uploads = new ConcurrentDictionary<string, FileStream>();
        var first = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 0,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")),
            IsLast = false
        };
        Assert(FileBrowserService.ReceiveChunk(first, uploads, td.Path) == "", "first chunk should not complete");

        var bad = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 9,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("def")),
            IsLast = true
        };
        Assert(FileBrowserService.ReceiveChunk(bad, uploads, td.Path).StartsWith("Upload error"), "offset mismatch should fail");
        Assert(uploads.ContainsKey("t1"), "offset mismatch should keep stream for caller cleanup/retry decision");

        var last = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 3,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("def")),
            IsLast = true
        };
        string result = FileBrowserService.ReceiveChunk(last, uploads, td.Path);
        Assert(result.StartsWith("Upload complete"), "last chunk should complete");
        Assert(!uploads.ContainsKey("t1"), "completed transfer should be removed");
        Assert(File.ReadAllText(Path.Combine(td.Path, "sample.bin")) == "abcdef", "file content should match chunks");
    }

    static void TestReceiveChunkReplacesDuplicateTransfer()
    {
        using var td = new TempDir();
        var uploads = new ConcurrentDictionary<string, FileStream>();
        var chunk = new FileChunkData
        {
            TransferId = "dup",
            FileName = "one.txt",
            Offset = 0,
            TotalSize = 3,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("one")),
            IsLast = false
        };
        FileBrowserService.ReceiveChunk(chunk, uploads, td.Path);
        FileBrowserService.ReceiveChunk(new FileChunkData
        {
            TransferId = "dup",
            FileName = "two.txt",
            Offset = 0,
            TotalSize = 3,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("two")),
            IsLast = true
        }, uploads, td.Path);
        Assert(File.ReadAllText(Path.Combine(td.Path, "two.txt")) == "two", "duplicate transfer should restart cleanly");
    }

    static void TestLineLengthLimitedStream()
    {
        byte[] okBytes = Encoding.UTF8.GetBytes("hello\n");
        using (var ok = new LineLengthLimitedStream(new MemoryStream(okBytes)))
        {
            byte[] buf = new byte[okBytes.Length];
            Assert(ok.Read(buf, 0, buf.Length) == okBytes.Length, "short line should read");
        }

        byte[] longBytes = new byte[LineLengthLimitedStream.MaxLineBytes + 1];
        Array.Fill<byte>(longBytes, (byte)'a');
        using var tooLong = new LineLengthLimitedStream(new MemoryStream(longBytes));
        AssertThrows<IOException>(() => tooLong.Read(new byte[longBytes.Length], 0, longBytes.Length), "long line should throw");
    }

    static void TestUpdateIntegrity()
    {
        using var td = new TempDir();
        string file = Path.Combine(td.Path, "update.bin");
        File.WriteAllText(file, "payload");
        string hash;
        using (var fs = File.OpenRead(file))
            hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(fs));
        Assert(UpdateIntegrity.VerifySha256Base64(file, hash, out var actual), "valid hash should pass");
        Assert(actual == hash, "actual hash should be reported");
        Assert(!UpdateIntegrity.VerifySha256Base64(file, null, out _), "missing hash should fail");
        Assert(!UpdateIntegrity.VerifySha256Base64(file, Convert.ToBase64String(new byte[32]), out _), "wrong hash should fail");
    }

    static void TestSendPacerWakesOnModeChange()
    {
        var pacer = new SendPacer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = Task.Run(() => pacer.Wait(cts.Token));
        Thread.Sleep(100);
        pacer.Mode = "monitor";
        Assert(task.Wait(1000), "mode change should wake pacer");
    }

    static void TestSendPacerWakesOnDemand()
    {
        var pacer = new SendPacer { Mode = "monitor" };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = Task.Run(() => pacer.Wait(cts.Token));
        Thread.Sleep(100);
        pacer.Wake();
        Assert(task.Wait(1000), "explicit wake should wake pacer");
    }

    static void TestApprovedClientAliasPersists()
    {
        using var td = new TempDir();
        string path = Path.Combine(td.Path, "approved_clients.json");
        var store = new ApprovedClientStore(path);
        store.Approve("DESKTOP-123", "key", "192.168.1.10", "salt");
        Assert(store.GetAlias("DESKTOP-123") == "", "new approved client should not have an alias");

        store.SetAlias("DESKTOP-123", "Kids PC");
        Assert(store.GetAlias("DESKTOP-123") == "Kids PC", "alias should be returned immediately");

        var reloaded = new ApprovedClientStore(path);
        Assert(reloaded.GetAlias("DESKTOP-123") == "Kids PC", "alias should persist after reload");
        Assert(reloaded.IsOk("DESKTOP-123", "key"), "alias persistence should not break stored key");
    }

    static void TestApprovedClientForgetPersists()
    {
        using var td = new TempDir();
        string path = Path.Combine(td.Path, "approved_clients.json");
        var store = new ApprovedClientStore(path);
        store.Approve("DESKTOP-A", "keyA", "10.0.0.1", "saltA");
        store.Approve("DESKTOP-B", "keyB", "10.0.0.2", "saltB");
        Assert(store.IsOk("DESKTOP-A", "keyA"), "DESKTOP-A should be approved");
        Assert(store.IsOk("DESKTOP-B", "keyB"), "DESKTOP-B should be approved");

        store.Forget("DESKTOP-A");
        Assert(!store.IsOk("DESKTOP-A", "keyA"), "forgotten client should not be approved in memory");

        var reloaded = new ApprovedClientStore(path);
        Assert(!reloaded.IsOk("DESKTOP-A", "keyA"), "forgotten client should be persisted as removed");
        Assert(reloaded.IsOk("DESKTOP-B", "keyB"), "non-forgotten client should still be approved after reload");
    }

    static void TestClientNeedsUpdate()
    {
        Assert(!ServerEngine.ClientNeedsUpdate(""), "empty version should not flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate("not-a-version"), "unparseable version should not flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate(Proto.AppVersion), "matching server version should not flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate(Proto.AppVersion + ".0"), "matching four-part version should not flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate("v" + Proto.AppVersion), "matching tagged version should not flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate(Proto.AppVersion + "-linux"), "matching suffixed version should not flag as outdated");
        Assert(ServerEngine.ClientNeedsUpdate("0.0.1"), "older version should flag as outdated");
        Assert(ServerEngine.ClientNeedsUpdate("0.0.1-linux"), "older Linux version should flag as outdated");
        Assert(!ServerEngine.ClientNeedsUpdate("999.0.0"), "newer version should not flag as outdated");
    }

    static void TestServerEngineInitialState()
    {
        using var engine = new ServerEngine(noBroadcast: true);
        Assert(engine.BroadcastDisabled, "BroadcastDisabled flag should reflect ctor argument");
        Assert(!string.IsNullOrEmpty(engine.Token), "engine should generate a token at construction");
        Assert(engine.ConnectionCount == 0, "connection count should start at zero");
        Assert(engine.Clients.IsEmpty, "clients dictionary should start empty");
        Assert(engine.PendingApprovals.IsEmpty, "pending approvals should start empty");
        Assert(engine.AvailableUpdate == null, "no update should be available before the checker runs");
    }

    static void TestServerEngineRegenerateToken()
    {
        using var engine = new ServerEngine(noBroadcast: true);
        string original = engine.Token;
        DateTime originalAt = engine.TokenIssuedAt;
        Thread.Sleep(20);
        engine.RegenerateToken();
        Assert(engine.Token != original, "RegenerateToken should produce a different token");
        Assert(engine.TokenIssuedAt >= originalAt, "RegenerateToken should refresh the issued-at timestamp");
    }

    static void TestVersionComparisonAcrossMinor()
    {
        Assert(Versioning.IsNewer("1.1.0", "1.0.999"), "1.1.0 > 1.0.999");
        Assert(Versioning.IsNewer("1.1.0", "1.0.148"), "1.1.0 > 1.0.148");
        Assert(Versioning.IsNewer("1.2.0", "1.1.999"), "1.2.0 > 1.1.999");
        Assert(Versioning.IsNewer("2.0.0", "1.999.999"), "2.0.0 > 1.999.999");
        Assert(Versioning.IsOlder("1.0.111-linux", "1.1.2"), "Linux suffix should still compare numerically");
        Assert(!Versioning.IsNewer("v1.1.0.0", "1.1.0"), "1.1.0.0 should normalize to 1.1.0");
        Assert(Versioning.TryNormalize("v1.1.2-linux", out var linuxVersion, out var linuxText) && linuxVersion.ToString(3) == "1.1.2" && linuxText == "1.1.2", "suffixed versions should normalize to three parts");
    }

    static void TestServerEnginePendingApprovalMissing()
    {
        using var engine = new ServerEngine(noBroadcast: true);
        Assert(!engine.ApprovePending("no-such-machine"), "approving unknown machine should return false");
        Assert(!engine.RejectPending("no-such-machine"), "rejecting unknown machine should return false");
        Assert(!engine.RequestRestart("no-such-machine"), "restart on unknown machine should return false");
        Assert(!engine.RequestShutdown("no-such-machine"), "shutdown on unknown machine should return false");
        Assert(!engine.WakeOffline("no-such-machine"), "wake on machine without MAC should return false");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cpumon-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
