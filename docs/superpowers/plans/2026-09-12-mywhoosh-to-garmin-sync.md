# MyWhoosh → Garmin Connect Auto-Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a macOS background service in C#/.NET that watches for new MyWhoosh `.fit` activity files and automatically uploads them to Garmin Connect, without ever needing to start an activity on the Garmin watch.

**Architecture:** A `Microsoft.Extensions.Hosting` console app running one `BackgroundService` that watches the MyWhoosh activities folder, hands each stable new file to a `SyncPipeline`, which deduplicates via a local SQLite tracker, authenticates against Garmin's unofficial mobile OAuth1→OAuth2 flow (tokens cached in the macOS Keychain), uploads the file, and fires a native notification.

**Tech Stack:** .NET 10 (LTS), `Microsoft.Extensions.Hosting`, `Microsoft.Data.Sqlite`, xUnit, `Microsoft.Extensions.TimeProvider.Testing`, macOS `security` and `osascript` CLIs (shelled out, no native Keychain P/Invoke needed).

**Spec:** `docs/superpowers/specs/2026-09-12-mywhoosh-to-garmin-design.md`

## Global Constraints

- Target framework: **net10.0**, nullable reference types enabled, implicit usings enabled.
- macOS only for this version (the design doc explicitly puts other OSes out of scope).
- No credential or token ever written to a plaintext file — Garmin tokens live only in the macOS Keychain (via the `security` CLI).
- TDD throughout: write the failing test, watch it fail, implement, watch it pass, commit.
- Every external process/network call goes through a small interface (`IProcessRunner`, injected `HttpClient`) so pure logic stays unit-testable without hitting the real OS Keychain or the real Garmin servers.
- Hand-write test fakes/stubs (no mocking framework) — this project is a learning exercise, and hand-rolled fakes make the interfaces being tested more visible.

---

## Before you start

Run once, interactively, to confirm the toolchain:

```bash
dotnet --version   # must print 10.x
```

If this prints anything below `10.0`, install the .NET 10 SDK before continuing (`brew install --cask dotnet-sdk` or from https://dotnet.microsoft.com/download).

---

### Task 1: Solution scaffolding

**Files:**
- Create: `MyWhoosh2Garmin.sln`
- Create: `src/MyWhoosh2Garmin/MyWhoosh2Garmin.csproj`
- Create: `src/MyWhoosh2Garmin/Program.cs` (placeholder entry point, replaced in Task 11)
- Create: `tests/MyWhoosh2Garmin.Tests/MyWhoosh2Garmin.Tests.csproj`
- Create: `tests/MyWhoosh2Garmin.Tests/SmokeTest.cs`
- Create: `.gitignore`

**Interfaces:**
- Produces: a buildable solution with a passing smoke test — every later task adds files under `src/MyWhoosh2Garmin/` and `tests/MyWhoosh2Garmin.Tests/` and assumes both projects already exist and are wired into the `.sln`.

- [ ] **Step 1: Create the two projects**

```bash
mkdir -p src/MyWhoosh2Garmin tests/MyWhoosh2Garmin.Tests
dotnet new console -n MyWhoosh2Garmin -o src/MyWhoosh2Garmin
dotnet new xunit -n MyWhoosh2Garmin.Tests -o tests/MyWhoosh2Garmin.Tests
dotnet new sln -n MyWhoosh2Garmin
dotnet sln add src/MyWhoosh2Garmin/MyWhoosh2Garmin.csproj
dotnet sln add tests/MyWhoosh2Garmin.Tests/MyWhoosh2Garmin.Tests.csproj
dotnet add tests/MyWhoosh2Garmin.Tests/MyWhoosh2Garmin.Tests.csproj reference src/MyWhoosh2Garmin/MyWhoosh2Garmin.csproj
```

- [ ] **Step 2: Set the target framework and enable nullable in both `.csproj` files**

Edit `src/MyWhoosh2Garmin/MyWhoosh2Garmin.csproj` and `tests/MyWhoosh2Garmin.Tests/MyWhoosh2Garmin.Tests.csproj` so the `<PropertyGroup>` includes:

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

- [ ] **Step 3: Add NuGet packages needed by later tasks**

```bash
dotnet add src/MyWhoosh2Garmin package Microsoft.Extensions.Hosting
dotnet add src/MyWhoosh2Garmin package Microsoft.Data.Sqlite
dotnet add tests/MyWhoosh2Garmin.Tests package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 4: Write a smoke test**

```csharp
// tests/MyWhoosh2Garmin.Tests/SmokeTest.cs
namespace MyWhoosh2Garmin.Tests;

public class SmokeTest
{
    [Fact]
    public void Solution_builds_and_tests_run()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: 1 test passes (plus whatever the `dotnet new xunit` template scaffolded — delete its default `UnitTest1.cs` if present).

- [ ] **Step 6: Create `.gitignore` and commit**

```
bin/
obj/
*.user
.DS_Store
```

```bash
git add MyWhoosh2Garmin.sln src tests .gitignore
git commit -m "Scaffold .NET 10 solution with console app and test project"
```

---

### Task 2: File hashing and stability detection

**Files:**
- Create: `src/MyWhoosh2Garmin/Utilities/FileHasher.cs`
- Create: `src/MyWhoosh2Garmin/Watching/IFileStabilityChecker.cs`
- Create: `src/MyWhoosh2Garmin/Watching/FileStabilityChecker.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Utilities/FileHasherTests.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Watching/FileStabilityCheckerTests.cs`

**Interfaces:**
- Produces: `FileHasher.ComputeSha256(string filePath) : string` (uppercase hex).
- Produces: `interface IFileStabilityChecker { Task<bool> WaitUntilStableAsync(string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken ct); }` and its implementation `FileStabilityChecker(TimeProvider? timeProvider = null, Func<string,long>? getFileSize = null)`. Task 4 consumes both.

**Concept note (pour toi) :** `FileStabilityChecker` reçoit une `TimeProvider` injectable plutôt que d'appeler `DateTimeOffset.UtcNow`/`Task.Delay` directement — ça permet au test de "avancer le temps" instantanément avec un `FakeTimeProvider`, sans vraiment attendre des secondes.

- [ ] **Step 1: Write the failing test for `FileHasher`**

```csharp
// tests/MyWhoosh2Garmin.Tests/Utilities/FileHasherTests.cs
using MyWhoosh2Garmin.Utilities;

namespace MyWhoosh2Garmin.Tests.Utilities;

public class FileHasherTests
{
    [Fact]
    public void ComputeSha256_returns_known_hash_for_known_content()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "hello world");

        try
        {
            string hash = FileHasher.ComputeSha256(tempFile);

            // sha256("hello world") — verified independently with `shasum -a 256`.
            Assert.Equal(
                "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE",
                hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --filter FileHasherTests`
Expected: FAIL — `FileHasher` does not exist yet (compile error).

- [ ] **Step 3: Implement `FileHasher`**

```csharp
// src/MyWhoosh2Garmin/Utilities/FileHasher.cs
using System.Security.Cryptography;

namespace MyWhoosh2Garmin.Utilities;

public static class FileHasher
{
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
```

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test --filter FileHasherTests`
Expected: PASS

- [ ] **Step 5: Write the failing tests for `FileStabilityChecker`**

```csharp
// tests/MyWhoosh2Garmin.Tests/Watching/FileStabilityCheckerTests.cs
using Microsoft.Extensions.Time.Testing;
using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class FileStabilityCheckerTests
{
    [Fact]
    public async Task Returns_true_once_size_stops_changing()
    {
        var timeProvider = new FakeTimeProvider();
        var sizeAtCall = new Queue<long>(new long[] { 100, 250, 250, 250 });
        var checker = new FileStabilityChecker(
            timeProvider,
            _ => sizeAtCall.Count > 1 ? sizeAtCall.Dequeue() : sizeAtCall.Peek());

        var task = checker.WaitUntilStableAsync(
            "irrelevant-path.fit",
            checkInterval: TimeSpan.FromSeconds(1),
            stablePeriod: TimeSpan.FromSeconds(2),
            CancellationToken.None);

        // Advance time in 1s steps so the fake delay completes each poll.
        for (int i = 0; i < 4 && !task.IsCompleted; i++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        bool result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task Returns_false_when_cancelled_before_stable()
    {
        var timeProvider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var checker = new FileStabilityChecker(timeProvider, _ => 100);

        var task = checker.WaitUntilStableAsync(
            "irrelevant-path.fit",
            checkInterval: TimeSpan.FromSeconds(1),
            stablePeriod: TimeSpan.FromSeconds(10),
            cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        cts.Cancel();

        bool result = await task;
        Assert.False(result);
    }
}
```

- [ ] **Step 6: Run and watch both fail**

Run: `dotnet test --filter FileStabilityCheckerTests`
Expected: FAIL — `FileStabilityChecker`/`IFileStabilityChecker` don't exist yet.

- [ ] **Step 7: Implement the interface and the checker**

```csharp
// src/MyWhoosh2Garmin/Watching/IFileStabilityChecker.cs
namespace MyWhoosh2Garmin.Watching;

public interface IFileStabilityChecker
{
    Task<bool> WaitUntilStableAsync(
        string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken cancellationToken);
}
```

```csharp
// src/MyWhoosh2Garmin/Watching/FileStabilityChecker.cs
namespace MyWhoosh2Garmin.Watching;

public class FileStabilityChecker : IFileStabilityChecker
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, long> _getFileSize;

    public FileStabilityChecker(TimeProvider? timeProvider = null, Func<string, long>? getFileSize = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getFileSize = getFileSize ?? (path => new FileInfo(path).Length);
    }

    public async Task<bool> WaitUntilStableAsync(
        string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken cancellationToken)
    {
        long lastSize = _getFileSize(path);
        var stableSince = _timeProvider.GetUtcNow();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            long currentSize = _getFileSize(path);
            if (currentSize != lastSize)
            {
                lastSize = currentSize;
                stableSince = _timeProvider.GetUtcNow();
                continue;
            }

            if (_timeProvider.GetUtcNow() - stableSince >= stablePeriod)
                return true;
        }

        return false;
    }
}
```

- [ ] **Step 8: Run and watch both pass**

Run: `dotnet test --filter FileStabilityCheckerTests`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/MyWhoosh2Garmin/Utilities src/MyWhoosh2Garmin/Watching tests/MyWhoosh2Garmin.Tests/Utilities tests/MyWhoosh2Garmin.Tests/Watching
git commit -m "Add file hashing and stability-detection helpers"
```

---

### Task 3: SQLite dedupe tracker

**Files:**
- Create: `src/MyWhoosh2Garmin/Tracking/IActivityTracker.cs`
- Create: `src/MyWhoosh2Garmin/Tracking/SqliteActivityTracker.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Tracking/SqliteActivityTrackerTests.cs`

**Interfaces:**
- Produces: `interface IActivityTracker { Task<bool> IsProcessedAsync(string fileHash); Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId); }`, implementation `SqliteActivityTracker(string databasePath) : IActivityTracker, IDisposable`. Task 10's `SyncPipeline` consumes `IActivityTracker`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MyWhoosh2Garmin.Tests/Tracking/SqliteActivityTrackerTests.cs
using MyWhoosh2Garmin.Tracking;

namespace MyWhoosh2Garmin.Tests.Tracking;

public class SqliteActivityTrackerTests
{
    [Fact]
    public async Task Unknown_hash_is_not_processed()
    {
        using var tracker = new SqliteActivityTracker("Data Source=:memory:");

        bool isProcessed = await tracker.IsProcessedAsync("some-hash");

        Assert.False(isProcessed);
    }

    [Fact]
    public async Task Marking_processed_makes_it_show_as_processed()
    {
        using var tracker = new SqliteActivityTracker("Data Source=:memory:");

        await tracker.MarkProcessedAsync("hash-1", "MyNewActivity-1.fit", "garmin-activity-42");

        Assert.True(await tracker.IsProcessedAsync("hash-1"));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter SqliteActivityTrackerTests`
Expected: FAIL — types don't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/MyWhoosh2Garmin/Tracking/IActivityTracker.cs
namespace MyWhoosh2Garmin.Tracking;

public interface IActivityTracker
{
    Task<bool> IsProcessedAsync(string fileHash);
    Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId);
}
```

```csharp
// src/MyWhoosh2Garmin/Tracking/SqliteActivityTracker.cs
using Microsoft.Data.Sqlite;

namespace MyWhoosh2Garmin.Tracking;

public class SqliteActivityTracker : IActivityTracker, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteActivityTracker(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using var createTable = _connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS processed_activities (
                file_hash TEXT PRIMARY KEY,
                file_name TEXT NOT NULL,
                garmin_activity_id TEXT,
                processed_at TEXT NOT NULL
            );
            """;
        createTable.ExecuteNonQuery();
    }

    public async Task<bool> IsProcessedAsync(string fileHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM processed_activities WHERE file_hash = $hash";
        command.Parameters.AddWithValue("$hash", fileHash);
        object? result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    public async Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO processed_activities (file_hash, file_name, garmin_activity_id, processed_at)
            VALUES ($hash, $name, $activityId, $processedAt)
            """;
        command.Parameters.AddWithValue("$hash", fileHash);
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$activityId", (object?)garminActivityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$processedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => _connection.Dispose();
}
```

Note: for `Data Source=:memory:` the database only lives as long as the connection stays open — which is exactly what this class does (one connection held for its lifetime), so the two tests above don't interfere with each other or need cleanup.

- [ ] **Step 4: Run and watch it pass**

Run: `dotnet test --filter SqliteActivityTrackerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/MyWhoosh2Garmin/Tracking tests/MyWhoosh2Garmin.Tests/Tracking
git commit -m "Add SQLite-backed activity dedupe tracker"
```

---

### Task 4: MyWhoosh folder locator and file watcher service

**Files:**
- Create: `src/MyWhoosh2Garmin/Watching/MyWhooshFolderLocator.cs`
- Create: `src/MyWhoosh2Garmin/Watching/ActivityFileWatcherService.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Watching/MyWhooshFolderLocatorTests.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Watching/ActivityFileWatcherServiceTests.cs`

**Interfaces:**
- Consumes: `IFileStabilityChecker.WaitUntilStableAsync(...)` from Task 2.
- Produces: `MyWhooshFolderLocator.GetActivitiesFolder() : string`. Produces `ActivityFileWatcherService(string watchFolder, IFileStabilityChecker stabilityChecker, Func<string, CancellationToken, Task> onActivityReady, ILogger<ActivityFileWatcherService> logger, TimeSpan? pollInterval = null) : BackgroundService`. Task 11 wires `onActivityReady` to `SyncPipeline.HandleNewActivityAsync`.

- [ ] **Step 1: Write the failing test for the locator**

```csharp
// tests/MyWhoosh2Garmin.Tests/Watching/MyWhooshFolderLocatorTests.cs
using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class MyWhooshFolderLocatorTests
{
    [Fact]
    public void Returns_path_under_the_user_home_directory()
    {
        string path = MyWhooshFolderLocator.GetActivitiesFolder();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(home, path);
        Assert.EndsWith(Path.Combine("MyWhoosh", "Content", "Data"), path);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter MyWhooshFolderLocatorTests`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement the locator**

```csharp
// src/MyWhoosh2Garmin/Watching/MyWhooshFolderLocator.cs
namespace MyWhoosh2Garmin.Watching;

public static class MyWhooshFolderLocator
{
    public static string GetActivitiesFolder()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Seul macOS est supporté pour l'instant.");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(
            home, "Library", "Containers", "com.whoosh.whooshgame", "Data",
            "Library", "Application Support", "Epic", "MyWhoosh", "Content", "Data");
    }
}
```

- [ ] **Step 4: Run and watch it pass**

Run: `dotnet test --filter MyWhooshFolderLocatorTests`
Expected: PASS

- [ ] **Step 5: Write the failing test for the watcher service**

This test uses a stub stability checker that always says "stable immediately" and a short poll interval, so the test doesn't need to wait for real `FileSystemWatcher` events.

```csharp
// tests/MyWhoosh2Garmin.Tests/Watching/ActivityFileWatcherServiceTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using MyWhoosh2Garmin.Watching;

namespace MyWhoosh2Garmin.Tests.Watching;

public class AlwaysStableChecker : IFileStabilityChecker
{
    public Task<bool> WaitUntilStableAsync(string path, TimeSpan checkInterval, TimeSpan stablePeriod, CancellationToken ct)
        => Task.FromResult(true);
}

public class ActivityFileWatcherServiceTests
{
    [Fact]
    public async Task Picks_up_a_pre_existing_fit_file_via_polling()
    {
        string tempDir = Directory.CreateTempSubdirectory("mw2g-test-").FullName;
        string fitFile = Path.Combine(tempDir, "MyNewActivity-1.fit");
        File.WriteAllBytes(fitFile, [1, 2, 3]);

        var seenFiles = new List<string>();
        var service = new ActivityFileWatcherService(
            tempDir,
            new AlwaysStableChecker(),
            (path, ct) => { seenFiles.Add(path); return Task.CompletedTask; },
            NullLogger<ActivityFileWatcherService>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(tempDir, recursive: true);
        }

        Assert.Contains(fitFile, seenFiles);
    }
}
```

- [ ] **Step 6: Run and watch it fail**

Run: `dotnet test --filter ActivityFileWatcherServiceTests`
Expected: FAIL — type doesn't exist.

- [ ] **Step 7: Implement the watcher service**

```csharp
// src/MyWhoosh2Garmin/Watching/ActivityFileWatcherService.cs
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MyWhoosh2Garmin.Watching;

public class ActivityFileWatcherService : BackgroundService
{
    private readonly string _watchFolder;
    private readonly IFileStabilityChecker _stabilityChecker;
    private readonly Func<string, CancellationToken, Task> _onActivityReady;
    private readonly ILogger<ActivityFileWatcherService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly HashSet<string> _seen = new();

    public ActivityFileWatcherService(
        string watchFolder,
        IFileStabilityChecker stabilityChecker,
        Func<string, CancellationToken, Task> onActivityReady,
        ILogger<ActivityFileWatcherService> logger,
        TimeSpan? pollInterval = null)
    {
        _watchFolder = watchFolder;
        _stabilityChecker = stabilityChecker;
        _onActivityReady = onActivityReady;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_watchFolder))
        {
            _logger.LogError(
                "Dossier MyWhoosh introuvable : {Folder}. MyWhoosh a peut-être changé d'emplacement de stockage.",
                _watchFolder);
            return;
        }

        var channel = Channel.CreateUnbounded<string>();

        using var watcher = new FileSystemWatcher(_watchFolder, "*.fit")
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => channel.Writer.TryWrite(e.FullPath);

        // Fallback: FileSystemWatcher can miss events on macOS, so also poll periodically.
        var pollingTask = PollForFilesAsync(channel.Writer, stoppingToken);

        try
        {
            await foreach (string path in channel.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_seen.Add(path))
                    continue;

                bool stable = await _stabilityChecker.WaitUntilStableAsync(
                    path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), stoppingToken);

                if (!stable)
                    continue;

                await _onActivityReady(path, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await pollingTask;
    }

    private async Task PollForFilesAsync(ChannelWriter<string> writer, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (string file in Directory.EnumerateFiles(_watchFolder, "*.fit"))
                    writer.TryWrite(file);

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
```

- [ ] **Step 8: Run and watch it pass**

Run: `dotnet test --filter ActivityFileWatcherServiceTests`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/MyWhoosh2Garmin/Watching tests/MyWhoosh2Garmin.Tests/Watching
git commit -m "Add MyWhoosh folder locator and background file watcher"
```

---

### Task 5: Garmin OAuth1 request signer

**Context:** Garmin Connect has no public API. The unofficial mobile login flow (documented by the `garth` Python library, which this plan ports to C#) signs two requests with OAuth1 (RFC 5849, HMAC-SHA1) before it ever issues an OAuth2 bearer token. This task implements just the signing math, verified against signatures independently computed in Python — no network calls yet.

**Files:**
- Create: `src/MyWhoosh2Garmin/Garmin/GarminOAuth1Signer.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/GarminOAuth1SignerTests.cs`

**Interfaces:**
- Produces: `static class GarminOAuth1Signer { static string BuildAuthorizationHeader(string httpMethod, string baseUrl, IReadOnlyDictionary<string,string> queryOrBodyParams, string consumerKey, string consumerSecret, string? token, string? tokenSecret, string nonce, long unixTimestamp) : string; static string PercentEncode(string value) : string; }`. Task 6 and Task 7 consume `BuildAuthorizationHeader`.

- [ ] **Step 1: Write the failing tests, using ground-truth vectors**

These expected signatures were computed independently in Python (`hmac`/`hashlib`, the same primitives `requests_oauthlib`/garth use under the hood), not guessed — if your C# implementation produces the same base64 string, the algorithm is correct.

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/GarminOAuth1SignerTests.cs
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminOAuth1SignerTests
{
    [Fact]
    public void Signature_matches_independently_computed_vector_with_token()
    {
        string header = GarminOAuth1Signer.BuildAuthorizationHeader(
            httpMethod: "GET",
            baseUrl: "https://example.com/resource",
            queryOrBodyParams: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            consumerKey: "consumerkey",
            consumerSecret: "consumersecret",
            token: "token",
            tokenSecret: "tokensecret",
            nonce: "abc123nonce",
            unixTimestamp: 1700000000);

        Assert.Contains("oauth_signature=\"1%2Bm8vOz4ay%2BmFMO3SmhjPTA0IoE%3D\"", header);
    }

    [Fact]
    public void Signature_matches_independently_computed_vector_without_token()
    {
        string header = GarminOAuth1Signer.BuildAuthorizationHeader(
            httpMethod: "GET",
            baseUrl: "https://connectapi.garmin.com/oauth-service/oauth/preauthorized",
            queryOrBodyParams: new Dictionary<string, string>
            {
                ["ticket"] = "ST-123456",
                ["login-url"] = "https://mobile.integration.garmin.com/gcm/android",
                ["accepts-mfa-tokens"] = "true",
            },
            consumerKey: "consumerkey",
            consumerSecret: "consumersecret",
            token: null,
            tokenSecret: null,
            nonce: "noncevalue",
            unixTimestamp: 1700000000);

        Assert.Contains("oauth_signature=\"qUX5XcXCiAcJj2hjMuBllqBtVbQ%3D\"", header);
    }
}
```

Note: the expected strings above are the *percent-encoded* form of the raw base64 signatures (`1+m8vOz4ay+mFMO3SmhjPTA0IoE=` and `qUX5XcXCiAcJj2hjMuBllqBtVbQ=`) because the header itself percent-encodes every value, including the signature.

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter GarminOAuth1SignerTests`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement the signer**

```csharp
// src/MyWhoosh2Garmin/Garmin/GarminOAuth1Signer.cs
using System.Security.Cryptography;
using System.Text;

namespace MyWhoosh2Garmin.Garmin;

public static class GarminOAuth1Signer
{
    public static string BuildAuthorizationHeader(
        string httpMethod,
        string baseUrl,
        IReadOnlyDictionary<string, string> queryOrBodyParams,
        string consumerKey,
        string consumerSecret,
        string? token,
        string? tokenSecret,
        string nonce,
        long unixTimestamp)
    {
        var oauthParams = new Dictionary<string, string>
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = unixTimestamp.ToString(),
            ["oauth_version"] = "1.0",
        };
        if (!string.IsNullOrEmpty(token))
            oauthParams["oauth_token"] = token;

        var allParams = new Dictionary<string, string>(oauthParams);
        foreach (var kv in queryOrBodyParams)
            allParams[kv.Key] = kv.Value;

        string paramString = string.Join("&",
            allParams
                .Select(kv => $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}")
                .OrderBy(s => s, StringComparer.Ordinal));

        string baseString = string.Join("&",
            httpMethod.ToUpperInvariant(),
            PercentEncode(baseUrl),
            PercentEncode(paramString));

        string signingKey = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? "")}";

        byte[] hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString));
        oauthParams["oauth_signature"] = Convert.ToBase64String(hash);

        string headerParams = string.Join(", ",
            oauthParams
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{PercentEncode(kv.Key)}=\"{PercentEncode(kv.Value)}\""));

        return $"OAuth {headerParams}";
    }

    public static string PercentEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            bool isUnreserved =
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c is '-' or '.' or '_' or '~';

            if (isUnreserved)
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
```

- [ ] **Step 4: Run and watch it pass**

Run: `dotnet test --filter GarminOAuth1SignerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/MyWhoosh2Garmin/Garmin tests/MyWhoosh2Garmin.Tests/Garmin
git commit -m "Add OAuth1 HMAC-SHA1 request signer for Garmin's unofficial auth flow"
```

---

### Task 6: Garmin SSO login (get a service ticket)

**Context:** First half of authentication: send the user's Garmin email/password to Garmin's mobile SSO endpoint and get back a short-lived "service ticket". Tested against a fake `HttpMessageHandler` so no real Garmin credentials are needed to run the test suite.

**Files:**
- Create: `src/MyWhoosh2Garmin/Garmin/GarminSsoException.cs`
- Create: `src/MyWhoosh2Garmin/Garmin/GarminAuthClient.cs` (login part only — exchange comes in Task 7)
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/FakeHttpMessageHandler.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/GarminAuthClientLoginTests.cs`

**Interfaces:**
- Produces: `class GarminAuthClient(HttpClient httpClient) { Task<string> LoginAndGetTicketAsync(string email, string password, CancellationToken ct); }` — the `HttpClient` is injected so tests substitute a fake handler. Task 7 adds more methods to this same class.
- Produces (test helper, reused in Task 7 and Task 9): `class FakeHttpMessageHandler : HttpMessageHandler` that maps request URL → canned `HttpResponseMessage`.

- [ ] **Step 1: Write the reusable fake HTTP handler**

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/FakeHttpMessageHandler.cs
using System.Net;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}
```

- [ ] **Step 2: Write the failing login test**

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/GarminAuthClientLoginTests.cs
using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminAuthClientLoginTests
{
    [Fact]
    public async Task LoginAndGetTicketAsync_returns_ticket_on_successful_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mobile/api/login")
            {
                string json = """
                    { "responseStatus": { "type": "SUCCESSFUL" }, "serviceTicketId": "ST-abc-123" }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        string ticket = await client.LoginAndGetTicketAsync("me@example.com", "hunter2", CancellationToken.None);

        Assert.Equal("ST-abc-123", ticket);
    }

    [Fact]
    public async Task LoginAndGetTicketAsync_throws_on_failed_response()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "responseStatus": { "type": "FAILED", "message": "invalid credentials" } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        await Assert.ThrowsAsync<GarminSsoException>(() =>
            client.LoginAndGetTicketAsync("me@example.com", "wrong", CancellationToken.None));
    }
}
```

- [ ] **Step 3: Run and watch it fail**

Run: `dotnet test --filter GarminAuthClientLoginTests`
Expected: FAIL — types don't exist.

- [ ] **Step 4: Implement**

```csharp
// src/MyWhoosh2Garmin/Garmin/GarminSsoException.cs
namespace MyWhoosh2Garmin.Garmin;

public class GarminSsoException : Exception
{
    public GarminSsoException(string message) : base(message) { }
}
```

```csharp
// src/MyWhoosh2Garmin/Garmin/GarminAuthClient.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyWhoosh2Garmin.Garmin;

public class GarminAuthClient
{
    private const string ClientId = "GCM_ANDROID_DARK";
    private static readonly string ServiceUrl = "https://mobile.integration.garmin.com/gcm/android";

    private readonly HttpClient _httpClient;

    public GarminAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> LoginAndGetTicketAsync(string email, string password, CancellationToken cancellationToken)
    {
        // Sets SSO cookies used by the following request; failures here are non-fatal.
        try
        {
            using var signInPage = new HttpRequestMessage(
                HttpMethod.Get, $"https://sso.garmin.com/mobile/sso/en/sign-in?clientId={ClientId}");
            await _httpClient.SendAsync(signInPage, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Best-effort; the login POST below still works without it in most cases.
        }

        string loginUrl =
            $"https://sso.garmin.com/mobile/api/login?clientId={ClientId}&locale=en-US&service={Uri.EscapeDataString(ServiceUrl)}";

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = JsonContent.Create(new
            {
                username = email,
                password,
                rememberMe = false,
                captchaToken = "",
            }),
        };

        using var response = await _httpClient.SendAsync(loginRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonDocument body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new GarminSsoException("Réponse SSO Garmin vide.");

        string responseType = body.RootElement
            .GetProperty("responseStatus")
            .GetProperty("type")
            .GetString() ?? "UNKNOWN";

        if (responseType != "SUCCESSFUL")
        {
            string message = body.RootElement.TryGetProperty("responseStatus", out var status)
                              && status.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? responseType
                : responseType;
            throw new GarminSsoException($"Échec de connexion Garmin : {message}");
        }

        return body.RootElement.GetProperty("serviceTicketId").GetString()
            ?? throw new GarminSsoException("Aucun ticket de service dans la réponse SSO Garmin.");
    }
}
```

- [ ] **Step 5: Run and watch it pass**

Run: `dotnet test --filter GarminAuthClientLoginTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/MyWhoosh2Garmin/Garmin tests/MyWhoosh2Garmin.Tests/Garmin
git commit -m "Add Garmin SSO login (service ticket retrieval)"
```

---

### Task 7: OAuth1 → OAuth2 token exchange

**Context:** Second half of authentication. The service ticket from Task 6 is exchanged for an OAuth1 token pair (signed with Garmin's app consumer key/secret, fetched once from a public S3 JSON file the community has documented), which is then itself used to sign a request that returns the OAuth2 bearer token actually used for API calls. Garmin's flow re-runs this same exchange to "refresh" — there is no separate refresh-token grant.

**Files:**
- Create: `src/MyWhoosh2Garmin/Garmin/OAuth1Token.cs`
- Create: `src/MyWhoosh2Garmin/Garmin/OAuth2Token.cs`
- Modify: `src/MyWhoosh2Garmin/Garmin/GarminAuthClient.cs` — add `GetOAuth1TokenAsync` and `ExchangeForAccessTokenAsync`
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/GarminAuthClientExchangeTests.cs`

**Interfaces:**
- Consumes: `GarminOAuth1Signer.BuildAuthorizationHeader(...)` from Task 5.
- Produces: `record OAuth1Token(string OAuthToken, string OAuthTokenSecret)`. Produces `record OAuth2Token(string AccessToken, string TokenType, DateTimeOffset ExpiresAt) { bool IsExpired(TimeProvider tp); }`. Produces on `GarminAuthClient`: `Task<OAuth1Token> GetOAuth1TokenAsync(string ticket, CancellationToken ct)` and `Task<OAuth2Token> ExchangeForAccessTokenAsync(OAuth1Token oauth1, CancellationToken ct)`. Task 8 persists `OAuth1Token`; Task 10's `SyncPipeline` consumes `OAuth2Token` and calls `ExchangeForAccessTokenAsync` whenever it is null or expired.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/GarminAuthClientExchangeTests.cs
using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminAuthClientExchangeTests
{
    [Fact]
    public async Task GetOAuth1TokenAsync_parses_form_encoded_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Contains("oauth-service/oauth/preauthorized", request.RequestUri!.AbsoluteUri);
            Assert.NotNull(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "oauth_token=tok-123&oauth_token_secret=secret-456", Encoding.UTF8,
                    "application/x-www-form-urlencoded"),
            };
        });
        var client = new GarminAuthClient(new HttpClient(handler));

        OAuth1Token token = await client.GetOAuth1TokenAsync("ST-abc-123", CancellationToken.None);

        Assert.Equal("tok-123", token.OAuthToken);
        Assert.Equal("secret-456", token.OAuthTokenSecret);
    }

    [Fact]
    public async Task ExchangeForAccessTokenAsync_parses_json_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Contains("oauth-service/oauth/exchange/user/2.0", request.RequestUri!.AbsoluteUri);
            string json = """
                {
                    "access_token": "access-789",
                    "token_type": "Bearer",
                    "expires_in": 3600
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var client = new GarminAuthClient(new HttpClient(handler));
        var oauth1 = new OAuth1Token("tok-123", "secret-456");

        OAuth2Token token = await client.ExchangeForAccessTokenAsync(oauth1, CancellationToken.None);

        Assert.Equal("access-789", token.AccessToken);
        Assert.False(token.IsExpired(TimeProvider.System));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter GarminAuthClientExchangeTests`
Expected: FAIL — members don't exist yet.

- [ ] **Step 3: Implement the token records**

```csharp
// src/MyWhoosh2Garmin/Garmin/OAuth1Token.cs
namespace MyWhoosh2Garmin.Garmin;

public record OAuth1Token(string OAuthToken, string OAuthTokenSecret);
```

```csharp
// src/MyWhoosh2Garmin/Garmin/OAuth2Token.cs
namespace MyWhoosh2Garmin.Garmin;

public record OAuth2Token(string AccessToken, string TokenType, DateTimeOffset ExpiresAt)
{
    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
}
```

- [ ] **Step 4: Add the exchange logic to `GarminAuthClient`**

Add these members inside the existing `GarminAuthClient` class from Task 6:

```csharp
    private const string OAuthConsumerUrl = "https://thegarth.s3.amazonaws.com/oauth_consumer.json";
    private (string Key, string Secret)? _cachedConsumer;

    private async Task<(string Key, string Secret)> GetOAuthConsumerAsync(CancellationToken cancellationToken)
    {
        if (_cachedConsumer is not null)
            return _cachedConsumer.Value;

        using var response = await _httpClient.GetAsync(OAuthConsumerUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        _cachedConsumer = (json.GetProperty("consumer_key").GetString()!, json.GetProperty("consumer_secret").GetString()!);
        return _cachedConsumer.Value;
    }

    public async Task<OAuth1Token> GetOAuth1TokenAsync(string ticket, CancellationToken cancellationToken)
    {
        var (consumerKey, consumerSecret) = await GetOAuthConsumerAsync(cancellationToken);

        string baseUrl = "https://connectapi.garmin.com/oauth-service/oauth/preauthorized";
        var queryParams = new Dictionary<string, string>
        {
            ["ticket"] = ticket,
            ["login-url"] = ServiceUrl,
            ["accepts-mfa-tokens"] = "true",
        };

        string authHeader = GarminOAuth1Signer.BuildAuthorizationHeader(
            "GET", baseUrl, queryParams, consumerKey, consumerSecret,
            token: null, tokenSecret: null,
            nonce: Guid.NewGuid().ToString("N"),
            unixTimestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        string url = baseUrl + "?" + string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader.Replace("OAuth ", "OAuth ", StringComparison.Ordinal) is var h ? "OAuth " + authHeader["OAuth ".Length..] : authHeader);
        request.Headers.UserAgent.ParseAdd("com.garmin.android.apps.connectmobile");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = System.Web.HttpUtility.ParseQueryString(body);
        string? oauthToken = parsed["oauth_token"];
        string? oauthTokenSecret = parsed["oauth_token_secret"];
        if (oauthToken is null || oauthTokenSecret is null)
            throw new GarminSsoException("Réponse OAuth1 Garmin incomplète.");

        return new OAuth1Token(oauthToken, oauthTokenSecret);
    }

    public async Task<OAuth2Token> ExchangeForAccessTokenAsync(OAuth1Token oauth1, CancellationToken cancellationToken)
    {
        var (consumerKey, consumerSecret) = await GetOAuthConsumerAsync(cancellationToken);

        string baseUrl = "https://connectapi.garmin.com/oauth-service/oauth/exchange/user/2.0";
        var formParams = new Dictionary<string, string> { ["audience"] = "GARMIN_CONNECT_MOBILE_ANDROID_DI" };

        string authHeader = GarminOAuth1Signer.BuildAuthorizationHeader(
            "POST", baseUrl, formParams, consumerKey, consumerSecret,
            oauth1.OAuthToken, oauth1.OAuthTokenSecret,
            nonce: Guid.NewGuid().ToString("N"),
            unixTimestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new FormUrlEncodedContent(formParams),
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader);
        request.Headers.UserAgent.ParseAdd("com.garmin.android.apps.connectmobile");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        string accessToken = json.GetProperty("access_token").GetString()!;
        string tokenType = json.GetProperty("token_type").GetString()!;
        int expiresIn = json.GetProperty("expires_in").GetInt32();

        return new OAuth2Token(accessToken, tokenType, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
```

Add `using System.Net.Http.Headers;` and `using System.Text.Json;` to the top of the file if not already present from Task 6.

**Note sur `AuthenticationHeaderValue.Parse` :** le header `OAuth ...` construit par le signer contient des paramètres séparés par `, ` — `AuthenticationHeaderValue.Parse` s'attend à `Scheme Parameter`, ce qui fonctionne ici car tout après `OAuth ` est traité comme une seule chaîne de paramètre. Si tu vois une exception de parsing au test manuel (Task 12), remplace cette ligne par `request.Headers.TryAddWithoutValidation("Authorization", authHeader);` qui est plus permissif.

- [ ] **Step 5: Run and watch it pass**

Run: `dotnet test --filter GarminAuthClientExchangeTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/MyWhoosh2Garmin/Garmin tests/MyWhoosh2Garmin.Tests/Garmin
git commit -m "Add OAuth1-to-OAuth2 token exchange for Garmin access tokens"
```

---

### Task 8: Process runner and Keychain token storage

**Files:**
- Create: `src/MyWhoosh2Garmin/Processes/IProcessRunner.cs`
- Create: `src/MyWhoosh2Garmin/Processes/ProcessResult.cs`
- Create: `src/MyWhoosh2Garmin/Processes/SystemProcessRunner.cs`
- Create: `src/MyWhoosh2Garmin/Garmin/ITokenStore.cs`
- Create: `src/MyWhoosh2Garmin/Garmin/MacKeychainTokenStore.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/MacKeychainTokenStoreTests.cs`

**Interfaces:**
- Produces: `interface IProcessRunner { Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments); }`, `record ProcessResult(int ExitCode, string StandardOutput, string StandardError)`, `class SystemProcessRunner : IProcessRunner`.
- Produces: `interface ITokenStore { Task SaveOAuth1TokenAsync(OAuth1Token token); Task<OAuth1Token?> LoadOAuth1TokenAsync(); }`, `class MacKeychainTokenStore(IProcessRunner processRunner) : ITokenStore`. Task 10's `SyncPipeline` and Task 11's `login` command consume `ITokenStore`.

- [ ] **Step 1: Write the failing tests for `MacKeychainTokenStore`**

A hand-written fake `IProcessRunner` stands in for the real `security` CLI, so the test verifies the right arguments are built and the JSON round-trips, without touching the real macOS Keychain.

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/MacKeychainTokenStoreTests.cs
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = new();
    public Func<string, IReadOnlyList<string>, ProcessResult> Respond { get; set; }
        = (_, _) => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Respond(fileName, arguments));
    }
}

public class MacKeychainTokenStoreTests
{
    [Fact]
    public async Task SaveOAuth1TokenAsync_calls_security_add_generic_password_with_json()
    {
        var runner = new FakeProcessRunner();
        var store = new MacKeychainTokenStore(runner);

        await store.SaveOAuth1TokenAsync(new OAuth1Token("tok-123", "secret-456"));

        var addCall = runner.Calls.Single(c => c.Arguments.Contains("add-generic-password"));
        Assert.Equal("security", addCall.FileName);
        int passwordFlagIndex = addCall.Arguments.ToList().IndexOf("-w");
        string storedJson = addCall.Arguments[passwordFlagIndex + 1];
        Assert.Contains("tok-123", storedJson);
        Assert.Contains("secret-456", storedJson);
    }

    [Fact]
    public async Task LoadOAuth1TokenAsync_returns_null_when_nothing_stored()
    {
        var runner = new FakeProcessRunner
        {
            Respond = (_, _) => new ProcessResult(44, "", "security: SecKeychainSearchCopyNext: The specified item could not be found."),
        };
        var store = new MacKeychainTokenStore(runner);

        OAuth1Token? token = await store.LoadOAuth1TokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task LoadOAuth1TokenAsync_parses_stored_json()
    {
        var runner = new FakeProcessRunner
        {
            Respond = (_, args) => args.Contains("find-generic-password")
                ? new ProcessResult(0, """{"OAuthToken":"tok-123","OAuthTokenSecret":"secret-456"}""" + "\n", "")
                : new ProcessResult(0, "", ""),
        };
        var store = new MacKeychainTokenStore(runner);

        OAuth1Token? token = await store.LoadOAuth1TokenAsync();

        Assert.Equal(new OAuth1Token("tok-123", "secret-456"), token);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter MacKeychainTokenStoreTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement the process runner abstraction**

```csharp
// src/MyWhoosh2Garmin/Processes/ProcessResult.cs
namespace MyWhoosh2Garmin.Processes;

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

```csharp
// src/MyWhoosh2Garmin/Processes/IProcessRunner.cs
namespace MyWhoosh2Garmin.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments);
}
```

```csharp
// src/MyWhoosh2Garmin/Processes/SystemProcessRunner.cs
using System.Diagnostics;

namespace MyWhoosh2Garmin.Processes;

public class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Impossible de démarrer le processus '{fileName}'.");

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 4: Implement the Keychain token store**

```csharp
// src/MyWhoosh2Garmin/Garmin/ITokenStore.cs
namespace MyWhoosh2Garmin.Garmin;

public interface ITokenStore
{
    Task SaveOAuth1TokenAsync(OAuth1Token token);
    Task<OAuth1Token?> LoadOAuth1TokenAsync();
}
```

```csharp
// src/MyWhoosh2Garmin/Garmin/MacKeychainTokenStore.cs
using System.Text.Json;
using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Garmin;

public class MacKeychainTokenStore : ITokenStore
{
    private const string ServiceName = "MyWhoosh2Garmin-GarminOAuth1";
    private readonly IProcessRunner _processRunner;

    public MacKeychainTokenStore(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task SaveOAuth1TokenAsync(OAuth1Token token)
    {
        string json = JsonSerializer.Serialize(token);

        // Ignore the result: this fails harmlessly if no entry exists yet.
        await _processRunner.RunAsync("security", ["delete-generic-password", "-s", ServiceName]);

        var result = await _processRunner.RunAsync(
            "security", ["add-generic-password", "-s", ServiceName, "-a", Environment.UserName, "-w", json]);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Échec d'écriture dans le Keychain : {result.StandardError}");
    }

    public async Task<OAuth1Token?> LoadOAuth1TokenAsync()
    {
        var result = await _processRunner.RunAsync(
            "security", ["find-generic-password", "-s", ServiceName, "-w"]);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return null;

        return JsonSerializer.Deserialize<OAuth1Token>(result.StandardOutput.Trim());
    }
}
```

- [ ] **Step 5: Run and watch it pass**

Run: `dotnet test --filter "MacKeychainTokenStoreTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/MyWhoosh2Garmin/Processes src/MyWhoosh2Garmin/Garmin tests/MyWhoosh2Garmin.Tests/Garmin
git commit -m "Add process runner abstraction and macOS Keychain token storage"
```

---

### Task 9: Garmin upload client

**Context:** Sends the `.fit` file to Garmin Connect's upload endpoint. The exact JSON shape below (`detailedImportResult`, `successes`, `failures`, code `202` for a duplicate) matches community documentation of this unofficial endpoint — flag it during the manual end-to-end test in Task 12 if a real response doesn't match, and adjust the parsing then.

**Files:**
- Create: `src/MyWhoosh2Garmin/Garmin/UploadResult.cs`
- Create: `src/MyWhoosh2Garmin/Garmin/GarminUploadClient.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/Garmin/GarminUploadClientTests.cs`

**Interfaces:**
- Produces: `enum UploadOutcome { Uploaded, Duplicate, Failed }`, `record UploadResult(UploadOutcome Outcome, string? GarminActivityId, string? ErrorMessage)`, `class GarminUploadClient(HttpClient httpClient) { Task<UploadResult> UploadFitFileAsync(string filePath, string accessToken, CancellationToken ct); }`. Task 10's `SyncPipeline` consumes this.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/MyWhoosh2Garmin.Tests/Garmin/GarminUploadClientTests.cs
using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminUploadClientTests
{
    private static string CreateTempFitFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MyNewActivity-{Guid.NewGuid():N}.fit");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_uploaded_with_activity_id_on_success()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            string json = """
                { "detailedImportResult": { "successes": [ { "internalId": 999 } ], "failures": [] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Uploaded, result.Outcome);
        Assert.Equal("999", result.GarminActivityId);
        File.Delete(filePath);
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_duplicate_when_garmin_reports_code_202()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "detailedImportResult": { "successes": [], "failures": [ { "code": 202, "message": "Duplicate" } ] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Duplicate, result.Outcome);
        File.Delete(filePath);
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_failed_with_message_on_other_error()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "detailedImportResult": { "successes": [], "failures": [ { "code": 500, "message": "Corrupt file" } ] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Failed, result.Outcome);
        Assert.Equal("Corrupt file", result.ErrorMessage);
        File.Delete(filePath);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter GarminUploadClientTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

```csharp
// src/MyWhoosh2Garmin/Garmin/UploadResult.cs
namespace MyWhoosh2Garmin.Garmin;

public enum UploadOutcome { Uploaded, Duplicate, Failed }

public record UploadResult(UploadOutcome Outcome, string? GarminActivityId, string? ErrorMessage);
```

```csharp
// src/MyWhoosh2Garmin/Garmin/GarminUploadClient.cs
using System.Net.Http.Headers;
using System.Text.Json;

namespace MyWhoosh2Garmin.Garmin;

public class GarminUploadClient
{
    private const int DuplicateActivityCode = 202;
    private readonly HttpClient _httpClient;

    public GarminUploadClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UploadResult> UploadFitFileAsync(string filePath, string accessToken, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        using var fileContent = new ByteArrayContent(fileBytes);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://connectapi.garmin.com/upload-service/upload/.fit")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("GCM-iOS-5.22.1.4");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(body);
        var detailedResult = doc.RootElement.GetProperty("detailedImportResult");

        if (detailedResult.TryGetProperty("successes", out var successes) && successes.GetArrayLength() > 0)
        {
            string activityId = successes[0].GetProperty("internalId").ToString();
            return new UploadResult(UploadOutcome.Uploaded, activityId, null);
        }

        if (detailedResult.TryGetProperty("failures", out var failures) && failures.GetArrayLength() > 0)
        {
            var failure = failures[0];
            int code = failure.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;
            string message = failure.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";

            return code == DuplicateActivityCode
                ? new UploadResult(UploadOutcome.Duplicate, null, null)
                : new UploadResult(UploadOutcome.Failed, null, message);
        }

        return new UploadResult(UploadOutcome.Failed, null, $"Réponse Garmin inattendue : {body}");
    }
}
```

- [ ] **Step 4: Run and watch it pass**

Run: `dotnet test --filter GarminUploadClientTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/MyWhoosh2Garmin/Garmin tests/MyWhoosh2Garmin.Tests/Garmin
git commit -m "Add Garmin Connect .fit upload client"
```

---

### Task 10: Notifications and the sync pipeline

**Files:**
- Create: `src/MyWhoosh2Garmin/Notifications/IMacNotifier.cs`
- Create: `src/MyWhoosh2Garmin/Notifications/OsascriptNotifier.cs`
- Create: `src/MyWhoosh2Garmin/SyncPipeline.cs`
- Test: `tests/MyWhoosh2Garmin.Tests/SyncPipelineTests.cs`

**Interfaces:**
- Consumes: `IActivityTracker` (Task 3), `ITokenStore`, `GarminAuthClient`, `OAuth1Token`, `OAuth2Token`, `GarminUploadClient`, `UploadOutcome` (Tasks 6-9), `FileHasher` (Task 2).
- Produces: `interface IMacNotifier { Task NotifyAsync(string title, string message); }`, `class OsascriptNotifier(IProcessRunner processRunner) : IMacNotifier`. Produces `class SyncPipeline(IActivityTracker tracker, ITokenStore tokenStore, GarminAuthClient authClient, GarminUploadClient uploadClient, IMacNotifier notifier, ILogger<SyncPipeline> logger, TimeProvider? timeProvider = null) { Task HandleNewActivityAsync(string filePath, CancellationToken ct); }`. Task 11 wires this into the host and passes `pipeline.HandleNewActivityAsync` as the watcher's `onActivityReady` callback.

- [ ] **Step 1: Write the failing pipeline tests, with hand-written fakes**

```csharp
// tests/MyWhoosh2Garmin.Tests/SyncPipelineTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using MyWhoosh2Garmin;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;

namespace MyWhoosh2Garmin.Tests;

public class FakeActivityTracker : IActivityTracker
{
    public HashSet<string> ProcessedHashes { get; } = new();
    public string? LastMarkedFileName { get; private set; }
    public string? LastMarkedActivityId { get; private set; }

    public Task<bool> IsProcessedAsync(string fileHash) => Task.FromResult(ProcessedHashes.Contains(fileHash));

    public Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId)
    {
        ProcessedHashes.Add(fileHash);
        LastMarkedFileName = fileName;
        LastMarkedActivityId = garminActivityId;
        return Task.CompletedTask;
    }
}

public class FakeTokenStore : ITokenStore
{
    public OAuth1Token? StoredToken { get; set; }
    public Task SaveOAuth1TokenAsync(OAuth1Token token) { StoredToken = token; return Task.CompletedTask; }
    public Task<OAuth1Token?> LoadOAuth1TokenAsync() => Task.FromResult(StoredToken);
}

public class FakeMacNotifier : IMacNotifier
{
    public List<(string Title, string Message)> Notifications { get; } = new();
    public Task NotifyAsync(string title, string message)
    {
        Notifications.Add((title, message));
        return Task.CompletedTask;
    }
}

public class SyncPipelineTests
{
    private static string CreateTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pipeline-test-{Guid.NewGuid():N}.fit");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Skips_already_processed_files_without_calling_garmin()
    {
        string filePath = CreateTempFile();
        var tracker = new FakeActivityTracker();
        tracker.ProcessedHashes.Add(MyWhoosh2Garmin.Utilities.FileHasher.ComputeSha256(filePath));
        var tokenStore = new FakeTokenStore { StoredToken = new OAuth1Token("t", "s") };
        var notifier = new FakeMacNotifier();

        // authClient/uploadClient are real instances but must never be called for this test to pass,
        // since there's no HttpClient wired to a fake handler here — any real call would throw.
        var pipeline = new SyncPipeline(
            tracker, tokenStore,
            new GarminAuthClient(new HttpClient()),
            new GarminUploadClient(new HttpClient()),
            notifier, NullLogger<SyncPipeline>.Instance);

        await pipeline.HandleNewActivityAsync(filePath, CancellationToken.None);

        Assert.Empty(notifier.Notifications);
        File.Delete(filePath);
    }

    [Fact]
    public async Task Logs_and_returns_when_no_garmin_account_linked()
    {
        string filePath = CreateTempFile();
        var tracker = new FakeActivityTracker();
        var tokenStore = new FakeTokenStore { StoredToken = null };
        var notifier = new FakeMacNotifier();

        var pipeline = new SyncPipeline(
            tracker, tokenStore,
            new GarminAuthClient(new HttpClient()),
            new GarminUploadClient(new HttpClient()),
            notifier, NullLogger<SyncPipeline>.Instance);

        await pipeline.HandleNewActivityAsync(filePath, CancellationToken.None);

        Assert.Empty(notifier.Notifications);
        Assert.Empty(tracker.ProcessedHashes);
        File.Delete(filePath);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter SyncPipelineTests`
Expected: FAIL — `SyncPipeline`/`IMacNotifier` don't exist.

- [ ] **Step 3: Implement the notifier**

```csharp
// src/MyWhoosh2Garmin/Notifications/IMacNotifier.cs
namespace MyWhoosh2Garmin.Notifications;

public interface IMacNotifier
{
    Task NotifyAsync(string title, string message);
}
```

```csharp
// src/MyWhoosh2Garmin/Notifications/OsascriptNotifier.cs
using MyWhoosh2Garmin.Processes;

namespace MyWhoosh2Garmin.Notifications;

public class OsascriptNotifier : IMacNotifier
{
    private readonly IProcessRunner _processRunner;

    public OsascriptNotifier(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public Task NotifyAsync(string title, string message)
    {
        string script = $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"";
        return _processRunner.RunAsync("osascript", ["-e", script]);
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}
```

- [ ] **Step 4: Implement the pipeline**

```csharp
// src/MyWhoosh2Garmin/SyncPipeline.cs
using Microsoft.Extensions.Logging;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;
using MyWhoosh2Garmin.Utilities;

namespace MyWhoosh2Garmin;

public class SyncPipeline
{
    private readonly IActivityTracker _tracker;
    private readonly ITokenStore _tokenStore;
    private readonly GarminAuthClient _authClient;
    private readonly GarminUploadClient _uploadClient;
    private readonly IMacNotifier _notifier;
    private readonly ILogger<SyncPipeline> _logger;
    private readonly TimeProvider _timeProvider;
    private OAuth2Token? _cachedAccessToken;

    public SyncPipeline(
        IActivityTracker tracker,
        ITokenStore tokenStore,
        GarminAuthClient authClient,
        GarminUploadClient uploadClient,
        IMacNotifier notifier,
        ILogger<SyncPipeline> logger,
        TimeProvider? timeProvider = null)
    {
        _tracker = tracker;
        _tokenStore = tokenStore;
        _authClient = authClient;
        _uploadClient = uploadClient;
        _notifier = notifier;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleNewActivityAsync(string filePath, CancellationToken cancellationToken)
    {
        string hash = FileHasher.ComputeSha256(filePath);

        if (await _tracker.IsProcessedAsync(hash))
        {
            _logger.LogInformation("Activité déjà synchronisée, ignorée : {File}", filePath);
            return;
        }

        OAuth1Token? oauth1 = await _tokenStore.LoadOAuth1TokenAsync();
        if (oauth1 is null)
        {
            _logger.LogError("Aucun compte Garmin lié. Lance la commande `login` d'abord.");
            return;
        }

        if (_cachedAccessToken is null || _cachedAccessToken.IsExpired(_timeProvider))
        {
            _cachedAccessToken = await _authClient.ExchangeForAccessTokenAsync(oauth1, cancellationToken);
        }

        UploadResult result = await _uploadClient.UploadFitFileAsync(filePath, _cachedAccessToken.AccessToken, cancellationToken);

        if (result.Outcome is UploadOutcome.Uploaded or UploadOutcome.Duplicate)
        {
            await _tracker.MarkProcessedAsync(hash, Path.GetFileName(filePath), result.GarminActivityId);
            await _notifier.NotifyAsync("MyWhoosh → Garmin", "Activité synchronisée ✅");
        }
        else
        {
            _logger.LogError("Échec de l'upload vers Garmin pour {File} : {Error}", filePath, result.ErrorMessage);
        }
    }
}
```

- [ ] **Step 5: Run and watch it pass**

Run: `dotnet test --filter SyncPipelineTests`
Expected: PASS

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: every test across all 10 tasks so far passes.

- [ ] **Step 7: Commit**

```bash
git add src/MyWhoosh2Garmin/Notifications src/MyWhoosh2Garmin/SyncPipeline.cs tests/MyWhoosh2Garmin.Tests/SyncPipelineTests.cs
git commit -m "Add macOS notifier and the end-to-end sync pipeline"
```

---

### Task 11: Host wiring and the `login` command

**Files:**
- Modify: `src/MyWhoosh2Garmin/Program.cs`

**Interfaces:**
- Consumes everything produced in Tasks 2-10.
- Produces: the runnable application — `dotnet run -- login` and `dotnet run` (no args, starts the background watcher).

- [ ] **Step 1: Replace `Program.cs`**

```csharp
// src/MyWhoosh2Garmin/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Processes;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;
using MyWhoosh2Garmin.Watching;

if (args.Length > 0 && args[0] == "login")
{
    await RunLoginCommandAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

string databasePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MyWhoosh2Garmin", "tracker.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddSingleton<IActivityTracker>(_ => new SqliteActivityTracker($"Data Source={databasePath}"));
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<ITokenStore, MacKeychainTokenStore>();
builder.Services.AddSingleton<IMacNotifier, OsascriptNotifier>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<GarminAuthClient>();
builder.Services.AddSingleton<GarminUploadClient>();
builder.Services.AddSingleton<SyncPipeline>();
builder.Services.AddSingleton<IFileStabilityChecker, FileStabilityChecker>();

builder.Services.AddHostedService(sp => new ActivityFileWatcherService(
    MyWhooshFolderLocator.GetActivitiesFolder(),
    sp.GetRequiredService<IFileStabilityChecker>(),
    (path, ct) => sp.GetRequiredService<SyncPipeline>().HandleNewActivityAsync(path, ct),
    sp.GetRequiredService<ILogger<ActivityFileWatcherService>>()));

var host = builder.Build();
await host.RunAsync();

async Task RunLoginCommandAsync()
{
    Console.Write("Email Garmin : ");
    string email = Console.ReadLine() ?? throw new InvalidOperationException("Email requis.");

    Console.Write("Mot de passe Garmin : ");
    string password = ReadPasswordMasked();

    using var httpClient = new HttpClient();
    var authClient = new GarminAuthClient(httpClient);
    var tokenStore = new MacKeychainTokenStore(new SystemProcessRunner());

    Console.WriteLine("Connexion à Garmin...");
    string ticket = await authClient.LoginAndGetTicketAsync(email, password, CancellationToken.None);
    OAuth1Token oauth1 = await authClient.GetOAuth1TokenAsync(ticket, CancellationToken.None);

    // Confirms the token pair actually works before saving it.
    await authClient.ExchangeForAccessTokenAsync(oauth1, CancellationToken.None);

    await tokenStore.SaveOAuth1TokenAsync(oauth1);
    Console.WriteLine("Compte Garmin lié avec succès. Le service peut maintenant tourner en tâche de fond.");
}

string ReadPasswordMasked()
{
    var password = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password.Length--;
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    Console.WriteLine();
    return password.ToString();
}
```

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: builds with no errors.

- [ ] **Step 3: Run the full test suite one more time**

Run: `dotnet test`
Expected: all tests still pass (`Program.cs` has no tests of its own — it's pure wiring, covered indirectly by every component test above).

- [ ] **Step 4: Commit**

```bash
git add src/MyWhoosh2Garmin/Program.cs
git commit -m "Wire the host, background service, and interactive login command"
```

---

### Task 12: macOS packaging and manual end-to-end verification

**Files:**
- Create: `packaging/com.mywhoosh2garmin.plist`
- Create: `docs/RUNNING.md`

**Interfaces:** None — this task packages and manually verifies the application built in Tasks 1-11.

- [ ] **Step 1: Publish a self-contained build**

```bash
dotnet publish src/MyWhoosh2Garmin -c Release -r osx-arm64 --self-contained -o publish/
```

(Use `osx-x64` instead of `osx-arm64` if not on Apple Silicon.)

- [ ] **Step 2: Run the login command manually**

```bash
./publish/MyWhoosh2Garmin login
```

Enter your real Garmin email/password when prompted. Expected: "Compte Garmin lié avec succès." If this throws a `GarminSsoException`, read the message — it will say whether Garmin rejected the credentials or the response shape didn't match what Task 6/7 expected (Garmin's endpoints are unofficial and can change).

- [ ] **Step 3: Verify the token landed in the Keychain**

```bash
security find-generic-password -s MyWhoosh2Garmin-GarminOAuth1 -w
```

Expected: prints the JSON `{"OAuthToken":"...","OAuthTokenSecret":"..."}`.

- [ ] **Step 4: Do a real MyWhoosh ride and watch it sync**

Run `./publish/MyWhoosh2Garmin` (no args) in a terminal you can watch. Complete a short MyWhoosh activity (even a minute or two is enough). Expected: within a few seconds of ending the ride, the console logs an upload attempt and a macOS notification "Activité synchronisée avec Garmin ✅" appears; the activity shows up in Garmin Connect.

If the upload fails, capture the logged error message — it will tell you which of the assumptions in Task 9 (the exact JSON field names in Garmin's response) didn't match reality, and that's the one place in this plan to adjust based on a real server response rather than documentation.

- [ ] **Step 5: Write the LaunchAgent plist**

```xml
<!-- packaging/com.mywhoosh2garmin.plist -->
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.mywhoosh2garmin</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/mywhoosh2garmin</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/mywhoosh2garmin.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/mywhoosh2garmin.error.log</string>
</dict>
</plist>
```

- [ ] **Step 6: Write install/run instructions**

```markdown
<!-- docs/RUNNING.md -->
# Installation et lancement

1. `dotnet publish src/MyWhoosh2Garmin -c Release -r osx-arm64 --self-contained -o publish/`
2. `sudo cp publish/MyWhoosh2Garmin /usr/local/bin/mywhoosh2garmin`
3. `/usr/local/bin/mywhoosh2garmin login` (une seule fois, ou à chaque révocation du compte Garmin)
4. `cp packaging/com.mywhoosh2garmin.plist ~/Library/LaunchAgents/`
5. `launchctl load ~/Library/LaunchAgents/com.mywhoosh2garmin.plist`

Le service tourne désormais en tâche de fond et démarre automatiquement à la connexion.

Pour l'arrêter : `launchctl unload ~/Library/LaunchAgents/com.mywhoosh2garmin.plist`

Logs : `/tmp/mywhoosh2garmin.log` et `/tmp/mywhoosh2garmin.error.log`.
```

- [ ] **Step 7: Commit**

```bash
git add packaging docs/RUNNING.md
git commit -m "Add macOS LaunchAgent packaging and run instructions"
```

---

## Self-review notes

- **Spec coverage:** every component in the design doc (watcher, auth, uploader, tracker, notifications, error handling for expired tokens/network failures/missing folder, TDD) maps to a task above. The design doc's "dedup even after restart" requirement is met because `SqliteActivityTracker` persists to a file-backed database in production (`Program.cs` Task 11), even though tests use `:memory:`.
- **Known open risk, called out rather than hidden:** the exact JSON field names Garmin's upload endpoint returns (Task 9) and the OAuth endpoints' exact behavior (Tasks 6-7) come from the `garth` Python library's current source and community documentation of an *unofficial* API — Task 12 Step 4 is where reality gets checked against these assumptions, and adjusting Task 9's parsing is the expected, normal outcome if Garmin's actual response differs.
- **MFA:** the design and this plan assume a Garmin account without multi-factor authentication enabled, matching the simplest case in `garth`'s own login flow. If your Garmin account has MFA on, `LoginAndGetTicketAsync` will need an additional step (an `MFA_REQUIRED` response type before the ticket step) — out of scope for this first version; disable MFA on the Garmin account or note this as a follow-up if it's needed.
