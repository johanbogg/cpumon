using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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
            TestReceiveChunkRejectsUnsafeNames();
            TestRenameRejectsTraversal();
            TestLineLengthLimitedStream();
            TestSecurityTokenFormat();
            TestUpdateIntegrity();
            TestSendPacerWakesOnModeChange();
            TestSendPacerWakesOnDemand();
            TestApprovedClientAliasPersists();
            TestApprovedClientForgetPersists();
            TestApprovedClientApprovePreservesMetadata();
            TestClientNeedsUpdate();
            TestServerEngineInitialState();
            TestServerEngineRegenerateToken();
            TestServerEnginePendingApprovalMissing();
            TestVersionComparisonAcrossMinor();
            TestLinuxUpdatePayload();
            TestCpuReportKeepsCoresOnDemand();
            TestDashboardStateInitialState();
            TestDashboardStatePendingApprovalsSorted();
            TestDashboardStateWaitingClientsStayVisibleUnderOsFilters();
            TestDashboardStateClientProjectionFlags();
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
        Assert(!uploads.ContainsKey("t1"), "offset mismatch should remove the stale upload");
        Assert(!File.Exists(Path.Combine(td.Path, "sample.bin.tmp")), "offset mismatch should delete the temp file");

        var retry = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 0,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("abcdef")),
            IsLast = true
        };
        string result = FileBrowserService.ReceiveChunk(retry, uploads, td.Path);
        Assert(result.StartsWith("Upload complete"), "retry from offset 0 should complete");
        Assert(!uploads.ContainsKey("t1"), "completed transfer should be removed");
        Assert(File.ReadAllText(Path.Combine(td.Path, "sample.bin")) == "abcdef", "file content should match retried chunk");
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

    static void TestReceiveChunkRejectsUnsafeNames()
    {
        using var td = new TempDir();
        var uploads = new ConcurrentDictionary<string, FileStream>();
        var chunk = new FileChunkData
        {
            TransferId = "unsafe",
            FileName = "bad:name.txt",
            Offset = 0,
            TotalSize = 1,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")),
            IsLast = true
        };
        string result = FileBrowserService.ReceiveChunk(chunk, uploads, td.Path);
        Assert(result.StartsWith("Upload error"), "unsafe upload filename should fail");
        Assert(!uploads.ContainsKey("unsafe"), "unsafe upload should not keep stream state");
    }

    static void TestRenameRejectsTraversal()
    {
        using var td = new TempDir();
        string original = Path.Combine(td.Path, "old.txt");
        File.WriteAllText(original, "x");
        string result = FileBrowserService.RenamePath(original, "..\\outside.txt");
        Assert(result.StartsWith("Rename error"), "rename traversal should fail");
        Assert(File.Exists(original), "failed rename should leave original in place");
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

    static void TestSecurityTokenFormat()
    {
        string token = Security.GenToken();
        Assert(token.Length == 24, "invite token should be 24 hex characters");
        foreach (char ch in token)
            Assert(Uri.IsHexDigit(ch), "invite token should contain only hex characters");
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

    static void TestApprovedClientApprovePreservesMetadata()
    {
        using var td = new TempDir();
        string path = Path.Combine(td.Path, "approved_clients.json");
        var store = new ApprovedClientStore(path);

        store.Approve("DESKTOP-X", "key1", "10.0.0.1", "salt1");
        store.SetAlias("DESKTOP-X", "Kitchen PC");
        store.SetMac("DESKTOP-X", "AA:BB:CC:DD:EE:FF");
        store.SetPaw("DESKTOP-X", true);
        store.Revoke("DESKTOP-X");
        Assert(!store.IsOk("DESKTOP-X", "key1"), "client should be revoked");

        store.Approve("DESKTOP-X", "key2", "10.0.0.2", "salt2");
        Assert(store.IsOk("DESKTOP-X", "key2"), "re-approval should reactivate with new key");
        Assert(!store.IsOk("DESKTOP-X", "key1"), "old key should no longer authenticate");
        Assert(store.GetAlias("DESKTOP-X") == "Kitchen PC", "alias should survive re-approval");
        Assert(store.GetMac("DESKTOP-X") == "AA:BB:CC:DD:EE:FF", "MAC should survive re-approval");
        Assert(store.IsPaw("DESKTOP-X"), "PAW flag should survive re-approval");

        var reloaded = new ApprovedClientStore(path);
        Assert(reloaded.IsOk("DESKTOP-X", "key2"), "re-approved entry should reload with the new key");
        Assert(reloaded.GetAlias("DESKTOP-X") == "Kitchen PC", "alias should reload after re-approval");
        Assert(reloaded.GetMac("DESKTOP-X") == "AA:BB:CC:DD:EE:FF", "MAC should reload after re-approval");
        Assert(reloaded.IsPaw("DESKTOP-X"), "PAW flag should reload after re-approval");
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

    static void TestLinuxUpdatePayload()
    {
        using var td = new TempDir();
        const string realScript = "#!/usr/bin/env python3\n# cpumon Linux client\nVERSION = \"1.2.3-linux\"\n";

        string py = Path.Combine(td.Path, "cpumon.py");
        File.WriteAllText(py, realScript);
        Assert(LinuxUpdatePayload.TryRead(py, out var name1, out var bytes1, out var err1), "valid cpumon.py should pass: " + err1);
        Assert(name1 == "cpumon.py", "fileName should be cpumon.py for a .py input");
        Assert(Encoding.UTF8.GetString(bytes1) == realScript, "bytes should match the script");

        string empty = Path.Combine(td.Path, "empty.py");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert(!LinuxUpdatePayload.TryRead(empty, out _, out _, out var err2), "empty file should fail");
        Assert(err2.Contains("empty", StringComparison.OrdinalIgnoreCase), "error should mention empty");

        string bogus = Path.Combine(td.Path, "bogus.py");
        File.WriteAllText(bogus, "print('hello world')\n");
        Assert(!LinuxUpdatePayload.TryRead(bogus, out _, out _, out var err3), "non-cpumon python should fail validation");
        Assert(err3.Contains("does not look like cpumon.py", StringComparison.Ordinal), "error should mention header check");

        string zipPath = Path.Combine(td.Path, "cpumon-linux-1.2.3.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("cpumon-linux-1.2.3/cpumon.py");
            using var es = entry.Open();
            byte[] payload = Encoding.UTF8.GetBytes(realScript);
            es.Write(payload, 0, payload.Length);
        }
        Assert(LinuxUpdatePayload.TryRead(zipPath, out var name4, out var bytes4, out var err4), "release zip with cpumon.py should pass: " + err4);
        Assert(name4 == "cpumon.py", "fileName should be cpumon.py for a zip input");
        Assert(Encoding.UTF8.GetString(bytes4) == realScript, "extracted bytes should match script");

        string emptyZip = Path.Combine(td.Path, "no-py.zip");
        using (var fs = File.Create(emptyZip))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("README.txt");
            using var es = entry.Open();
            byte[] payload = Encoding.UTF8.GetBytes("nothing to see");
            es.Write(payload, 0, payload.Length);
        }
        Assert(!LinuxUpdatePayload.TryRead(emptyZip, out _, out _, out var err5), "zip without cpumon.py should fail");
        Assert(err5.Contains("did not contain cpumon.py", StringComparison.Ordinal), "error should mention missing cpumon.py");
    }

    static void TestCpuReportKeepsCoresOnDemand()
    {
        var cores = new[]
        {
            new CoreSnapshot(0, 4200, 52, 12),
            new CoreSnapshot(1, 4100, 50, 9)
        };
        var summary = new CpuSnapshot(true, 2, 51, 4150, 10, 18, Array.Empty<CoreSnapshot>());
        var normal = ReportBuilder.Build(summary, "Test CPU");
        Assert(normal.CoreCount == 2, "normal report should keep core count");
        Assert(normal.Cores.Count == 0, "normal report should not carry per-core telemetry");

        var detail = ReportBuilder.BuildCpuDetail(new CpuSnapshot(true, 2, 51, 4150, 10, 18, cores), "Test CPU");
        Assert(detail.CoreCount == 2, "detail report should keep core count");
        Assert(detail.Cores.Count == 2, "detail report should include per-core telemetry on demand");
        Assert(detail.Cores[0].Index == 0 && detail.Cores[1].Index == 1, "detail report should preserve core order");
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

    static void TestDashboardStateInitialState()
    {
        using var engine = new ServerEngine(noBroadcast: true);
        var builder = new ServerDashboardStateBuilder(engine);
        var state = builder.Build(Array.Empty<string>(), "all", "name");

        Assert(state.BroadcastDisabled, "dashboard state should include broadcast mode");
        Assert(state.Token == engine.Token, "dashboard state should include the current token");
        Assert(state.ConnectionCount == 0, "initial dashboard state should have no connections");
        Assert(state.AuthenticatedClientCount == 0, "initial dashboard state should have no authenticated clients");
        Assert(state.Clients.Count == 0, "initial dashboard state should have no client cards");
        Assert(state.PendingApprovals.Count == 0, "initial dashboard state should have no pending approvals");
        var filteredState = builder.Build(Array.Empty<string>(), "windows", "name");
        Assert(filteredState.OfflineClients.Count == 0, "offline clients should be hidden outside the all filter");
    }

    static void TestDashboardStatePendingApprovalsSorted()
    {
        var engine = new ServerEngine(noBroadcast: true);
        engine.PendingApprovals["zeta"] = new PendingClientApproval
        {
            MachineName = "zeta",
            Ip = "10.0.0.3",
            Remote = "10.0.0.3:1234",
            Client = FakeRemoteClient(),
            ClientVersion = "1.0.1"
        };
        engine.PendingApprovals["alpha"] = new PendingClientApproval
        {
            MachineName = "alpha",
            Ip = "10.0.0.2",
            Remote = "10.0.0.2:1234",
            Client = FakeRemoteClient(),
            ClientVersion = "1.0.2"
        };

        var state = new ServerDashboardStateBuilder(engine).Build(Array.Empty<string>(), "all", "name");
        Assert(state.PendingApprovals.Count == 2, "dashboard state should include pending approvals");
        Assert(state.PendingApprovals[0].MachineName == "alpha", "pending approvals should sort by machine name");
        Assert(state.PendingApprovals[1].MachineName == "zeta", "pending approvals should sort by machine name");
        Assert(state.PendingApprovals[0].ClientVersion == "1.0.2", "pending approval projection should keep client version");
    }

    static void TestDashboardStateWaitingClientsStayVisibleUnderOsFilters()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var waiting = FakeRemoteClient();
        waiting.MachineName = "waiting-client";
        waiting.ClientVersion = Proto.AppVersion;
        waiting.LastReport = null;
        engine.Clients[waiting.MachineName] = waiting;

        var builder = new ServerDashboardStateBuilder(engine);
        Assert(builder.Build(Array.Empty<string>(), "windows", "name").Clients.Count == 1, "waiting client should remain visible in Windows filter");
        Assert(builder.Build(Array.Empty<string>(), "linux", "name").Clients.Count == 1, "waiting client should remain visible in Linux filter");
    }

    static void TestDashboardStateClientProjectionFlags()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var linux = FakeRemoteClient();
        linux.MachineName = "linux-box";
        linux.ClientVersion = "0.0.1-linux";
        linux.LastSeen = DateTime.UtcNow.AddSeconds(-90);
        linux.LastReport = new MachineReport { MachineName = "linux-box", OsVersion = "Linux Debian", RamTotalGB = 8, RamUsedGB = 2 };
        engine.Clients[linux.MachineName] = linux;

        var selected = new[] { "linux-box" };
        var state = new ServerDashboardStateBuilder(engine).Build(selected, "linux", "name");
        Assert(state.SelectedMachineNames.Contains("linux-box"), "dashboard state should copy selected machine names");
        Assert(state.Clients.Count == 1, "linux client should be visible under linux filter");
        var card = state.Clients[0];
        Assert(card.IsLinux, "linux client should be marked linux");
        Assert(card.IsOutdated, "older linux client should be marked outdated");
        Assert(card.IsStale, "stale client should be marked stale");
        Assert(!card.CanRdp, "linux client should not expose RDP capability");
        Assert(card.CanTerminal, "linux client should expose terminal capability");
        Assert(!ReferenceEquals(card.Report, linux.LastReport), "dashboard state should not expose the live report instance");
    }

    static RemoteClient FakeRemoteClient()
    {
        var client = (RemoteClient)RuntimeHelpers.GetUninitializedObject(typeof(RemoteClient));
        client.LastSeen = DateTime.UtcNow;
        client.ClientVersion = Proto.AppVersion;
        client.SendMode = "full";
        return client;
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
