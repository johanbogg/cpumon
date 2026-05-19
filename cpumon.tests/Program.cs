using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
            TestDashboardControllerSendUserMessageDropsBlankPrompt();
            TestOperatorStoreCreateAndVerify();
            TestOperatorStorePersistsAcrossInstances();
            TestOperatorStoreRejectsDoubleCreate();
            TestArgon2HelperRoundTripAndRejectsTamper();
            TestBootstrapTokenSingleUseAndShape();
            TestBootstrapTokenExpiryClearsAndRejects();
            TestSessionStoreIssueAndValidateRoundTrip();
            TestSessionStoreSlidingExpiryRejectsStale();
            TestSessionStoreTouchRefreshesLastUsed();
            TestSessionStoreInvalidateRemoves();
            TestSessionStorePruneRemovesExpiredEntries();
            TestWebHostStartsAndStopsCleanly();
            TestWebHostHealthzReturnsJson();
            TestWebHostSetsSecurityHeaders();
            TestRateLimiterBlocksAfterMaxFailures();
            TestRateLimiterResetClears();
            TestRateLimiterWindowSlides();
            TestAuthLoginRejectsBlankBody();
            TestAuthLoginRejectsWrongPassword();
            TestAuthLoginSuccessIssuesCookies();
            TestAuthLogoutWithCsrfClearsCookies();
            TestAuthLogoutWithoutCsrfReturns403();
            TestAuthWhoamiReturnsUsername();
            TestAuthBootstrapWhenOperatorExistsReturns409();
            TestAuthBootstrapSucceedsAndIssuesCookies();
            TestAuthRateLimitBlocksAfterFiveFailedLogins();
            TestDashboardStateRequiresAuth();
            TestDashboardStateReturnsJsonWithToken();
            TestDashboardSelectReplacesSelection();
            TestDashboardFilterOsSetsValue();
            TestDashboardFilterRequiresCsrf();
            TestDashboardWebStateIsSessionLocal();
            TestDashboardTokenRegenerateChangesToken();
            TestPerClientActionsRequireAuth();
            TestPerClientActionsRequireCsrf();
            TestPerClientRestartReturns404ForUnknownMachine();
            TestPerClientShutdownReturns404ForUnknownMachine();
            TestPerClientForgetReturns404ForUnknownMachine();
            TestPerClientExpandTogglesClientFlag();
            TestPerClientPawTogglesStoreFlag();
            TestPerClientMessageValidatesBody();
            TestSnapshotEndpointsRequireAuth();
            TestSnapshotReturns204AndTriggersFetchWhenEmpty();
            TestSnapshotReturnsCachedAndDoesNotTriggerWhenFresh();
            TestSnapshotTriggersFetchWhenStale();
            TestSnapshotForceBypassesTtl();
            TestSnapshotHealthDerivesFromReportWithoutTriggering();
            TestSnapshotFetchTriggerIsThrottled();
            TestApprovedListReturnsProjectedEntries();
            TestApprovedPatchUpdatesAliasAndPaw();
            TestApprovedDeleteRemovesFromStore();
            TestOfflineWakeReturns404ForUnknownMachine();
            TestOfflineMacValidatesFormat();
            TestAlertsGetPutRoundTripUpdatesService();
            TestAlertsPutPreservesExistingPassword();
            TestApprovedPatchResolvesCanonicalCasing();
            TestSlice9EndpointsRequireAuth();
            TestSlice9EndpointsRequireCsrf();
            TestLogRequiresAuth();
            TestLogSinceAndLimitFilterEntries();
            TestWebBootstrapIssuesAndShowsWhenOperatorMissing();
            TestWebBootstrapSkippedWhenOperatorExists();
            TestWebStartupComposesAllRoutesAndSurfacesBootstrapUrl();
            TestWebPlatformServicesShowBootstrapUrlPrintsToStdout();
            TestSetupPageBranches();
            TestWebStaticRoutesServeLoginAndDashboard();
            TestPendingApproveRejectRoutesRequireCsrfAndReturn404();
            TestWebSocketStateSentOnConnect();
            TestWebSocketStateUpdatedOnControllerAction();
            TestWebSocketLogStreamsNewEntries();
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

        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
        Assert(controller.CycleSortMode() == "os", "sort mode should cycle to os");
        var names = controller.GetState().Clients.Select(c => c.MachineName).ToList();

        Assert(names.SequenceEqual(new[] { "m-win", "z-win", "a-lnx", "b-lnx" }), "OS sort should place Windows clients before Linux and sort names within each group");
    }

    static void TestDashboardStateCapabilityFlagsForLinuxAndWindows()
    {
        var engine = new ServerEngine(noBroadcast: true);
        AddReportedClient(engine, "win-box", "Microsoft Windows 11", Proto.AppVersion);
        AddReportedClient(engine, "linux-box", "Linux Debian", "0.0.1-linux");

        var cards = new ServerDashboardController(engine, new FakeServerPlatformServices()).GetState().Clients.ToDictionary(c => c.MachineName, StringComparer.OrdinalIgnoreCase);
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

        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
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

        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
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

        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
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

        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
        controller.ToggleSelection("stale");
        controller.PurgeStaleClients();

        Assert(!engine.Clients.ContainsKey("stale"), "PurgeStaleClients should remove clients older than the stale threshold");
        Assert(engine.Clients.ContainsKey("fresh"), "PurgeStaleClients should keep fresh clients");
        Assert(!controller.SelectedMachineNames.Contains("stale"), "PurgeStaleClients should remove stale machines from dashboard selection");
    }

    static void TestDashboardControllerPendingDelegatesReturnFalseForMissing()
    {
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true), new FakeServerPlatformServices());
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
        Assert(platform.PromptUserMessageMachine == "lnx", "SendUserMessage should route through platform services");
    }

    static void TestDashboardControllerToggleExpandedFlipsClient()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var client = FakeRemoteClient();
        client.MachineName = "card-box";
        client.Expanded = false;
        engine.Clients[client.MachineName] = client;
        var controller = new ServerDashboardController(engine, new FakeServerPlatformServices());
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
        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(new ServerEngine(noBroadcast: true), platform);
        controller.OpenTerminal("shell-box", "powershell");
        Assert(platform.ShowTerminalCall != null, "OpenTerminal should route through platform services");
        Assert(platform.ShowTerminalCall!.Value.Machine == "shell-box", "terminal call should carry the machine name");
        Assert(platform.ShowTerminalCall.Value.Shell == "powershell", "terminal call should carry the requested shell");
    }

    static void TestDashboardControllerSendUserMessageDropsBlankPrompt()
    {
        var engine = new ServerEngine(noBroadcast: true);
        var platform = new FakeServerPlatformServices { PromptUserMessageReturn = "  " };
        var controller = new ServerDashboardController(engine, platform);
        controller.SendUserMessage("box");
        Assert(!engine.Log.Recent(10).Any(e => e.M.StartsWith("Msg→")), "whitespace prompt should not produce a Msg log entry");

        platform.PromptUserMessageReturn = null;
        controller.SendUserMessage("box");
        Assert(!engine.Log.Recent(10).Any(e => e.M.StartsWith("Msg→")), "cancelled prompt should not produce a Msg log entry");
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

    static void TestOperatorStoreCreateAndVerify()
    {
        using var td = new TempDir();
        var path = Path.Combine(td.Path, "operator.json");
        var store = new OperatorStore(path);
        Assert(!store.Exists, "fresh operator store should not report Exists");
        store.Create("admin", "supersecretpassword12");
        Assert(store.Exists, "store should report Exists after Create");
        Assert(store.Username == "admin", "stored username should match");
        Assert(store.Verify("admin", "supersecretpassword12"), "correct password should verify");
        Assert(!store.Verify("admin", "wrongpasswordwrong"), "wrong password should not verify");
        Assert(!store.Verify("wronguser", "supersecretpassword12"), "wrong username should not verify");
    }

    static void TestOperatorStorePersistsAcrossInstances()
    {
        using var td = new TempDir();
        var path = Path.Combine(td.Path, "operator.json");
        new OperatorStore(path).Create("admin", "persistencetesting1");
        var fresh = new OperatorStore(path);
        Assert(fresh.Exists, "operator should persist across store instances");
        Assert(fresh.Username == "admin", "reloaded username should match");
        Assert(fresh.Verify("admin", "persistencetesting1"), "reloaded store should verify correct password");
    }

    static void TestOperatorStoreRejectsDoubleCreate()
    {
        using var td = new TempDir();
        var store = new OperatorStore(Path.Combine(td.Path, "operator.json"));
        store.Create("admin", "firstpasswordfirst");
        AssertThrows<InvalidOperationException>(() => store.Create("admin", "secondpassword12"),
            "creating an operator twice in the same instance should throw");
    }

    static void TestArgon2HelperRoundTripAndRejectsTamper()
    {
        // Use a lightweight config to keep the smoke test fast — production hashing uses the defaults.
        var hash = Argon2Helper.Hash("correct horse battery staple", memoryKiB: 1024, iterations: 1, parallelism: 1);
        Assert(Argon2Helper.Verify("correct horse battery staple", hash), "correct password should verify");
        Assert(!Argon2Helper.Verify("wrong horse battery staple", hash), "wrong password should not verify");
        Assert(!Argon2Helper.Verify("correct horse battery staple", hash[..^4] + "AAAA"), "tampered hash should not verify");
        Assert(!Argon2Helper.Verify("correct horse battery staple", "not-a-valid-hash"), "malformed hash should not verify");
        Assert(!Argon2Helper.Verify("correct horse battery staple", ""), "empty hash should not verify");
    }

    static void TestBootstrapTokenSingleUseAndShape()
    {
        using var issuer = new BootstrapTokenIssuer();
        Assert(!issuer.IsActive, "fresh issuer should be inactive");
        Assert(issuer.ExpiresAt == null, "fresh issuer should have no expiry");
        var (token, expiresAt) = issuer.Issue();
        Assert(token.Length >= 20, "token should be at least 20 chars");
        Assert(token.All(c => (c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7')), "token should be base32");
        Assert(issuer.IsActive, "issuer should be active after Issue");
        Assert(expiresAt > DateTime.UtcNow, "expiry should be in the future");
        Assert(issuer.Consume(token), "valid token should consume");
        Assert(!issuer.IsActive, "issuer should be inactive after Consume");
        Assert(!issuer.Consume(token), "consumed token should not consume again");
    }

    static void TestBootstrapTokenExpiryClearsAndRejects()
    {
        using var issuer = new BootstrapTokenIssuer { Validity = TimeSpan.FromMilliseconds(100) };
        var (token, _) = issuer.Issue();
        Thread.Sleep(250);
        Assert(!issuer.IsActive, "issuer should be inactive after expiry");
        Assert(!issuer.Consume(token), "expired token should not consume");
    }

    static void TestSessionStoreIssueAndValidateRoundTrip()
    {
        using var store = new SessionStore(startPruner: false);
        var s = store.Issue("admin", "127.0.0.1", "ua/test");
        Assert(s.Id.Length >= 32, "session id should be long");
        Assert(s.CsrfToken.Length >= 24, "csrf token should be long");
        Assert(s.Id != s.CsrfToken, "session id and csrf must differ");
        Assert(s.Username == "admin", "session should carry username");
        var validated = store.Validate(s.Id);
        Assert(validated != null && validated.Id == s.Id, "issued session should validate");
        Assert(store.Validate(null) == null, "null id should not validate");
        Assert(store.Validate("not-a-real-session") == null, "unknown id should not validate");
    }

    static void TestSessionStoreSlidingExpiryRejectsStale()
    {
        using var store = new SessionStore(startPruner: false) { SlidingExpiry = TimeSpan.FromMilliseconds(50) };
        var s = store.Issue("admin", "::1", "");
        Assert(store.Validate(s.Id) != null, "fresh session should validate");
        Thread.Sleep(120);
        Assert(store.Validate(s.Id) == null, "expired session should not validate");
        Assert(store.Count == 0, "expired session should be removed on access");
    }

    static void TestSessionStoreTouchRefreshesLastUsed()
    {
        using var store = new SessionStore(startPruner: false) { SlidingExpiry = TimeSpan.FromMilliseconds(200) };
        var s = store.Issue("admin", "::1", "");
        var initial = s.LastUsedAt;
        Thread.Sleep(80);
        var validated = store.Validate(s.Id);
        Assert(validated != null, "session should still be valid mid-window");
        Assert(validated!.LastUsedAt > initial, "validate should refresh LastUsedAt");
        Thread.Sleep(150);
        Assert(store.Validate(s.Id) != null, "sliding expiry should keep refreshed session alive past original window");
    }

    static void TestSessionStoreInvalidateRemoves()
    {
        using var store = new SessionStore(startPruner: false);
        var s = store.Issue("admin", "::1", "");
        Assert(store.Invalidate(s.Id), "invalidate should report removal");
        Assert(store.Validate(s.Id) == null, "invalidated session should not validate");
        Assert(!store.Invalidate(s.Id), "second invalidate should report nothing removed");
    }

    static void TestSessionStorePruneRemovesExpiredEntries()
    {
        using var store = new SessionStore(startPruner: false) { SlidingExpiry = TimeSpan.FromMilliseconds(150) };
        var alive = store.Issue("alive", "::1", "");
        var dead1 = store.Issue("dead1", "::1", "");
        var dead2 = store.Issue("dead2", "::1", "");
        // Touch the live one mid-window so its LastUsedAt advances.
        Thread.Sleep(60);
        Assert(store.Validate(alive.Id) != null, "mid-window validate should succeed");
        // Wait long enough for dead1/2 to expire but not the refreshed alive.
        Thread.Sleep(120);
        int evented = 0;
        store.Pruned += n => evented += n;
        int swept = store.Prune();
        Assert(swept == 2, "prune should remove two stale entries");
        Assert(evented == 2, "Pruned event should report two removals");
        Assert(store.Count == 1, "live session should remain after prune");
        Assert(store.Validate(dead1.Id) == null, "pruned session id should no longer validate");
    }

    static void TestWebHostStartsAndStopsCleanly()
    {
        var host = new WebHost();
        try
        {
            host.StartAsync(new WebHostOptions { Port = 0, UseTls = false, ServerVersion = "test" })
                .GetAwaiter().GetResult();
            Assert(host.IsRunning, "host should be running after StartAsync");
            Assert(host.Port > 0, "host should expose the bound port after StartAsync");
        }
        finally
        {
            host.DisposeAsync().GetAwaiter().GetResult();
        }
        Assert(!host.IsRunning, "host should be stopped after DisposeAsync");
    }

    static void TestWebHostHealthzReturnsJson()
    {
        var host = new WebHost();
        try
        {
            host.StartAsync(new WebHostOptions { Port = 0, UseTls = false, ServerVersion = "1.2.3" })
                .GetAwaiter().GetResult();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
            using var resp = client.GetAsync("/api/healthz").GetAwaiter().GetResult();
            Assert((int)resp.StatusCode == 200, "/api/healthz should return 200");
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert(body.Contains("\"ok\":true"), "healthz body should contain ok=true");
            Assert(body.Contains("\"version\":\"1.2.3\""), "healthz body should echo server version");
            Assert(body.Contains("\"uptimeSec\""), "healthz body should contain uptimeSec");
        }
        finally
        {
            host.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    static void TestWebHostSetsSecurityHeaders()
    {
        var host = new WebHost();
        try
        {
            host.StartAsync(new WebHostOptions { Port = 0, UseTls = false, ServerVersion = "hdrtest" })
                .GetAwaiter().GetResult();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
            using var resp = client.GetAsync("/api/healthz").GetAwaiter().GetResult();
            Assert(GetHeader(resp, "X-Content-Type-Options") == "nosniff", "X-Content-Type-Options should be nosniff");
            Assert(GetHeader(resp, "X-Frame-Options") == "DENY", "X-Frame-Options should be DENY");
            Assert(GetHeader(resp, "Referrer-Policy") == "no-referrer", "Referrer-Policy should be no-referrer");
            var csp = GetHeader(resp, "Content-Security-Policy");
            Assert(csp.Contains("default-src 'self'"), "CSP should restrict default-src to self");
            Assert(csp.Contains("script-src 'self' 'unsafe-inline'"), "script-src must allow inline so /setup's bootstrap form works; tighten with nonces in Phase 3");
            Assert(GetHeader(resp, "Server") == "cpumon/hdrtest", "Server header should embed ServerVersion");
            Assert(GetHeader(resp, "Strict-Transport-Security") == "", "HSTS must NOT be set in plain-HTTP mode");
        }
        finally
        {
            host.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    static string GetHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v1)) return string.Join(", ", v1);
        if (resp.Content.Headers.TryGetValues(name, out var v2)) return string.Join(", ", v2);
        return "";
    }

    // ── rate limiter ────────────────────────────────────────────────

    static void TestRateLimiterBlocksAfterMaxFailures()
    {
        var rl = new RateLimiter { MaxFailures = 3 };
        Assert(!rl.IsBlocked("10.0.0.1"), "fresh limiter should not block");
        rl.RecordFailure("10.0.0.1");
        rl.RecordFailure("10.0.0.1");
        Assert(!rl.IsBlocked("10.0.0.1"), "below threshold should not block");
        rl.RecordFailure("10.0.0.1");
        Assert(rl.IsBlocked("10.0.0.1"), "reaching MaxFailures should block");
        Assert(!rl.IsBlocked("10.0.0.2"), "block should be per-IP");
    }

    static void TestRateLimiterResetClears()
    {
        var rl = new RateLimiter { MaxFailures = 2 };
        rl.RecordFailure("ip"); rl.RecordFailure("ip");
        Assert(rl.IsBlocked("ip"), "should block after threshold");
        rl.Reset("ip");
        Assert(!rl.IsBlocked("ip"), "Reset should clear the block");
        Assert(rl.FailureCount("ip") == 0, "count should be zero after Reset");
    }

    static void TestRateLimiterWindowSlides()
    {
        var rl = new RateLimiter { MaxFailures = 2, Window = TimeSpan.FromMilliseconds(120) };
        rl.RecordFailure("ip"); rl.RecordFailure("ip");
        Assert(rl.IsBlocked("ip"), "should block immediately");
        Thread.Sleep(180);
        Assert(!rl.IsBlocked("ip"), "old failures should age out of window");
        Assert(rl.FailureCount("ip") == 0, "count should drop to zero after window passes");
    }

    // ── auth API ────────────────────────────────────────────────────

    static void TestAuthLoginRejectsBlankBody()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        using var resp = h.Post("/api/auth/login", body: null);
        Assert((int)resp.StatusCode == 400, "blank body should return 400");
        Assert(h.Body(resp).Contains("\"error\":\"validation_failed\""), "error code should be validation_failed");
    }

    static void TestAuthLoginRejectsWrongPassword()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        using var resp = h.Post("/api/auth/login", new { username = "admin", password = "wrongpasswordwrong" });
        Assert((int)resp.StatusCode == 401, "wrong password should return 401");
        Assert(h.Body(resp).Contains("\"error\":\"invalid_credentials\""), "error code should be invalid_credentials");
        Assert(h.RateLimit.FailureCount("127.0.0.1") == 1, "failed login should bump rate-limit counter");
    }

    static void TestAuthLoginSuccessIssuesCookies()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        using var resp = h.Post("/api/auth/login", new { username = "admin", password = "correctpassword12" });
        Assert((int)resp.StatusCode == 204, "successful login should return 204");
        Assert(h.CookieValue("cpumon_sess").Length > 0, "cpumon_sess cookie should be set");
        Assert(h.CookieValue("cpumon_csrf").Length > 0, "cpumon_csrf cookie should be set");
        Assert(h.CookieValue("cpumon_sess") != h.CookieValue("cpumon_csrf"), "session id and csrf must differ");
        Assert(h.Sessions.Count == 1, "session store should have one entry");
    }

    static void TestAuthLogoutWithCsrfClearsCookies()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        h.Post("/api/auth/login", new { username = "admin", password = "correctpassword12" }).Dispose();
        var csrf = h.CookieValue("cpumon_csrf");
        using var resp = h.Post("/api/auth/logout", body: null, csrfHeader: csrf);
        Assert((int)resp.StatusCode == 204, "logout with valid csrf should return 204");
        Assert(h.Sessions.Count == 0, "session should be removed after logout");
        Assert(h.CookieValue("cpumon_sess") == "", "cpumon_sess cookie should be cleared by server");
    }

    static void TestAuthLogoutWithoutCsrfReturns403()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        h.Post("/api/auth/login", new { username = "admin", password = "correctpassword12" }).Dispose();
        using var resp = h.Post("/api/auth/logout", body: null, csrfHeader: null);
        Assert((int)resp.StatusCode == 403, "logout without csrf header should return 403");
        Assert(h.Body(resp).Contains("\"error\":\"csrf_failed\""), "error code should be csrf_failed");
        Assert(h.Sessions.Count == 1, "session should remain after failed csrf check");
    }

    static void TestAuthWhoamiReturnsUsername()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "correctpassword12");
        h.Post("/api/auth/login", new { username = "admin", password = "correctpassword12" }).Dispose();
        using var resp = h.Get("/api/auth/whoami");
        Assert((int)resp.StatusCode == 200, "whoami after login should return 200");
        var body = h.Body(resp);
        Assert(body.Contains("\"username\":\"admin\""), "whoami should echo the username");
        Assert(body.Contains("\"sessionCreatedAt\""), "whoami should include sessionCreatedAt");
    }

    static void TestAuthBootstrapWhenOperatorExistsReturns409()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "preexistingpwd12");
        var (token, _) = h.Bootstrap.Issue();
        using var resp = h.Post("/api/auth/bootstrap", new { username = "two", password = "anotherpassword12", bootstrapToken = token });
        Assert((int)resp.StatusCode == 409, "bootstrap with existing operator should return 409");
        Assert(h.Body(resp).Contains("\"error\":\"bootstrap_disabled\""), "error code should be bootstrap_disabled");
    }

    static void TestAuthBootstrapSucceedsAndIssuesCookies()
    {
        using var h = new WebApiTestHost();
        Assert(!h.Operators.Exists, "operator should not exist initially");
        var (token, _) = h.Bootstrap.Issue();
        using var resp = h.Post("/api/auth/bootstrap", new { username = "admin", password = "freshpassword12", bootstrapToken = token });
        Assert((int)resp.StatusCode == 204, "successful bootstrap should return 204");
        Assert(h.Operators.Exists, "operator should exist after bootstrap");
        Assert(h.Operators.Username == "admin", "operator username should be persisted");
        Assert(h.CookieValue("cpumon_sess").Length > 0, "session cookie should be set after bootstrap");
        Assert(!h.Bootstrap.IsActive, "bootstrap token should be consumed");
    }

    static void TestAuthRateLimitBlocksAfterFiveFailedLogins()
    {
        using var h = new WebApiTestHost();
        h.Operators.Create("admin", "rightpassword12345");
        for (int i = 0; i < 5; i++)
        {
            using var r = h.Post("/api/auth/login", new { username = "admin", password = $"wrong{i:D2}wrongwrong" });
            Assert((int)r.StatusCode == 401, $"attempt {i + 1} should be 401");
        }
        using var resp = h.Post("/api/auth/login", new { username = "admin", password = "rightpassword12345" });
        Assert((int)resp.StatusCode == 429, "6th attempt should be 429 even with correct password");
        Assert(resp.Headers.TryGetValues("Retry-After", out _), "429 response should include Retry-After header");
        Assert(h.Body(resp).Contains("\"error\":\"rate_limited\""), "error code should be rate_limited");
    }

    // ── dashboard API ───────────────────────────────────────────────

    static void TestDashboardStateRequiresAuth()
    {
        using var h = new WebApiTestHost();
        using var resp = h.Get("/api/state");
        Assert((int)resp.StatusCode == 401, "GET /api/state without auth should return 401");
        Assert(h.Body(resp).Contains("\"error\":\"auth_required\""), "error code should be auth_required");
    }

    static void TestDashboardStateReturnsJsonWithToken()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Get("/api/state");
        Assert((int)resp.StatusCode == 200, "GET /api/state after auth should return 200");
        var body = h.Body(resp);
        Assert(body.Contains($"\"token\":\"{h.Engine.Token}\""), "state body should contain current engine token");
        Assert(body.Contains("\"clients\":"), "state body should contain clients array");
        Assert(body.Contains("\"pendingApprovals\":"), "state body should contain pendingApprovals array");
        Assert(body.Contains("\"offlineClients\":"), "state body should contain offlineClients array");
        Assert(body.Contains("\"osFilter\":\"all\""), "default osFilter should be all");
    }

    static void TestDashboardSelectReplacesSelection()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var r1 = h.Post("/api/state/select", new { machineNames = new[] { "alpha", "beta" } }, csrfHeader: h.Csrf);
        Assert((int)r1.StatusCode == 204, "select should return 204");
        using var s1 = h.Get("/api/state");
        var b1 = h.Body(s1);
        Assert(b1.Contains("\"selectedMachineNames\":[\"alpha\",\"beta\"]"), "selection should reflect posted machines");
        using var r2 = h.Post("/api/state/select", new { machineNames = new[] { "gamma" } }, csrfHeader: h.Csrf);
        Assert((int)r2.StatusCode == 204, "second select should return 204");
        using var s2 = h.Get("/api/state");
        var b2 = h.Body(s2);
        Assert(b2.Contains("\"selectedMachineNames\":[\"gamma\"]"), "select should replace, not merge");
    }

    static void TestDashboardFilterOsSetsValue()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var r = h.Post("/api/state/filter/os", new { value = "linux" }, csrfHeader: h.Csrf);
        Assert((int)r.StatusCode == 200, "filter set should return 200");
        Assert(h.Body(r).Contains("\"value\":\"linux\""), "response should echo new filter value");
        using var state = h.Get("/api/state");
        Assert(h.Body(state).Contains("\"osFilter\":\"linux\""), "session osFilter should be updated");
        using var bad = h.Post("/api/state/filter/os", new { value = "bogus" }, csrfHeader: h.Csrf);
        Assert((int)bad.StatusCode == 400, "invalid filter value should return 400");
    }

    static void TestDashboardFilterRequiresCsrf()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Post("/api/state/filter/os", new { value = "windows" }, csrfHeader: null);
        Assert((int)resp.StatusCode == 403, "POST without csrf should return 403");
        Assert(h.Body(resp).Contains("\"error\":\"csrf_failed\""), "error code should be csrf_failed");
        using var state = h.Get("/api/state");
        Assert(h.Body(state).Contains("\"osFilter\":\"all\""), "filter should not change on failed csrf check");
    }

    static void TestDashboardWebStateIsSessionLocal()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var client = AddReportedClient(h.Engine, "win-box", "Microsoft Windows 11", Proto.AppVersion);
        using var second = h.NewSession();

        using var expand = h.Post("/api/clients/win-box/expand", body: null, csrfHeader: h.Csrf);
        Assert((int)expand.StatusCode == 200, "expand should return 200");
        Assert(h.Body(expand).Contains("\"expanded\":true"), "expand response should report the session-local expanded state");
        Assert(!client.Expanded, "web expand must not mutate the shared WinForms/client expanded flag");

        using var firstState = h.Get("/api/state");
        using var secondState = second.Get("/api/state");
        Assert(h.Body(firstState).Contains("\"machineName\":\"win-box\"") && h.Body(firstState).Contains("\"isExpanded\":true"),
            "expanded web session should see its own expanded card");
        Assert(h.Body(secondState).Contains("\"machineName\":\"win-box\"") && h.Body(secondState).Contains("\"isExpanded\":false"),
            "second browser session should not inherit another browser's expansion");

        using var filter = h.Post("/api/state/filter/os", new { value = "linux" }, csrfHeader: h.Csrf);
        Assert((int)filter.StatusCode == 200, "filter set should return 200");
        using var firstFiltered = h.Get("/api/state");
        using var secondFiltered = second.Get("/api/state");
        Assert(h.Body(firstFiltered).Contains("\"osFilter\":\"linux\""), "first session should keep its own filter");
        Assert(h.Body(secondFiltered).Contains("\"osFilter\":\"all\""), "second session should keep default filter");
    }

    static void TestDashboardTokenRegenerateChangesToken()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var before = h.Engine.Token;
        using var resp = h.Post("/api/token/regenerate", body: null, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 200, "token regenerate should return 200");
        Assert(h.Engine.Token != before, "engine token should change after regenerate");
        Assert(h.Body(resp).Contains($"\"token\":\"{h.Engine.Token}\""), "response should echo new token");
    }

    static readonly (string Path, bool HasBody)[] PerClientActionPaths =
    {
        ("/api/clients/box/restart",    false),
        ("/api/clients/box/shutdown",   false),
        ("/api/clients/box/forget",     false),
        ("/api/clients/box/paw",        false),
        ("/api/clients/box/message",    true),
        ("/api/clients/box/screenshot", false),
        ("/api/clients/box/expand",     false),
    };

    static void TestPerClientActionsRequireAuth()
    {
        using var h = new WebApiTestHost();
        foreach (var (path, hasBody) in PerClientActionPaths)
        {
            using var resp = h.Post(path, body: hasBody ? new { text = "hi" } : null);
            Assert((int)resp.StatusCode == 401, $"POST {path} without auth should return 401");
            Assert(h.Body(resp).Contains("\"error\":\"auth_required\""), $"POST {path} should report auth_required");
        }
    }

    static void TestPerClientActionsRequireCsrf()
    {
        using var h = new WebApiTestHost();
        h.Login();
        foreach (var (path, hasBody) in PerClientActionPaths)
        {
            using var resp = h.Post(path, body: hasBody ? new { text = "hi" } : null, csrfHeader: null);
            Assert((int)resp.StatusCode == 403, $"POST {path} without csrf should return 403");
            Assert(h.Body(resp).Contains("\"error\":\"csrf_failed\""), $"POST {path} should report csrf_failed");
        }
    }

    static void TestPerClientRestartReturns404ForUnknownMachine()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Post("/api/clients/ghost/restart", body: null, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 404, "restart on unknown machine should return 404");
        Assert(h.Body(resp).Contains("\"error\":\"not_found\""), "error code should be not_found");
    }

    static void TestPerClientShutdownReturns404ForUnknownMachine()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Post("/api/clients/ghost/shutdown", body: null, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 404, "shutdown on unknown machine should return 404");
        Assert(h.Body(resp).Contains("\"error\":\"not_found\""), "error code should be not_found");
    }

    static void TestPerClientForgetReturns404ForUnknownMachine()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Post("/api/clients/ghost/forget", body: null, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 404, "forget on unknown machine should return 404");
        Assert(h.Body(resp).Contains("\"error\":\"not_found\""), "error code should be not_found");
    }

    static void TestPerClientExpandTogglesClientFlag()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var client = AddReportedClient(h.Engine, "win-box", "Microsoft Windows 11", Proto.AppVersion);
        Assert(!client.Expanded, "expanded should start false");
        using var r1 = h.Post("/api/clients/win-box/expand", body: null, csrfHeader: h.Csrf);
        Assert((int)r1.StatusCode == 200, "expand should return 200");
        Assert(!client.Expanded, "web expand should leave the shared client expanded flag unchanged");
        Assert(h.Body(r1).Contains("\"expanded\":true"), "response should echo expanded=true");
        using var state1 = h.Get("/api/state");
        Assert(h.Body(state1).Contains("\"isExpanded\":true"), "session state should show the card as expanded");
        using var r2 = h.Post("/api/clients/win-box/expand", body: null, csrfHeader: h.Csrf);
        Assert((int)r2.StatusCode == 200, "second expand should return 200");
        Assert(!client.Expanded, "web collapse should leave the shared client expanded flag unchanged");
        Assert(h.Body(r2).Contains("\"expanded\":false"), "response should echo expanded=false");
    }

    static void TestPerClientPawTogglesStoreFlag()
    {
        using var h = new WebApiTestHost();
        h.Login();
        AddReportedClient(h.Engine, "paw-box", "Microsoft Windows 11", Proto.AppVersion);
        h.Engine.Store.Approve("paw-box", "k", "127.0.0.1");
        Assert(!h.Engine.Store.IsPaw("paw-box"), "paw should start false");
        using var r1 = h.Post("/api/clients/paw-box/paw", body: null, csrfHeader: h.Csrf);
        Assert((int)r1.StatusCode == 200, "paw should return 200");
        Assert(h.Engine.Store.IsPaw("paw-box"), "paw flag should flip on");
        Assert(h.Body(r1).Contains("\"isPaw\":true"), "response should echo isPaw=true");
        using var r2 = h.Post("/api/clients/paw-box/paw", body: null, csrfHeader: h.Csrf);
        Assert((int)r2.StatusCode == 200, "second paw should return 200");
        Assert(!h.Engine.Store.IsPaw("paw-box"), "paw flag should flip off");
        Assert(h.Body(r2).Contains("\"isPaw\":false"), "response should echo isPaw=false");
    }

    static void TestPerClientMessageValidatesBody()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var blank = h.Post("/api/clients/box/message", new { text = "" }, csrfHeader: h.Csrf);
        Assert((int)blank.StatusCode == 400, "blank message body should return 400");
        Assert(h.Body(blank).Contains("\"error\":\"validation_failed\""), "blank message error should be validation_failed");
        using var oversized = h.Post("/api/clients/box/message", new { text = new string('x', 501) }, csrfHeader: h.Csrf);
        Assert((int)oversized.StatusCode == 400, "oversized message body should return 400");
        Assert(h.Body(oversized).Contains("\"error\":\"validation_failed\""), "oversized message error should be validation_failed");
        using var ghost = h.Post("/api/clients/ghost/message", new { text = "hi" }, csrfHeader: h.Csrf);
        Assert((int)ghost.StatusCode == 404, "message to unknown machine should return 404");
        Assert(h.Body(ghost).Contains("\"error\":\"not_found\""), "unknown machine error should be not_found");
    }

    static void TestApprovedListReturnsProjectedEntries()
    {
        using var h = new WebApiTestHost();
        h.Login();
        h.Engine.Store.Approve("alpha", "k1", "10.0.0.1");
        h.Engine.Store.Approve("beta",  "k2", "10.0.0.2");
        h.Engine.Store.SetAlias("alpha", "Alpha box");
        h.Engine.Store.SetPaw("beta", true);
        using var resp = h.Get("/api/approved");
        Assert((int)resp.StatusCode == 200, "GET /api/approved should return 200");
        var body = h.Body(resp);
        Assert(body.Contains("\"name\":\"alpha\""), "approved list should include alpha");
        Assert(body.Contains("\"alias\":\"Alpha box\""), "approved list should include alpha's alias");
        Assert(body.Contains("\"name\":\"beta\""), "approved list should include beta");
        Assert(body.Contains("\"isPaw\":true"), "approved list should include beta's paw flag");
        Assert(!body.Contains("\"key\""), "approved list must not leak key");
        Assert(!body.Contains("\"salt\""), "approved list must not leak salt");
    }

    static void TestApprovedPatchUpdatesAliasAndPaw()
    {
        using var h = new WebApiTestHost();
        h.Login();
        h.Engine.Store.Approve("box", "k", "10.0.0.5");
        using var ok = h.Patch("/api/approved/box", new { alias = "renamed", isPaw = true }, csrfHeader: h.Csrf);
        Assert((int)ok.StatusCode == 204, "PATCH should return 204");
        Assert(h.Engine.Store.GetAlias("box") == "renamed", "alias should be persisted");
        Assert(h.Engine.Store.IsPaw("box"), "paw flag should flip on");
        using var ghost = h.Patch("/api/approved/ghost", new { alias = "x" }, csrfHeader: h.Csrf);
        Assert((int)ghost.StatusCode == 404, "PATCH on unknown machine should return 404");
        using var noCsrf = h.Patch("/api/approved/box", new { alias = "blocked" }, csrfHeader: null);
        Assert((int)noCsrf.StatusCode == 403, "PATCH without csrf should return 403");
        Assert(h.Engine.Store.GetAlias("box") == "renamed", "alias should not change on csrf failure");
    }

    static void TestApprovedDeleteRemovesFromStore()
    {
        using var h = new WebApiTestHost();
        h.Login();
        h.Engine.Store.Approve("doomed", "k", "10.0.0.6");
        Assert(h.Engine.Store.All().Any(c => c.Name == "doomed"), "fixture: doomed should be approved");
        using var resp = h.Delete("/api/approved/doomed", csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 204, "DELETE should return 204");
        Assert(!h.Engine.Store.All().Any(c => c.Name == "doomed"), "doomed should be gone from store");
    }

    static void TestOfflineWakeReturns404ForUnknownMachine()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var resp = h.Post("/api/offline/ghost/wake", body: null, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 404, "wake on unknown machine should return 404");
        Assert(h.Body(resp).Contains("\"error\":\"not_found\""), "error code should be not_found");
    }

    static void TestOfflineMacValidatesFormat()
    {
        using var h = new WebApiTestHost();
        h.Login();
        h.Engine.Store.Approve("box", "k", "10.0.0.7");
        using var bad = h.Post("/api/offline/box/mac", new { mac = "not-a-mac" }, csrfHeader: h.Csrf);
        Assert((int)bad.StatusCode == 400, "invalid mac should return 400");
        Assert(h.Body(bad).Contains("\"error\":\"validation_failed\""), "invalid mac error should be validation_failed");
        Assert(h.Engine.Store.GetMac("box") == "", "invalid mac must not be persisted");
        using var ok = h.Post("/api/offline/box/mac", new { mac = "AA:BB:CC:DD:EE:FF" }, csrfHeader: h.Csrf);
        Assert((int)ok.StatusCode == 204, "valid mac should return 204");
        Assert(h.Engine.Store.GetMac("box") == "AA:BB:CC:DD:EE:FF", "mac should be persisted");
    }

    static void TestSetupPageBranches()
    {
        using var h = new WebApiTestHost();

        using var missing = h.Get("/setup");
        Assert((int)missing.StatusCode == 200, "GET /setup with no token should still return 200");
        var missingBody = h.Body(missing);
        Assert(missingBody.Contains("Missing setup token"), "no-token branch should explain to the operator");
        Assert(!missingBody.Contains("<form"), "no-token branch must not render a form");

        using var withToken = h.Get("/setup?t=ABCDEF1234567890");
        Assert((int)withToken.StatusCode == 200, "GET /setup?t=… should return 200");
        var formBody = h.Body(withToken);
        Assert(formBody.Contains("<form"), "token branch should render the form");
        Assert(formBody.Contains("value=\"ABCDEF1234567890\""), "form should embed the supplied bootstrap token");
        Assert(formBody.Contains("method=\"post\""), "form must POST so a no-JS fallback can't leak credentials into a URL");
        Assert(formBody.Contains("action=\"/api/auth/bootstrap\""), "form action must target the bootstrap endpoint, not the current URL");
        Assert(formBody.Contains("autocomplete=\"off\""), "form should opt out of browser autofill");

        h.Operators.Create("admin", "correctpassword12");
        using var done = h.Get("/setup?t=ABCDEF1234567890");
        Assert((int)done.StatusCode == 200, "GET /setup after operator created should return 200");
        var doneBody = h.Body(done);
        Assert(doneBody.Contains("Setup complete"), "post-bootstrap branch should say setup is done");
        Assert(!doneBody.Contains("<form"), "post-bootstrap branch must not render a form");
    }

    static void TestWebStartupComposesAllRoutesAndSurfacesBootstrapUrl()
    {
        using var td = new TempDir();
        using var engine = new ServerEngine(noBroadcast: true,
            store: new ApprovedClientStore(Path.Combine(td.Path, "approved.json")),
            alerts: new AlertService(new CLog(), Path.Combine(td.Path, "alerts.json")));
        var platform = new FakeServerPlatformServices();
        var controller = new ServerDashboardController(engine, platform);
        var opts = new WebStartupOptions
        {
            Port         = 0,
            UseTls       = false,
            BindAddress  = "127.0.0.1",
            OperatorPath = Path.Combine(td.Path, "operator.json"),
        };
        using var web = WebStartup.StartAsync(engine, controller, platform, opts).GetAwaiter().GetResult();
        Assert(web.Host.IsRunning, "WebStartup should leave the host running");
        Assert(web.Host.Port > 0, "WebHost should bind to an OS-picked port");
        Assert(platform.ShowBootstrapUrlCalls.Count == 1, "first-run with no operator should surface bootstrap URL");
        Assert(platform.ShowBootstrapUrlCalls[0].Url.Contains($":{web.Host.Port}/setup?t="), "bootstrap URL should embed the bound port");
        Assert(web.Bootstrap.IsActive, "bootstrap issuer should hold an active token after startup");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{web.Host.Port}") };
        using var healthz = http.GetAsync("/api/healthz").GetAwaiter().GetResult();
        Assert((int)healthz.StatusCode == 200, "/api/healthz should be reachable through the composed host");
        using var state = http.GetAsync("/api/state").GetAwaiter().GetResult();
        Assert((int)state.StatusCode == 401, "/api/state should require auth and respond through the composed dashboard route");
        using var approved = http.GetAsync("/api/approved").GetAwaiter().GetResult();
        Assert((int)approved.StatusCode == 401, "/api/approved should be wired and require auth");
        using var alerts = http.GetAsync("/api/alerts").GetAwaiter().GetResult();
        Assert((int)alerts.StatusCode == 401, "/api/alerts should be wired and require auth");
        using var log = http.GetAsync("/api/log").GetAwaiter().GetResult();
        Assert((int)log.StatusCode == 401, "/api/log should be wired and require auth");
    }

    static void TestWebPlatformServicesShowBootstrapUrlPrintsToStdout()
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var outSink = new System.IO.StringWriter();
        var errSink = new System.IO.StringWriter();
        try
        {
            Console.SetOut(outSink);
            Console.SetError(errSink);
            new WebPlatformServices().ShowBootstrapUrl("https://localhost:47202/setup?t=ABC", DateTime.UtcNow.AddMinutes(10));
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
        Assert(outSink.ToString().Contains("https://localhost:47202/setup?t=ABC"), "stub should print URL to stdout");
        Assert(errSink.ToString().Contains("https://localhost:47202/setup?t=ABC"), "stub should also print URL to stderr");
    }

    static void TestWebStaticRoutesServeLoginAndDashboard()
    {
        using var h = new WebApiTestHost();
        using var unauthRoot = h.Get("/");
        Assert((int)unauthRoot.StatusCode == 200, "unauthenticated GET / should follow redirect to login");
        Assert(h.Body(unauthRoot).Contains("operator console"), "unauthenticated root should land on the login page");

        using var login = h.Get("/login");
        Assert((int)login.StatusCode == 200, "GET /login should serve the login page");
        Assert(h.Body(login).Contains("operator console"), "login page should contain operator console text");

        using var css = h.Get("/web/app.css");
        Assert((int)css.StatusCode == 200, "app.css should be served");
        Assert(h.Body(css).Contains("--bg-deep"), "app.css should contain the dashboard palette");
        Assert(h.Body(css).Contains("/web/fonts/ibm-plex-mono-400.ttf"), "app.css should self-host the web UI fonts");
        Assert(!h.Body(css).Contains("fonts.googleapis.com") && !h.Body(css).Contains("fonts.gstatic.com"), "app.css should not reference remote font hosts");

        using var font = h.Get("/web/fonts/ibm-plex-mono-400.ttf");
        Assert((int)font.StatusCode == 200, "self-hosted font should be served");
        Assert(font.Content.Headers.ContentType?.MediaType == "font/ttf", "self-hosted font should use font/ttf content type");
        Assert(font.Content.Headers.ContentLength.GetValueOrDefault() > 1000, "self-hosted font should contain binary font bytes");

        h.Login();
        using var root = h.Get("/");
        Assert((int)root.StatusCode == 200, "authenticated GET / should return the dashboard shell");
        var body = h.Body(root);
        Assert(body.Contains("clientTemplate"), "dashboard shell should include the client card template");
        Assert(body.Contains("/web/app.js"), "dashboard shell should load the app script");
        Assert(!body.Contains("fonts.googleapis.com") && !body.Contains("fonts.gstatic.com"), "dashboard shell should not load remote fonts");

        using var js = h.Get("/web/app.js");
        Assert((int)js.StatusCode == 200, "app.js should be served");
        var jsBody = h.Body(js);
        Assert(jsBody.Contains("function clientCard("), "app.js should include client card rendering");
        Assert(jsBody.Contains("function ramText("), "app.js should include the RAM formatter used by expanded cards");
        Assert(jsBody.Contains("function openScreenshotDialog("), "app.js should include the web screenshot dialog");
    }

    static void TestPendingApproveRejectRoutesRequireCsrfAndReturn404()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var noCsrf = h.Post("/api/pending/ghost/approve", body: null, csrfHeader: null);
        Assert((int)noCsrf.StatusCode == 403, "pending approve should require csrf");
        using var missingApprove = h.Post("/api/pending/ghost/approve", body: null, csrfHeader: h.Csrf);
        Assert((int)missingApprove.StatusCode == 404, "pending approve on unknown machine should return 404");
        using var missingReject = h.Post("/api/pending/ghost/reject", body: null, csrfHeader: h.Csrf);
        Assert((int)missingReject.StatusCode == 404, "pending reject on unknown machine should return 404");
    }

    static void TestWebSocketStateSentOnConnect()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var ws = h.ConnectWebSocket("/ws/state");
        string msg = ReceiveWsText(ws);
        using var doc = JsonDocument.Parse(msg);
        Assert(doc.RootElement.GetProperty("type").GetString() == "state", "state websocket should send a state frame");
        Assert(doc.RootElement.GetProperty("state").GetProperty("token").GetString() == h.Controller.GetState().Token, "state frame should contain dashboard state");
    }

    static void TestWebSocketStateUpdatedOnControllerAction()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var ws = h.ConnectWebSocket("/ws/state");
        ReceiveWsText(ws);
        using var resp = h.Post("/api/state/filter/os", new { value = "linux" }, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 200, "fixture filter mutation should return 200");
        string msg = ReceiveWsText(ws, timeoutMs: 2000);
        using var doc = JsonDocument.Parse(msg);
        Assert(doc.RootElement.GetProperty("type").GetString() == "state", "updated state websocket frame should have type=state");
        Assert(doc.RootElement.GetProperty("state").GetProperty("osFilter").GetString() == "linux", "state websocket should reflect controller mutations");
    }

    static void TestWebSocketLogStreamsNewEntries()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var ws = h.ConnectWebSocket("/ws/log");
        h.Engine.Log.Add("ws-log-entry", System.Drawing.Color.FromArgb(0x12, 0x34, 0x56));
        string msg = ReceiveWsText(ws, timeoutMs: 2000);
        using var doc = JsonDocument.Parse(msg);
        Assert(doc.RootElement.GetProperty("type").GetString() == "log", "log websocket should send log frames");
        var entry = doc.RootElement.GetProperty("entry");
        Assert(entry.GetProperty("message").GetString() == "ws-log-entry", "log websocket should stream newly added log entry");
        Assert(entry.GetProperty("color").GetString() == "#123456", "log websocket should include hex colour");
    }

    static void TestWebBootstrapIssuesAndShowsWhenOperatorMissing()
    {
        using var td = new TempDir();
        var operators = new OperatorStore(Path.Combine(td.Path, "operator.json"));
        using var issuer = new BootstrapTokenIssuer();
        var platform = new FakeServerPlatformServices();
        bool buildCalled = false;
        var log = new CLog();
        bool issued = WebBootstrap.MaybeIssueAndShow(operators, issuer, platform, token =>
        {
            buildCalled = true;
            Assert(!string.IsNullOrEmpty(token), "issued token should be non-empty");
            return $"https://localhost:47202/setup?t={token}";
        }, log);
        Assert(issued, "MaybeIssueAndShow should return true when no operator account exists");
        Assert(buildCalled, "url builder should run once an operator is missing");
        Assert(platform.ShowBootstrapUrlCalls.Count == 1, "platform.ShowBootstrapUrl should be called exactly once");
        var (url, expiresAt) = platform.ShowBootstrapUrlCalls[0];
        Assert(url.StartsWith("https://localhost:47202/setup?t="), "URL should embed the built setup link");
        Assert((expiresAt - DateTime.UtcNow).TotalMinutes > 5, "expiry should be at least 5 min in the future");
        Assert(issuer.IsActive, "issuer should hold an active token after MaybeIssueAndShow");
    }

    static void TestWebBootstrapSkippedWhenOperatorExists()
    {
        using var td = new TempDir();
        var operators = new OperatorStore(Path.Combine(td.Path, "operator.json"));
        operators.Create("admin", "correctpassword12");
        using var issuer = new BootstrapTokenIssuer();
        var platform = new FakeServerPlatformServices();
        bool buildCalled = false;
        bool issued = WebBootstrap.MaybeIssueAndShow(operators, issuer, platform, _ => { buildCalled = true; return ""; });
        Assert(!issued, "MaybeIssueAndShow should return false once an operator account exists");
        Assert(!buildCalled, "url builder must not run when bootstrap is skipped");
        Assert(platform.ShowBootstrapUrlCalls.Count == 0, "platform.ShowBootstrapUrl must not be called when operator exists");
        Assert(!issuer.IsActive, "issuer should NOT mint a token when bootstrap is skipped");
    }

    static void TestLogRequiresAuth()
    {
        using var h = new WebApiTestHost();
        using var resp = h.Get("/api/log");
        Assert((int)resp.StatusCode == 401, "GET /api/log without auth should return 401");
        Assert(h.Body(resp).Contains("\"error\":\"auth_required\""), "error code should be auth_required");
    }

    static void TestLogSinceAndLimitFilterEntries()
    {
        using var h = new WebApiTestHost();
        h.Login();
        // ServerEngine startup writes a couple lines already; add deterministic ones.
        h.Engine.Log.Add("entry-A", System.Drawing.Color.Gray);
        h.Engine.Log.Add("entry-B", System.Drawing.Color.FromArgb(0xFF, 0x88, 0x00));
        h.Engine.Log.Add("entry-C", System.Drawing.Color.Red);

        using var all = h.Get("/api/log?limit=500");
        Assert((int)all.StatusCode == 200, "GET /api/log should return 200");
        var allBody = h.Body(all);
        Assert(allBody.Contains("\"message\":\"entry-A\""), "log should include seeded entry-A");
        Assert(allBody.Contains("\"message\":\"entry-C\""), "log should include seeded entry-C");
        Assert(allBody.Contains("\"color\":\"#FF8800\""), "log entries should expose hex colour");

        using var limited = h.Get("/api/log?limit=1");
        Assert((int)limited.StatusCode == 200, "limited GET should return 200");
        var limitedBody = h.Body(limited);
        Assert(limitedBody.Contains("\"message\":\"entry-C\""), "limit=1 should return the most-recent entry");
        Assert(!limitedBody.Contains("\"message\":\"entry-A\""), "limit=1 should not include older entries");

        var future = DateTime.UtcNow.AddMinutes(5).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        using var none = h.Get($"/api/log?since={Uri.EscapeDataString(future)}");
        Assert((int)none.StatusCode == 200, "future since should return 200");
        Assert(h.Body(none) == "[]", "since in the future should yield an empty array");

        using var huge = h.Get("/api/log?limit=9999");
        Assert((int)huge.StatusCode == 200, "oversize limit should clamp, not error");
    }

    static void TestAlertsGetPutRoundTripUpdatesService()
    {
        using var h = new WebApiTestHost();
        h.Login();
        using var get = h.Get("/api/alerts");
        Assert((int)get.StatusCode == 200, "GET /api/alerts should return 200");
        var body = h.Body(get);
        Assert(body.Contains("\"cooldown\":30"), "default cooldown should be 30 minutes");
        Assert(body.Contains("\"passwordSet\":false"), "GET should expose passwordSet boolean (not the encrypted blob)");
        Assert(!body.Contains("\"pass\""), "GET must not expose the encrypted password field");
        var update = new
        {
            host     = "smtp.example.com",
            port     = 587,
            from     = "ops@example.com",
            to       = "ops@example.com",
            ram      = 90,
            cooldown = 15,
        };
        using var put = h.Put("/api/alerts", update, csrfHeader: h.Csrf);
        Assert((int)put.StatusCode == 204, "PUT /api/alerts should return 204");
        Assert(h.Alerts.Config.SmtpHost == "smtp.example.com", "alert host should be updated in-memory");
        Assert(h.Alerts.Config.AlertRamPct == 90, "alert ram pct should be updated");
        Assert(h.Alerts.Config.CooldownMinutes == 15, "cooldown should be updated");
        using var bad = h.Put("/api/alerts", new { port = -1 }, csrfHeader: h.Csrf);
        Assert((int)bad.StatusCode == 400, "invalid port should return 400");
    }

    static void TestAlertsPutPreservesExistingPassword()
    {
        using var h = new WebApiTestHost();
        h.Login();
        // Bypass DPAPI by stashing a sentinel directly; web code only cares that the field is non-empty.
        h.Alerts.SaveConfig(new AlertConfig
        {
            SmtpHost = "smtp.example.com", FromAddress = "ops@x.com", ToAddress = "ops@x.com",
            Username = "ops", EncryptedPassword = "STORED_BLOB", CooldownMinutes = 30,
        });
        using var keep = h.Put("/api/alerts", new
        {
            host = "smtp.example.com", port = 587, from = "ops@x.com", to = "ops@x.com",
            username = "ops", ram = 80, cooldown = 30,
        }, csrfHeader: h.Csrf);
        Assert((int)keep.StatusCode == 204, "PUT without password fields should return 204");
        Assert(h.Alerts.Config.EncryptedPassword == "STORED_BLOB", "PUT without password fields must preserve existing encrypted password");
        Assert(h.Alerts.Config.Username == "ops", "username should still be ops");
        using var clear = h.Put("/api/alerts", new
        {
            host = "smtp.example.com", port = 587, from = "ops@x.com", to = "ops@x.com",
            username = "ops", clearPassword = true, cooldown = 30,
        }, csrfHeader: h.Csrf);
        Assert((int)clear.StatusCode == 204, "PUT with clearPassword should return 204");
        Assert(string.IsNullOrEmpty(h.Alerts.Config.EncryptedPassword), "clearPassword=true must wipe the encrypted blob");
    }

    static void TestApprovedPatchResolvesCanonicalCasing()
    {
        using var h = new WebApiTestHost();
        h.Login();
        h.Engine.Store.Approve("mybox", "k", "10.0.0.9");
        using var resp = h.Patch("/api/approved/MYBOX", new { alias = "renamed" }, csrfHeader: h.Csrf);
        Assert((int)resp.StatusCode == 204, "case-mismatched PATCH should resolve canonical name and return 204");
        Assert(h.Engine.Store.GetAlias("mybox") == "renamed", "PATCH must mutate the stored entry under its canonical casing");
    }

    static readonly (string Method, string Path, bool HasBody)[] Slice9Paths =
    {
        ("POST",   "/api/offline/box/wake",   false),
        ("POST",   "/api/offline/box/mac",    true),
        ("POST",   "/api/offline/box/forget", false),
        ("PATCH",  "/api/approved/box",       true),
        ("DELETE", "/api/approved/box",       false),
        ("PUT",    "/api/alerts",             true),
        ("POST",   "/api/alerts/test",        false),
    };

    static void TestSlice9EndpointsRequireAuth()
    {
        using var h = new WebApiTestHost();
        foreach (var (method, path, hasBody) in Slice9Paths)
        {
            using var resp = h.Send(method, path, hasBody ? new { } : null, csrfHeader: null);
            Assert((int)resp.StatusCode == 401, $"{method} {path} without auth should return 401");
        }
        using var listResp = h.Get("/api/approved");
        Assert((int)listResp.StatusCode == 401, "GET /api/approved without auth should return 401");
        using var alertsGet = h.Get("/api/alerts");
        Assert((int)alertsGet.StatusCode == 401, "GET /api/alerts without auth should return 401");
    }

    static void TestSlice9EndpointsRequireCsrf()
    {
        using var h = new WebApiTestHost();
        h.Login();
        foreach (var (method, path, hasBody) in Slice9Paths)
        {
            using var resp = h.Send(method, path, hasBody ? new { } : null, csrfHeader: null);
            Assert((int)resp.StatusCode == 403, $"{method} {path} without csrf should return 403");
            Assert(h.Body(resp).Contains("\"error\":\"csrf_failed\""), $"{method} {path} should report csrf_failed");
        }
    }

    static readonly string[] SnapshotPaths =
    {
        "/api/clients/box/processes",
        "/api/clients/box/sysinfo",
        "/api/clients/box/services",
        "/api/clients/box/events",
        "/api/clients/box/cpu-detail",
        "/api/clients/box/screenshot",
        "/api/clients/box/health",
    };

    static void TestSnapshotEndpointsRequireAuth()
    {
        using var h = new WebApiTestHost();
        foreach (var path in SnapshotPaths)
        {
            using var resp = h.Get(path);
            Assert((int)resp.StatusCode == 401, $"GET {path} without auth should return 401");
            Assert(h.Body(resp).Contains("\"error\":\"auth_required\""), $"GET {path} should report auth_required");
        }
    }

    static void TestSnapshotReturns204AndTriggersFetchWhenEmpty()
    {
        using var h = new WebApiTestHost();
        h.Login();
        AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Processes) == null, "no fetch should be recorded yet");
        using var resp = h.Get("/api/clients/box/processes");
        Assert((int)resp.StatusCode == 204, "empty snapshot should return 204");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Processes) != null, "empty snapshot GET should trigger a fetch");
    }

    static void TestSnapshotReturnsCachedAndDoesNotTriggerWhenFresh()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var client = AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        client.LastProcessList = new List<ProcessInfo> { new() { Pid = 42, Name = "explorer.exe" } };
        h.Snapshots.MarkReceivedAt("box", SnapshotKind.Processes, DateTime.UtcNow.AddSeconds(-1));
        using var resp = h.Get("/api/clients/box/processes");
        Assert((int)resp.StatusCode == 200, "fresh snapshot should return 200");
        var body = h.Body(resp);
        Assert(body.Contains("\"pid\":42"), "response should contain the cached process entry");
        Assert(body.Contains("\"snapshot\":"), "response should wrap the payload under snapshot");
        Assert(body.Contains("\"receivedAt\":"), "response should include receivedAt");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Processes) == null, "fresh snapshot should not trigger fetch");
    }

    static void TestSnapshotTriggersFetchWhenStale()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var client = AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        client.LastServiceList = new List<ServiceInfo> { new() { Name = "spooler", Status = "Running" } };
        h.Snapshots.MarkReceivedAt("box", SnapshotKind.Services, DateTime.UtcNow.AddSeconds(-30));
        using var resp = h.Get("/api/clients/box/services");
        Assert((int)resp.StatusCode == 200, "stale snapshot should still return cached body with 200");
        Assert(h.Body(resp).Contains("\"n\":\"spooler\""), "response should contain cached service entry");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Services) != null, "stale snapshot GET should trigger a fetch");
    }

    static void TestSnapshotForceBypassesTtl()
    {
        using var h = new WebApiTestHost();
        h.Login();
        var client = AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        client.LastSysInfo = new SystemInfoReport { Hostname = "box" };
        h.Snapshots.MarkReceivedAt("box", SnapshotKind.SysInfo, DateTime.UtcNow);
        Assert(!h.Snapshots.IsStale("box", SnapshotKind.SysInfo), "snapshot should be fresh before force");
        using var resp = h.Get("/api/clients/box/sysinfo?force=true");
        Assert((int)resp.StatusCode == 200, "force=true should still return cached body");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.SysInfo) != null, "force=true should trigger fetch despite fresh cache");
    }

    static void TestSnapshotFetchTriggerIsThrottled()
    {
        using var h = new WebApiTestHost();
        h.Login();
        AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        using var r1 = h.Get("/api/clients/box/processes");
        Assert((int)r1.StatusCode == 204, "first GET on empty snapshot should return 204");
        var t1 = h.Snapshots.TriggeredAt("box", SnapshotKind.Processes);
        Assert(t1 != null, "first GET should record a trigger");
        using var r2 = h.Get("/api/clients/box/processes");
        Assert((int)r2.StatusCode == 204, "second GET on empty snapshot should still return 204");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Processes) == t1, "rapid re-GET within throttle window must not re-trigger fetch");
        using var r3 = h.Get("/api/clients/box/processes?force=true");
        Assert(h.Snapshots.TriggeredAt("box", SnapshotKind.Processes) == t1, "force=true within throttle window also must not re-trigger");
    }

    static void TestSnapshotHealthDerivesFromReportWithoutTriggering()
    {
        using var h = new WebApiTestHost();
        h.Login();
        AddReportedClient(h.Engine, "box", "Microsoft Windows 11", Proto.AppVersion);
        using var resp = h.Get("/api/clients/box/health");
        Assert((int)resp.StatusCode == 200, "health should return 200 with a connected client");
        var body = h.Body(resp);
        Assert(body.Contains("\"machineName\":\"box\""), "health response should echo machine name");
        Assert(body.Contains("\"hasReport\":true"), "health should reflect a present report");
        Assert(body.Contains("\"ramTotalGB\":8"), "health should expose ram from latest report");
        foreach (var kind in new[] { SnapshotKind.Processes, SnapshotKind.SysInfo, SnapshotKind.Services, SnapshotKind.Events, SnapshotKind.CpuDetail, SnapshotKind.Screenshot })
            Assert(h.Snapshots.TriggeredAt("box", kind) == null, $"health GET must not trigger a fetch for {kind}");
    }

    sealed class WebApiTestHost : IDisposable
    {
        public WebHost                   Host       { get; }
        public HttpClient                Client     { get; }
        public OperatorStore             Operators  { get; }
        public SessionStore              Sessions   { get; }
        public BootstrapTokenIssuer      Bootstrap  { get; }
        public RateLimiter               RateLimit  { get; }
        public ServerEngine              Engine     { get; }
        public ServerDashboardController Controller { get; }
        public SnapshotCache             Snapshots  { get; }
        public AlertService              Alerts     { get; }
        readonly TempDir _td;
        readonly CookieContainer _cookies;

        public WebApiTestHost()
        {
            _td        = new TempDir();
            Operators  = new OperatorStore(Path.Combine(_td.Path, "operator.json"));
            Sessions   = new SessionStore(startPruner: false);
            Bootstrap  = new BootstrapTokenIssuer();
            RateLimit  = new RateLimiter();
            Alerts     = new AlertService(new CLog(), Path.Combine(_td.Path, "alerts.json"));
            var store  = new ApprovedClientStore(Path.Combine(_td.Path, "approved_clients.json"));
            Engine     = new ServerEngine(noBroadcast: true, store: store, alerts: Alerts);
            Controller = new ServerDashboardController(Engine, new FakeServerPlatformServices());
            Snapshots  = new SnapshotCache(Engine);
            Host       = new WebHost();
            Host.StartAsync(new WebHostOptions
            {
                Port = 0, UseTls = false, ServerVersion = "test",
                ConfigureRoutes = (app, ctx) =>
                {
                    WebStaticApi.Map(app, Sessions);
                    WebAuthApi.Map(app, Operators, Sessions, Bootstrap, RateLimit, ctx);
                    WebDashboardApi.Map(app, Engine, Controller, Sessions, ctx);
                    WebClientActionsApi.Map(app, Engine, Controller, Sessions, ctx);
                    WebSnapshotApi.Map(app, Engine, Snapshots, Sessions, ctx);
                    WebOfflineApi.Map(app, Engine, Sessions, ctx);
                    WebApprovedApi.Map(app, Engine, Sessions, ctx);
                    WebAlertsApi.Map(app, Alerts, Sessions, ctx);
                    WebLogApi.Map(app, Engine, Sessions, ctx);
                    WebSocketApi.Map(app, Engine, Controller, Sessions, ctx);
                }
            }).GetAwaiter().GetResult();
            _cookies = new CookieContainer();
            Client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = _cookies })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Host.Port}")
            };
        }

        public void Login(string username = "admin", string password = "correctpassword12")
        {
            if (!Operators.Exists) Operators.Create(username, password);
            using var r = Post("/api/auth/login", new { username, password });
            if ((int)r.StatusCode != 204) throw new InvalidOperationException("login failed in test setup");
        }

        public WebApiSession NewSession(string username = "admin", string password = "correctpassword12")
        {
            if (!Operators.Exists) Operators.Create(username, password);
            var cookies = new CookieContainer();
            var client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = cookies })
            {
                BaseAddress = Client.BaseAddress
            };
            var session = new WebApiSession(client, cookies);
            using var r = session.Post("/api/auth/login", new { username, password });
            if ((int)r.StatusCode != 204)
            {
                session.Dispose();
                throw new InvalidOperationException("login failed in secondary test setup");
            }
            return session;
        }

        public string Csrf => CookieValue("cpumon_csrf");

        public HttpResponseMessage Post(string path, object? body = null, string? csrfHeader = null)
            => Send(HttpMethod.Post, path, body, csrfHeader);

        public HttpResponseMessage Put(string path, object? body = null, string? csrfHeader = null)
            => Send(HttpMethod.Put, path, body, csrfHeader);

        public HttpResponseMessage Patch(string path, object? body = null, string? csrfHeader = null)
            => Send(new HttpMethod("PATCH"), path, body, csrfHeader);

        public HttpResponseMessage Delete(string path, string? csrfHeader = null)
            => Send(HttpMethod.Delete, path, body: null, csrfHeader);

        public HttpResponseMessage Get(string path) => Client.GetAsync(path).GetAwaiter().GetResult();

        public HttpResponseMessage Send(string method, string path, object? body = null, string? csrfHeader = null)
            => Send(method == "PATCH" ? new HttpMethod("PATCH") : new HttpMethod(method), path, body, csrfHeader);

        public HttpResponseMessage Send(HttpMethod method, string path, object? body, string? csrfHeader)
        {
            var req = new HttpRequestMessage(method, path);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (csrfHeader != null) req.Headers.Add("X-CSRF-Token", csrfHeader);
            return Client.SendAsync(req).GetAwaiter().GetResult();
        }

        public string Body(HttpResponseMessage resp) => resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        public ClientWebSocket ConnectWebSocket(string path)
        {
            var ws = new ClientWebSocket();
            var cookies = _cookies.GetCookieHeader(Client.BaseAddress!);
            if (!string.IsNullOrEmpty(cookies))
                ws.Options.SetRequestHeader("Cookie", cookies);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var uri = new Uri($"ws://127.0.0.1:{Host.Port}{path}");
            ws.ConnectAsync(uri, cts.Token).GetAwaiter().GetResult();
            return ws;
        }

        public string CookieValue(string name)
        {
            foreach (Cookie c in _cookies.GetCookies(Client.BaseAddress!))
                if (c.Name == name) return c.Value;
            return "";
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.DisposeAsync().GetAwaiter().GetResult();
            Snapshots.Dispose();
            Sessions.Dispose();
            Bootstrap.Dispose();
            _td.Dispose();
        }
    }

    sealed class WebApiSession : IDisposable
    {
        readonly CookieContainer _cookies;
        public HttpClient Client { get; }

        public WebApiSession(HttpClient client, CookieContainer cookies)
        {
            Client = client;
            _cookies = cookies;
        }

        public string Csrf => CookieValue("cpumon_csrf");

        public HttpResponseMessage Post(string path, object? body = null, string? csrfHeader = null)
            => Send(HttpMethod.Post, path, body, csrfHeader);

        public HttpResponseMessage Get(string path) => Client.GetAsync(path).GetAwaiter().GetResult();

        public HttpResponseMessage Send(HttpMethod method, string path, object? body, string? csrfHeader)
        {
            var req = new HttpRequestMessage(method, path);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (csrfHeader != null) req.Headers.Add("X-CSRF-Token", csrfHeader);
            return Client.SendAsync(req).GetAwaiter().GetResult();
        }

        public string Body(HttpResponseMessage resp) => resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        string CookieValue(string name)
        {
            foreach (Cookie c in _cookies.GetCookies(Client.BaseAddress!))
                if (c.Name == name) return c.Value;
            return "";
        }

        public void Dispose() => Client.Dispose();
    }

    static RemoteClient FakeRemoteClient()
    {
        var client = (RemoteClient)RuntimeHelpers.GetUninitializedObject(typeof(RemoteClient));
        client.LastSeen = DateTime.UtcNow;
        client.ClientVersion = Proto.AppVersion;
        client.SendMode = "full";
        // Hydrate just enough readonly state so engine RequestX paths can lock,
        // serialize, and Send into a sink. Tests that observe "fetch triggered"
        // rely on Send completing without throwing — a disconnected real client
        // would queue or drop the cmd, never throw an unhandled exception.
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var t = typeof(RemoteClient);
        t.GetField("_wl",        flags)!.SetValue(client, new object());
        t.GetField("PendingCmds", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .SetValue(client, new ConcurrentQueue<ServerCommand>());
        t.GetField("_wr",        flags)!.SetValue(client, new StreamWriter(Stream.Null, new UTF8Encoding(false)) { AutoFlush = true });
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

    static string ReceiveWsText(ClientWebSocket ws, int timeoutMs = 1000)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var result = ws.ReceiveAsync(buffer, cts.Token).GetAwaiter().GetResult();
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("websocket closed before a text message was received");
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("websocket returned a non-text frame");
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
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
        public string? PromptUserMessageMachine { get; private set; }
        public string? PromptUserMessageReturn { get; set; }
        public List<(string Url, DateTime ExpiresAt)> ShowBootstrapUrlCalls { get; } = new();

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
        public string? PromptUserMessage(string machineName) { PromptUserMessageMachine = machineName; return PromptUserMessageReturn; }
        public void ShowBootstrapUrl(string url, DateTime expiresAt) => ShowBootstrapUrlCalls.Add((url, expiresAt));
    }
}
