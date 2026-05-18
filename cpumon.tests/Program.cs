using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            TestDashboardStateOsSortPlacesWindowsBeforeLinuxAndKeepsNameOrder();
            TestDashboardStateCapabilityFlagsForLinuxAndWindows();
            TestDashboardControllerOwnsFiltersSortAndSelection();
            TestDashboardControllerSelectAllVisibleRespectsOsFilter();
            TestDashboardControllerSelectOutdatedVisibleRespectsVersionComparison();
            TestDashboardControllerPurgeStaleClientsRemovesAndDeselects();
            TestDashboardControllerPendingDelegatesReturnFalseForMissing();
            TestDashboardControllerRestartRaisesWarningConfirmation();
            TestDashboardControllerForgetClientConfirmationInvokesEngineOnConfirm();
            TestDashboardControllerCopyTokenRaisesClipboardEvent();
            TestDashboardControllerUsesPlatformServicesWhenProvided();
            TestDashboardControllerToggleExpandedFlipsClient();
            TestDashboardControllerPushUpdatePicksLinuxFilterForLinuxClient();
            TestDashboardControllerSetOfflineMacRoutesPromptToEngine();
            TestDashboardControllerOpenTerminalCarriesShellArgument();
            TestDashboardControllerSubmitUserMessageDropsBlank();
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

    static void TestDashboardStateOsSortPlacesWindowsBeforeLinuxAndKeepsNameOrder()
    {
        var engine = new ServerEngine(noBroadcast: true);
        AddReportedClient(engine, "z-win", "Microsoft Windows 11", Proto.AppVersion);
        AddReportedClient(engine, "a-lnx", "Linux Debian", "0.0.1-linux");
        AddReportedClient(engine, "m-win", "Microsoft Windows 10", Proto.AppVersion);
        AddReportedClient(engine, "b-lnx", "Linux Ubuntu", "0.0.2-linux");

        var controller = new ServerDashboardController(engine);
        Assert(controller.CycleSortMode() == "os", "sort mode should cycle to os");
        var names = controller.GetState().Clients.Select(c => c.MachineName).ToList();

        Assert(names.SequenceEqual(new[] { "m-win", "z-win", "a-lnx", "b-lnx" }), "OS sort should place Windows clients before Linux and sort names within each group");
    }

    static void TestDashboardStateCapabilityFlagsForLinuxAndWindows()
    {
        var engine = new ServerEngine(noBroadcast: true);
        AddReportedClient(engine, "win-box", "Microsoft Windows 11", Proto.AppVersion);
        AddReportedClient(engine, "linux-box", "Linux Debian", "0.0.1-linux");

        var cards = new ServerDashboardController(engine).GetState().Clients.ToDictionary(c => c.MachineName, StringComparer.OrdinalIgnoreCase);
        var win = cards["win-box"];
        var linux = cards["linux-box"];

        Assert(win.CanRdp, "Windows clients should expose RDP capability");
        Assert(win.CanTerminal, "Windows clients should expose terminal capability");
        Assert(!win.CanBash, "Windows clients should not expose Bash launcher capability");
        Assert(win.CanServices, "Windows clients should expose services capability");
        Assert(win.CanScreenshot, "Windows clients should expose screenshot capability");
        Assert(win.CanEvents, "Windows clients should expose event viewer capability");
        Assert(win.CanCpuDetail, "Windows clients should expose CPU detail capability");

        Assert(!linux.CanRdp, "Linux clients should not expose RDP capability");
        Assert(linux.CanTerminal, "Linux clients should expose terminal capability");
        Assert(linux.CanBash, "Linux clients should expose Bash launcher capability");
        Assert(linux.CanServices, "Linux clients should expose services capability");
        Assert(!linux.CanScreenshot, "Linux clients should not expose screenshot capability");
        Assert(!linux.CanEvents, "Linux clients should not expose Windows event viewer capability");
        Assert(!linux.CanCpuDetail, "Linux clients should not expose CPU detail capability");
    }

    static void TestDashboardControllerOwnsFiltersSortAndSelection()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var linux = FakeRemoteClient();
        linux.MachineName = "linux-box";
        linux.ClientVersion = "0.0.1-linux";
        linux.LastReport = new MachineReport { MachineName = linux.MachineName, OsVersion = "Linux Debian" };
        engine.Clients[linux.MachineName] = linux;

        var windows = FakeRemoteClient();
        windows.MachineName = "win-box";
        windows.ClientVersion = Proto.AppVersion;
        windows.LastReport = new MachineReport { MachineName = windows.MachineName, OsVersion = "Microsoft Windows 11" };
        engine.Clients[windows.MachineName] = windows;

        var controller = new ServerDashboardController(engine);
        Assert(controller.GetState().OsFilter == "all", "dashboard controller should default to all clients");
        Assert(controller.GetState().SortMode == "name", "dashboard controller should default to name sort");

        controller.ToggleSelection("WIN-BOX");
        Assert(controller.GetState().SelectedMachineNames.Contains("win-box"), "dashboard controller selection should be case-insensitive");
        controller.ToggleSelection("win-box");
        Assert(controller.GetState().SelectedMachineNames.Count == 0, "toggling a selected machine should clear it");

        Assert(controller.CycleOsFilter() == "windows", "first OS filter cycle should select windows");
        controller.SelectAllVisible();
        var windowsState = controller.GetState();
        Assert(windowsState.Clients.Count == 1 && windowsState.Clients[0].MachineName == "win-box", "windows filter should expose only the Windows client");
        Assert(windowsState.SelectedMachineNames.SetEquals(new[] { "win-box" }), "select all should select visible clients only");

        Assert(controller.CycleOsFilter() == "linux", "second OS filter cycle should select linux");
        controller.SelectOutdatedVisible();
        var linuxState = controller.GetState();
        Assert(linuxState.Clients.Count == 1 && linuxState.Clients[0].MachineName == "linux-box", "linux filter should expose only the Linux client");
        Assert(linuxState.SelectedMachineNames.SetEquals(new[] { "win-box", "linux-box" }), "select outdated should add outdated visible clients");

        Assert(controller.CycleSortMode() == "os", "sort mode should cycle to os");
        controller.ClearSelection();
        Assert(controller.GetState().SelectedMachineNames.Count == 0, "clear selection should remove selected clients");
    }

    static void TestDashboardControllerSelectAllVisibleRespectsOsFilter()
    {
        var engine = new ServerEngine(noBroadcast: true);
        AddReportedClient(engine, "win-box", "Microsoft Windows 11", Proto.AppVersion);
        AddReportedClient(engine, "linux-box", "Linux Debian", "0.0.1-linux");

        var controller = new ServerDashboardController(engine);
        Assert(controller.CycleOsFilter() == "windows", "first OS filter cycle should select windows");
        controller.SelectAllVisible();

        var selected = controller.GetState().SelectedMachineNames;
        Assert(selected.SetEquals(new[] { "win-box" }), "SelectAllVisible must select only clients visible under the current OS filter");
    }

    static void TestDashboardControllerSelectOutdatedVisibleRespectsVersionComparison()
    {
        var engine = new ServerEngine(noBroadcast: true);
        AddReportedClient(engine, "old-box", "Microsoft Windows 11", "0.0.1");
        AddReportedClient(engine, "current-box", "Microsoft Windows 11", Proto.AppVersion);
        AddReportedClient(engine, "future-box", "Microsoft Windows 11", "9.9.9");

        var controller = new ServerDashboardController(engine);
        controller.SelectOutdatedVisible();

        Assert(controller.GetState().SelectedMachineNames.SetEquals(new[] { "old-box" }), "SelectOutdatedVisible should select only visible clients older than the server version");
    }

    static void TestDashboardControllerPurgeStaleClientsRemovesAndDeselects()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var fresh = AddReportedClient(engine, "fresh", "Microsoft Windows 11", Proto.AppVersion);
        fresh.LastSeen = DateTime.UtcNow;
        var stale = AddReportedClient(engine, "stale", "Microsoft Windows 11", Proto.AppVersion);
        stale.LastSeen = DateTime.UtcNow.AddMinutes(-5);

        var controller = new ServerDashboardController(engine);
        controller.ToggleSelection("stale");
        controller.PurgeStaleClients();

        Assert(!engine.Clients.ContainsKey("stale"), "PurgeStaleClients should remove clients older than the stale threshold");
        Assert(engine.Clients.ContainsKey("fresh"), "PurgeStaleClients should keep fresh clients");
        Assert(!controller.SelectedMachineNames.Contains("stale"), "PurgeStaleClients should remove stale machines from dashboard selection");
    }

    static void TestDashboardControllerPendingDelegatesReturnFalseForMissing()
    {
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true));
        Assert(!controller.ApprovePending("no-such-machine"), "controller approving missing pending client should return false");
        Assert(!controller.RejectPending("no-such-machine"), "controller rejecting missing pending client should return false");
    }

    static void TestDashboardControllerRestartRaisesWarningConfirmation()
    {
        var platform = new FakeServerPlatformServices { ConfirmReturn = false };
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true), platform);
        controller.RestartClient("box-a");
        Assert(platform.LastConfirm != null, "RestartClient should route confirmation through platform services");
        Assert(platform.LastConfirm!.Value.Kind == DashboardConfirmKind.Warning, "restart confirmation should be a warning");
        Assert(platform.LastConfirm.Value.Message.Contains("box-a"), "restart confirmation should mention machine name");
    }

    static void TestDashboardControllerForgetClientConfirmationInvokesEngineOnConfirm()
    {
        var platform = new FakeServerPlatformServices { ConfirmReturn = false };
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true), platform);
        controller.ForgetClient("doomed-box");
        Assert(platform.LastConfirm != null, "ForgetClient should route confirmation through platform services");
        Assert(platform.LastConfirm!.Value.Kind == DashboardConfirmKind.Question, "forget confirmation should default to question");
        Assert(platform.LastConfirm.Value.Message.Contains("doomed-box"), "forget confirmation should mention machine name");
    }

    static void TestDashboardControllerCopyTokenRaisesClipboardEvent()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(engine, platform);
        controller.CopyToken();
        Assert(platform.ClipboardText == engine.Token, "CopyToken should publish current engine token to clipboard");
    }

    static void TestDashboardControllerUsesPlatformServicesWhenProvided()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var linux = FakeRemoteClient();
        linux.MachineName = "lnx";
        linux.LastReport = new MachineReport { MachineName = linux.MachineName, OsVersion = "Linux Debian" };
        engine.Clients[linux.MachineName] = linux;

        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(engine, platform);

        controller.CopyToken();
        Assert(platform.ClipboardText == engine.Token, "CopyToken should route clipboard text through platform services");

        platform.ConfirmReturn = false;
        controller.RestartClient("box-a");
        Assert(platform.LastConfirm != null, "RestartClient should route confirmation through platform services");
        Assert(platform.LastConfirm!.Value.Kind == DashboardConfirmKind.Warning, "platform confirmation should preserve warning kind");

        controller.RequestSetOfflineMac("offline-box");
        Assert(platform.LastPrompt != null, "RequestSetOfflineMac should route prompt through platform services");
        Assert(platform.LastPrompt!.Value.Label.Contains("offline-box"), "platform prompt should preserve machine label");

        controller.PushUpdate("lnx");
        Assert(platform.LastPickFile != null, "PushUpdate should route file picker through platform services");
        Assert(platform.LastPickFile!.Value.Filter.Contains("*.py"), "platform file picker should preserve Linux filter");

        controller.ShowApprovedClients();
        Assert(platform.ShowApprovedCalled, "ShowApprovedClients should route through platform services");

        controller.ShowAlerts();
        Assert(platform.ShowAlertsCalled, "ShowAlerts should route through platform services");

        controller.RequestProcesses("no-such-box");
        Assert(platform.ShowProcessMachine == "no-such-box", "RequestProcesses should route through platform services");

        controller.ShowHealth("lnx");
        Assert(platform.ShowHealthMachine == "lnx", "ShowHealth should route through platform services");

        controller.OpenTerminal("lnx", "bash");
        Assert(platform.ShowTerminalCall == ("lnx", "bash"), "OpenTerminal should route shell argument through platform services");

        controller.OpenFileBrowser("lnx", "/var/log");
        Assert(platform.ShowFileBrowserCall == ("lnx", "/var/log"), "OpenFileBrowser should route initial path through platform services");

        controller.OpenRdp("lnx");
        Assert(platform.ShowRdpMachine == "lnx", "OpenRdp should route through platform services");

        controller.SendUserMessage("lnx");
        Assert(platform.PromptSendUserMessageCall?.Machine == "lnx", "SendUserMessage should route through platform services");
        platform.PromptSendUserMessageCall?.OnSubmit("hello");
    }

    static void TestDashboardControllerToggleExpandedFlipsClient()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var client = FakeRemoteClient();
        client.MachineName = "card-box";
        client.Expanded = false;
        engine.Clients[client.MachineName] = client;
        var controller = new ServerDashboardController(engine);
        controller.ToggleClientExpanded("card-box");
        Assert(client.Expanded, "ToggleClientExpanded should expand a collapsed client");
        controller.ToggleClientExpanded("card-box");
        Assert(!client.Expanded, "ToggleClientExpanded should collapse an expanded client");
        controller.ToggleClientExpanded("no-such-box");
    }

    static void TestDashboardControllerPushUpdatePicksLinuxFilterForLinuxClient()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var linux = FakeRemoteClient();
        linux.MachineName = "lnx";
        linux.LastReport = new MachineReport { MachineName = linux.MachineName, OsVersion = "Linux Debian" };
        engine.Clients[linux.MachineName] = linux;
        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(engine, platform);
        controller.PushUpdate("lnx");
        Assert(platform.LastPickFile != null, "PushUpdate should route file picker through platform services for known client");
        Assert(platform.LastPickFile!.Value.Filter.Contains("*.py"), "Linux client picker should include .py filter");
        controller.PushUpdate("no-such-box");
    }

    static void TestDashboardControllerOpenTerminalCarriesShellArgument()
    {
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true));
        DashboardDialogRequest? captured = null;
        controller.DialogRequested += req => captured = req;
        controller.OpenTerminal("shell-box", "powershell");
        Assert(captured != null, "OpenTerminal should raise DialogRequested");
        Assert(captured!.Kind == "terminal", "terminal dialog should carry kind=terminal");
        Assert(captured.MachineName == "shell-box", "terminal dialog should carry the machine name");
        Assert(captured.Argument == "powershell", "terminal dialog should carry the requested shell as argument");
    }

    static void TestDashboardControllerSubmitUserMessageDropsBlank()
    {
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true));
        Assert(!controller.SubmitUserMessage("box", ""), "SubmitUserMessage should reject empty text");
        Assert(!controller.SubmitUserMessage("box", "   "), "SubmitUserMessage should reject whitespace text");
        Assert(!controller.SubmitUserMessage("no-such-box", "hello"), "SubmitUserMessage should return false for unknown client");
    }

    static void TestDashboardControllerSetOfflineMacRoutesPromptToEngine()
    {
        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true), platform);
        controller.RequestSetOfflineMac("offline-box");
        Assert(platform.LastPrompt != null, "RequestSetOfflineMac should route prompt through platform services");
        Assert(platform.LastPrompt!.Value.Title == "Set MAC", "prompt should be titled Set MAC");
        Assert(platform.LastPrompt.Value.Label.Contains("offline-box"), "prompt label should mention machine name");
    }

    static RemoteClient FakeRemoteClient()
    {
        var client = (RemoteClient)RuntimeHelpers.GetUninitializedObject(typeof(RemoteClient));
        client.LastSeen = DateTime.UtcNow;
        client.ClientVersion = Proto.AppVersion;
        client.SendMode = "full";
        return client;
    }

    static RemoteClient AddReportedClient(ServerEngine engine, string machineName, string osVersion, string clientVersion)
    {
        var client = FakeRemoteClient();
        client.MachineName = machineName;
        client.ClientVersion = clientVersion;
        client.LastReport = new MachineReport
        {
            MachineName = machineName,
            OsVersion = osVersion,
            CpuName = "Test CPU",
            RamTotalGB = 8,
            RamUsedGB = 2
        };
        engine.Clients[machineName] = client;
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

    sealed class FakeServerPlatformServices : IServerPlatformServices
    {
        public string? ClipboardText { get; private set; }
        public (string Message, string Title, DashboardConfirmKind Kind)? LastConfirm { get; private set; }
        public bool ConfirmReturn { get; set; } = true;
        public (string Title, string Label)? LastPrompt { get; private set; }
        public string? PromptReturn { get; set; }
        public (string Title, string Filter)? LastPickFile { get; private set; }
        public string? PickFileReturn { get; set; }
        public string? OpenExternalTarget { get; private set; }
        public bool ShowApprovedCalled { get; private set; }
        public bool ShowAlertsCalled { get; private set; }
        public string? ShowProcessMachine { get; private set; }
        public string? UpdateProcessMachine { get; private set; }
        public string? ShowSysInfoMachine { get; private set; }
        public string? ShowServicesMachine { get; private set; }
        public string? ShowEventsMachine { get; private set; }
        public (string Machine, CpuDetailReport Detail)? ShowCpuDetailCall { get; private set; }
        public (string Machine, ScreenshotData Shot)? ShowScreenshotCall { get; private set; }
        public string? ShowHealthMachine { get; private set; }
        public (string Machine, string Shell)? ShowTerminalCall { get; private set; }
        public (string Machine, string? Path)? ShowFileBrowserCall { get; private set; }
        public string? ShowRdpMachine { get; private set; }
        public (string Machine, Action<string> OnSubmit)? PromptSendUserMessageCall { get; private set; }

        public void SetClipboardText(string text) => ClipboardText = text;
        public bool Confirm(string message, string title, DashboardConfirmKind kind) { LastConfirm = (message, title, kind); return ConfirmReturn; }
        public string? Prompt(string title, string label) { LastPrompt = (title, label); return PromptReturn; }
        public string? PickFile(string title, string filter) { LastPickFile = (title, filter); return PickFileReturn; }
        public void OpenExternal(string target) => OpenExternalTarget = target;
        public void ShowApprovedClients() => ShowApprovedCalled = true;
        public void ShowAlerts() => ShowAlertsCalled = true;
        public void ShowProcessDialog(string machineName) => ShowProcessMachine = machineName;
        public void UpdateProcessDialog(RemoteClient cl) => UpdateProcessMachine = cl.MachineName;
        public void ShowSysInfoDialog(RemoteClient cl) => ShowSysInfoMachine = cl.MachineName;
        public void ShowServicesDialog(RemoteClient cl) => ShowServicesMachine = cl.MachineName;
        public void ShowEventsDialog(RemoteClient cl) => ShowEventsMachine = cl.MachineName;
        public void ShowCpuDetailDialog(string machineName, CpuDetailReport detail) => ShowCpuDetailCall = (machineName, detail);
        public void ShowScreenshotDialog(string machineName, ScreenshotData shot) => ShowScreenshotCall = (machineName, shot);
        public void ShowHealthDialog(string machineName) => ShowHealthMachine = machineName;
        public void ShowTerminal(string machineName, string shell) => ShowTerminalCall = (machineName, shell);
        public void ShowFileBrowser(string machineName, string? initialPath) => ShowFileBrowserCall = (machineName, initialPath);
        public void ShowRdp(string machineName) => ShowRdpMachine = machineName;
        public void PromptSendUserMessage(string machineName, Action<string> onSubmit) => PromptSendUserMessageCall = (machineName, onSubmit);
    }
}
