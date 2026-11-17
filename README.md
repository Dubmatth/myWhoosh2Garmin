# MyWhoosh2Garmin

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![macOS](https://img.shields.io/badge/macOS-000000?logo=apple&logoColor=white)
![Tests: xUnit](https://img.shields.io/badge/tests-xUnit-5C2D91)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

A macOS background service that automatically syncs [MyWhoosh](https://www.mywhoosh.com/) indoor rides to **Garmin Connect** — no need to start an activity on your watch.

Zwift pushes every ride to Garmin Connect out of the box; MyWhoosh doesn't. This service fills that gap.

## How it works

```
MyWhoosh writes .fit ──► FileWatcher ──► stability check ──► SQLite dedup ──► Garmin auth ──► upload ──► macOS notification
```

1. A .NET `BackgroundService` watches the folder where MyWhoosh writes its `.fit` files at the end of each ride.
2. Once a new file is stable (fully written), it is hashed and checked against a local **SQLite** database to avoid duplicates.
3. The service authenticates with Garmin (unofficial mobile flow, tokens cached in the **macOS Keychain**) and uploads the file.
4. A native macOS notification confirms the sync.

Garmin has no public API for individuals, so authentication and upload reproduce the mobile flow used by the community reference [`garth`](https://github.com/matin/garth) (Python) — OAuth1 → OAuth2 exchange, MFA code support included.

## Highlights

- **Clean, testable design** — every network/system boundary (Garmin HTTP, Keychain, external processes, notifications) sits behind an injectable interface.
- **Test-driven** — xUnit suite with hand-written test doubles instead of a mocking framework (`FakeHttpMessageHandler`, `FakeTokenStore`, …).
- **Robust file handling** — waits for files to stop changing before processing; content hashing prevents double uploads.
- **Secure token storage** — OAuth tokens live in the macOS Keychain, never on disk in plain text.

## Requirements

- macOS (Apple Silicon or Intel)
- [.NET SDK 10](https://dotnet.microsoft.com/download) or later
- A Garmin Connect account
- MyWhoosh installed and used at least once (so its data folder exists)

## Installation

```bash
git clone https://github.com/Dubmatth/myWhoosh2Garmin.git
cd myWhoosh2Garmin
dotnet publish src/MyWhoosh2Garmin -c Release -r osx-arm64 --self-contained -o publish/
# use -r osx-x64 on an Intel Mac
sudo cp publish/MyWhoosh2Garmin /usr/local/bin/mywhoosh2garmin
```

Link your Garmin account (once):

```bash
mywhoosh2garmin login
```

If MFA is enabled on your account, you'll be prompted for the code sent by email/SMS.

Run automatically in the background (macOS LaunchAgent):

```bash
cp packaging/com.mywhoosh2garmin.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.mywhoosh2garmin.plist
```

Detailed instructions, logs and manual checks: [`docs/RUNNING.md`](docs/RUNNING.md).

## Development

```bash
dotnet build
dotnet test
```

Design docs: [architecture spec](docs/superpowers/specs/) and [implementation plan](docs/superpowers/plans/).

## Known limitations

- **Incomplete Garmin training metrics.** Normalized Power, TSS, Intensity Factor and Training Effect are computed *on the watch* by Garmin's Firstbeat engine, not by Garmin Connect. MyWhoosh `.fit` files don't contain them, so they won't appear on synced activities.

  | Data | Recorded on a Garmin watch | Via MyWhoosh2Garmin |
  |---|---|---|
  | Heart rate, power, cadence | ✅ | ✅ identical |
  | Distance, duration, calories | ✅ | ⚠️ computed by MyWhoosh (virtual route) |
  | NP / TSS / IF / Training Effect | ✅ | ❌ missing |

- **VO2 max is never updated** for indoor/virtual rides, whatever the source (a Garmin limitation).
- **MFA:** only code-based MFA (email/SMS) is supported, not push approval.
- **Unofficial API:** Garmin may change its mobile flow at any time. Use at your own risk regarding Garmin's terms of service.
- **macOS only**, with a fixed MyWhoosh install path (a missing folder is logged as an error at startup).

## License

[MIT](LICENSE)
