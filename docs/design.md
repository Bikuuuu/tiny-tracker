# Tiny Tracker: Design

## 1. Summary

Tiny Tracker is a Windows 11 tray app that keeps the apps you choose up to date, using winget. It has no main window. Everything happens in a Windows 11-style flyout that opens from the notification area (including the hidden-icons menu) or from a keyboard shortcut. It checks on a schedule and shows live progress. It installs updates on request, or by itself for apps where you turn on Auto.

## 2. Goals and non-goals

**Goals**
- A native Windows 11 look that feels "modern, live, dynamic": Fluent controls, acrylic, the system light/dark mode and accent color, smooth motion, live progress.
- It only updates the apps the user chose. It never installs new software, uninstalls anything, or changes app settings.
- It leaves nothing behind. See §10.
- Nearly zero cost while idle. See §9.
- Safe admin handling. The UI never runs elevated.
- Public, MIT-licensed, and buildable from source by anyone.

**Non-goals for v1**
- Microsoft Store distribution: the Store blocks the elevated helper that silent mode needs.
- Windows 10.
- ARM64 builds. They come later, once someone can test them.
- Package managers other than winget.
- Apps that are not in winget's catalog.
- Telemetry or analytics of any kind.
- Languages other than English. Strings live in resource files so translations can be added later.

## 3. Decisions

| Topic | Decision |
|---|---|
| Audience | Public GitHub repo, MIT license. Public |
| Name | "Tiny Tracker" everywhere, and `TinyTracker` in code and file names. The GitHub repo is `Bikuuuu/tiny-tracker` |
| OS | Windows 11 only |
| Stack | C#, .NET 10 (LTS), WinUI 3 (Windows App SDK, pinned version), unpackaged, self-contained and trimmed, x64 |
| Distribution | Inno Setup 7.1 per-machine installer into Program Files, published on GitHub Releases. No Store |
| Signing | None: releases stay unsigned (§11) |
| First run | All apps are listed and **none is pre-selected**. The user picks, and can change the list later in Settings |
| Admin rights | Default: ask once per batch. Opt-in "Install updates without asking" (silent mode) |
| Open apps | Try the update first. If the app is in use, offer **Close & update** (close, update, reopen). Never close an app without the user's OK |
| Look | Match Windows: system mode and accent color, acrylic, Segoe UI Variable, Segoe Fluent Icons |
| Icon | Original vector hamster (pink knitted hood, gold heart sunglasses). Detailed art at 32 px and up, simplified art for 16–24 px |
| Extras in v1 | Skip version, History, release notes in the flyout (What's new), security fixes first, Auto for every app at once, wait N days before auto-install, an install window for automatic updates, notification levels, offering newly installed apps, backing up and restoring the list, cancel, self-update, pause during games, global shortcut, download speed limit |
| Testing | Automated on GitHub-hosted runners, plus a manual checklist on a real PC |

## 4. User experience

Screenshot: [`flyout.png`](images/flyout.png), from the demo. Its made-up apps have no icons, so they show letter tiles.

### 4.1 Tray icon
- It shows the simplified hamster at 16/20/24 px and the detailed art at 32 px and up. Assets are pre-rendered from the SVG sources in `assets/icon/`.
- **States:**
  - Idle: plain icon.
  - Updates ready: a small white dot with a dark outline in the corner, visible on dark, light and accent taskbars.
  - Working: a subtle frame animation that runs only while a check or install is active.
- **Tooltip:** "Tiny Tracker: 3 updates ready", "…: Installing Example Editor (45%)", or "…: Up to date".
- **Left-click** toggles the flyout. **Right-click** opens a menu: Open, Check now, Update all, Settings, Quit. The menu follows the Windows mode, as the flyout does, and changes with it. If Windows ever drops the switch for dark menus, it stays light.

### 4.2 Flyout window
- 380 px wide at every text size, like Windows' own flyouts. Its height fits the content, up to 70% of the work area; beyond that the list scrolls.
- **Height changes:** the new height is measured before the window changes, so each change resizes it once, never to an old height first. It grows at once, as a page starts to slide in or a row appears. It shrinks at once, except when a row or notice leaves a list on Updates: then, unless the page changes, it waits until the rest has moved into place (about half a second), so nothing is cut off. It doesn't glide, since Windows shows a window's new size a frame before WinUI draws it (§13).
- It sits bottom-right above the taskbar, 12 px from the edges, on the monitor that holds the taskbar. It is DPI-aware on every monitor.
- **Appearance:** acrylic backdrop, rounded corners, Windows 11 flyout border and shadow; in high contrast, the theme's window color. It follows the system (Windows) mode and the accent color live.
- **Opening:** it slides up and fades in, like the system flyouts. It opens instantly because the window is created at startup and hidden with DWM cloaking, never re-created.
- **Closing:** clicking outside (focus loss) or pressing Esc. Clicking the tray icon while the flyout is open closes it; a debounce prevents the close-then-reopen bug.
- It has no taskbar button and doesn't appear in Alt+Tab.
- Pages slide in with a back arrow: **Updates** (home), **Choose apps**, **Settings**, **History**, **What's new**.
- When opened, it shows cached results at once. If the last check is more than 15 minutes old, it refreshes live and the refresh icon spins.

### 4.3 Updates page (home)
- **Header:** hamster icon, app name, and icon buttons for Check now, History and Settings. Below them, a summary: "3 updates ready · checked 2 min ago".
- **Updates** section: one card per app, sorted with in-progress first, then security fixes (§6.2), then needs-attention, then available. Each card shows:
  - the real app icon, kept in memory only
  - the name, with an **Auto** pill if the app is on Auto (§6.2) (in high contrast, the theme's button colors with a thin border, so it differs from the Security pill)
  - old → new version, with the changed part of the new version in the accent color, and after it a blue **Security** pill (the theme's highlight colors in high contrast) for a security fix while Security fixes first is on (§6.2)
  - a status line and an action
- **Row states:**

| State | Status line | Action |
|---|---|---|
| Available | "2 days ago · What's new" | **Update**, plus the "…" menu |
| Waiting | "Waiting…" | ✕ Cancel |
| Waiting for permission | "Waiting for permission…" while the admin prompt shows | ✕ Cancel |
| Downloading | progress bar and "180 of 400 MB · 20 MB/s" (or "180 MB · 20 MB/s" if the size is unknown) | percent and ✕ Cancel |
| Installing | progress bar (indeterminate if unknown) and "Installing…" | none |
| App in use | "Example Editor is running" | **Close & update** |
| Closing | "Closing Example Editor…" | none |
| Didn't close | "Example Editor didn't close" | **Force close** and ✕ Cancel |
| Can't close | "Close Example Editor, then try again" | **Retry** |
| Needs permission | "Needs admin permission", or "Permission was declined" after a declined prompt | **Install** (one prompt for the batch) |
| Failed | the reason in plain words | **Retry**, and **Details** (technical code) |
| Restart needed | "Restart to finish" | none |
| Updated | "Updated to 2025.1.3" | none; the row animates into "Up to date" |
| Phantom | "Installed, but Windows still reports the old version" | **Update anyway** |
| Phantom, App Installer (§6.2) | "Can't finish while Tiny Tracker runs: quit it and open it again" | none |
| Version unknown | "Version unknown · this app updates itself" | none |
| Not found | "Not installed anymore" / "Not found in winget" | **Stop tracking** |

- **Tiny Tracker's own update** (§6.5) takes a row at the top of Updates: "Tiny Tracker 0.2.0", "0.1.0 → 0.2.0 · What's new" (its release page) and **Update**. It then shows "Waits for the other updates", "Waiting for permission…", Downloading with its progress and ✕ Cancel, and "Installing… Tiny Tracker will restart", with no action. When it can't install, it shows "Close Tiny Tracker for the other accounts on this PC, then try again" or "Couldn't update Tiny Tracker" with the reason, such as "The download didn't match GitHub's record" or "Couldn't reach GitHub", and **Retry**.
- **Uninstalled apps:** an app that's "Not installed anymore" stops being tracked by itself once a check at least a day after the first miss still doesn't find it. A check that finds it again starts the day over, so a brief miss, such as an app updating itself, removes nothing. "Not found in winget" apps are still installed, so they stay until the user acts.
- **Auto rows that wait:** an Available row whose app has Auto on shows why it hasn't installed yet, in place of the release age: "Installs automatically in 2 days" (the wait setting), "Installs automatically at 22:00" (the install window), "Waits for the network", "Waits for an unmetered connection" or "Waits for Energy saver to turn off". In default mode, an update that needs admin shows the Needs permission state (§6.3). A full-screen app holds it without a message.
- **The "…" menu:** Update now, Skip this version (Undo is in the same menu), Auto-update on/off for this app alone (§6.2), What's new, Stop tracking. Stop tracking shows an inline "Removed · Undo" for 5 s.
- **Up to date:** a collapsed card ("9 apps are up to date") showing a stack of icons. It expands to the full list and is expanded by default when nothing needs updating. Skipped versions and apps whose version is unknown are listed here too; a skipped app's "…" menu has Undo.
- **Footer:** "Next check in 5 h 58 min" and the accent button **Update all (N)**. N counts rows in the Available, Needs permission or Failed state; skipped, phantom and unknown-version apps are excluded, and so is Tiny Tracker's own row.
- "What's new" opens the What's new page (§4.9) when winget has the text of the release notes, else the release notes URL in the default browser. It is hidden when the package has neither. With **Show What's new links** off (§4.5), status lines leave it out, Tiny Tracker's own included; the "…" menu keeps it.
- The values shown are examples only; the defaults are in §4.5.
- **Empty state** (no apps chosen): the hamster, "Choose the apps you want to keep up to date", and **Choose apps**.
- **Tip after the first Choose apps or Restore:** a one-time tip on Updates says "Windows keeps new icons under ^ on the taskbar. Drag the hamster out to keep it in view." Its ✕ closes it for good.
- **New apps:** a winget app that joins a check's list (§6.1) after the first list, and that the user never tracked or turned down, gets a notice on Updates: "New app: Contoso Editor", with **Track** and **No thanks**. Checks list apps once one is tracked; an app winget only starts to match later counts as new too.
  - **Track** adds it, on Auto when Update apps automatically is on (§6.2), and a check runs for it. **No thanks** means it's never offered again.
  - Each app gets its own notice, which stays until answered. It goes once the app is tracked from Choose apps or Restore, or uninstalled.
  - At most three show at a time, in name order and together, above any tip; answering one brings the next.
  - Tiny Tracker itself is never offered.
  - There's no toast, and the tray badge doesn't count them.
- **Motion:** rows and notices fade in and out, and the rest of their list moves smoothly into place. The problem banner, Tiny Tracker's own row and the opened Up to date list fade in; they go at once, since a fade out would overlap what moves up into their place. Progress is live. The summary crossfades (about 150 ms) when its words change. All motion stops when the flyout is hidden.

### 4.4 Choose apps page
- Opens automatically on first run, and from Settings.
- It lists every installed app that winget can update, sorted by name, with icon, version and publisher. **Nothing is ticked on first run.**
- A search box filters as you type. There's a "Show selected" filter and a "3 of 48 selected" count.
- **Select all**, beside Show selected, ticks every app the list shows, so a search or Show selected narrows it. Once all of them are ticked it reads **Deselect all**, which first asks "Stop tracking 12 apps?" with a **Stop tracking** button; Esc or a click elsewhere cancels. It covers the tracked apps listed with a reason too, but never Updated elsewhere, which has no tick boxes. It's hidden while the list loads and when nothing shows.
- Tracked apps it can't list come first, ticked, each saying why: "Not installed anymore", "Not found in winget", "Version unknown", or "Not found right now" when the last check didn't reach it. The two that are still installed ("Not found in winget" and "Version unknown") keep an app of the same name out of Updated elsewhere, so it shows once. Unticking one stops tracking it. So the page shows every tracked app, and its count agrees with Settings.
- Changes apply as you tick. **Done** returns to Updates, and a check runs for newly added apps.
- At the end of the list, a section named **Updated elsewhere (N)** holds the installed apps that winget can't update. These include games from Steam and other launchers, drivers, Windows components, Microsoft Store apps, and apps whose installed version is unknown. winget reports no available versions for Store apps, so the Store keeps them up to date.
  - It starts collapsed. Expanding it shows every one of these apps, and search filters them too.
  - One line explains that these apps update themselves or through another app, so they can't be tracked here.
  - Rows have no tick box. Each row names what updates the app when that's known, such as Steam, Microsoft Store, Windows Update or a driver tool. Otherwise it says "No exact match in winget" when winget has packages of a similar name but the lookup below can't take one of them, and "Not in winget" when it has none.
- winget's full installed list misses some apps it can update. So the page also looks up the unmatched apps by name and id, and moves each app it finds into the tickable list. A match counts only when it's unambiguous: the names agree, the offered version isn't older than the installed one, and no other package fits as well. Otherwise the app stays under Updated elsewhere, because a wrong match could install a different edition over it.

### 4.5 Settings page

| Section | Setting | Default |
|---|---|---|
| Apps | Choose apps (N tracked) › | none tracked |
| Apps | **Back up** and **Restore** the list of tracked apps (below) | |
| Checking | Check for updates every: 1, 3, 6, 12 or 24 h | 6 h |
| Installing | Update apps automatically: every tracked app is on Auto and installs by itself within the rules below; an app's "…" menu can turn Auto off, or on, for that app alone (§6.2) | Off |
| Installing | Install updates without asking (silent mode; one admin approval to turn on) | Off |
| Installing | Wait before auto-installing: Off, 1, 3 or 7 days | Off |
| Installing | Security fixes first: updates whose notes mention a security fix get the Security pill, come first, and skip the wait before auto-installing (§6.2) | On |
| Installing | Install automatically only at certain hours, then "From [22:00] to [06:00]": whole hours in the Windows time format. The window can cross midnight. The "to" list leaves out the "from" hour, and picking a "from" hour equal to "to" moves "to" one hour on. The note says "Your PC needs to be on then." (§6.2) | Off, 22:00 to 06:00 when enabled |
| Installing | Pause during games and presentations | On |
| Installing | Limit download speed (one admin approval the first time, unless winget's proxy option is already on), then "Enter limit in kilobytes per second [____] KB/s" (100 to 1,000,000), with the MB/s equivalent shown next to it | Off, 17500 when enabled |
| Notifications | Show notifications: All, When I need to act, Only failures or Off, with a line saying what the chosen level shows (§4.7) | All |
| General | Start with Windows | On (installer checkbox) |
| General | Shortcut to open (click to record, Esc to clear) | Win + Shift + U |
| General | Show What's new links: the link after each row's status line (§4.3) | On |
| General | Update Tiny Tracker automatically: it installs by itself where that needs no prompt, that is, with silent mode on; otherwise the note under it says "Updates wait for your click without silent mode". Greyed with "Works once Tiny Tracker is installed" when not run from Program Files (§6.5) | On |
| About | Open logs folder, Copy diagnostic info (versions, counts and settings only: no app names, paths or user names) | |

- **Back up** saves "Tiny Tracker apps.json" through Windows' Save dialog. It holds each tracked app's winget id, name and whether it's on Auto, whether Update apps automatically is on, and nothing else. Names are cut to 256 characters and control characters become spaces, and an id that isn't winget's shape is left out, so a file it writes always restores.
- **Restore** opens such a file through Windows' Open dialog and adds its apps that are installed on this PC, by winget id. From a file made with Update apps automatically on, every app follows this PC's switch. From any other, an app the file has on Auto keeps it, as its own choice while this PC's switch is off, and the others follow the switch (§6.2). It asks winget only which of them are installed. The added apps take winget's id and name.
  - Apps already tracked stay as they are, nothing is removed, and the settings don't change. A check then runs for the added apps.
  - While winget reads the installed apps, the card's text says "Finding which apps are installed…". Clicking again meanwhile does nothing.
  - A notice says what happened: "Added 12 apps. Not installed here: Contoso Chat, Litware Reader.", with up to five names and then "and 3 more". When every app in the file is tracked already, it says so and winget isn't asked.
  - A file that isn't a backup gets "This file isn't a Tiny Tracker app list.", one that can't be read "This file couldn't be read.", and when winget can't answer, "The list couldn't be restored. Try again in a moment."; when winget is missing or too old, a "winget needs an update" notice with **Open Store**. Each new result replaces the last one.
- If Windows' Save or Open dialog doesn't open, Settings shows "The file dialog didn't open." with Details.

The footer shows the version ("v1.0.0"), the GitHub and license links, a heart that opens the author's Ko-fi page ("Support Tiny Tracker" on hover) and a **Quit** button.

### 4.6 History page
- Grouped by the local day ("Today", "Yesterday", "Sep 22"), newest first. Each entry shows the result icon, app, "from → to" or the reason, and the time in the Windows time format.
- The result is one of: Updated / Failed / Skipped / Cancelled. An app's newest entry has **Retry** when it failed and the app's row still offers that same version with Update, Retry, Install or Close & update. Retry does what that button does and returns to Updates, where the row shows the install.
- Tiny Tracker's own updates are entries too: "Tiny Tracker · 0.1.0 → 0.2.0", or one that failed (§6.5).
- It keeps 90 days and shows 50 entries at a time, with **Show older**. The footer has **Clear history**, which asks first and clears what the page showed; an entry written meanwhile stays.

### 4.7 Notifications
- Notifications are Windows toasts with buttons, shown under the app's own name and icon. Clicking the body opens the flyout.

| Trigger | Toast | Buttons |
|---|---|---|
| New versions (Auto off), once per version, batched per check | "3 updates ready" plus names | Update all, View |
| Auto-updates that need admin (default mode), once per version, batched per check | "2 updates need your permission" plus names | Install, Later |
| An update ended "in use" while the flyout was closed | "Example Editor needs to close to update" (or "2 apps need to close to update" plus names) | Close & update, Later |
| Batch finished | "3 updates installed" / "2 updates installed, 1 failed" / "1 update failed", plus "Restart to finish updating Example Editor" when one needs a restart | View |
| Tiny Tracker's own update is ready and waits for a click (§6.5), once per version | "Tiny Tracker 1.1 is available" | Update, Later |
| Tiny Tracker updated itself (§6.5) | "Tiny Tracker was updated to 1.1" | What's new |
| Tiny Tracker couldn't update itself, for a reason that won't pass by itself (§6.5) | "Couldn't update Tiny Tracker" plus the reason | View |

- Update all and Install act without opening the flyout; Install installs every row that needs permission, with one prompt. View and Close & update open it, and Close & update then starts, so a Force close can be answered.
- Apps whose update needs admin (default mode) or that must close get their own toasts: they're not in "updates ready", and the batch toast doesn't count them as failed. An app that Close & update couldn't close counts as failed.
- No toast shows while the flyout is open. The versions it showed count as announced.
- Toasts are held back while a full-screen game or presentation runs or the PC is locked, then shown afterwards as one toast with the current numbers. News still held when the app quits is shown after its next start.
- Toasts live only as long as the app. They're removed when it quits or starts, and at restart, so no COM activator is registered.
- **Levels** (Settings, §4.5):

| Toast | All | When I need to act | Only failures | Off |
|---|---|---|---|---|
| Updates ready, need your permission, needs to close | ✓ | ✓ | | |
| Batch finished, all installed | ✓ | | | |
| Batch finished, with a failure | ✓ | ✓ | ✓ | |
| Batch finished, with a restart needed | ✓ | ✓ | | |
| Tiny Tracker is available | ✓ | ✓ | | |
| Tiny Tracker was updated | ✓ | | | |
| Tiny Tracker couldn't update itself | ✓ | ✓ | ✓ | |

- A batch toast that shows keeps its words, such as "2 updates installed, 1 failed". Toasts a level leaves out count as seen, so changing the level later brings back no old news. At every level, the tray badge still shows what's pending.

### 4.8 Keyboard and accessibility
- The global shortcut opens the flyout, or closes it when it's open (default Win+Shift+U, configurable; Ctrl+Alt is AltGr on many keyboards, where AltGr+U types a character). It needs Ctrl, Alt or Win. If another app already owns the combination, Settings shows "Shortcut in use", and if Windows refuses it for another reason, "Couldn't set this shortcut" (the log has the code): a new one isn't saved, and the saved one is tried again at each start and when Settings opens.
- In the flyout: arrow keys move between rows, Enter runs the primary action, Esc closes an open list, menu or Details first and then goes back or closes, and Tab order follows the layout.
- **Focus:** when the flyout or a page opens, the focus is on the page's first control: Check now on Updates, the search box in Choose apps, Choose apps in Settings, and Back in History and What's new, where Enter only goes back. Going back returns it to the control that opened the page, or to that first control when none did. After **Show older** it's on the first entry added, and after a new app's **Track** or **No thanks**, on the next new app's **Track**, else on Check now. Focus that the app moves shows no tooltip, and the header's and Back's tooltips open below their buttons, inside the flyout.
- Every control has an automation name.
- **High contrast:** everything takes the theme's colors, and the Auto and Security pills stay distinct (§4.3).
- **Text size** (Windows' Text size, up to 225%): the flyout keeps its width, and what no longer fits side by side moves onto its own line: a Settings card's control under its text, the install window's "to" under "From", the MB/s under the speed box, a row's buttons under its status line, and a footer's button under its text. Notice buttons wrap, with Details on a line of its own. The header's title and the version line end in "…" when they don't fit. Icons and letter tiles keep their size.

### 4.9 What's new page
- It opens from an app's What's new, on its status line or in its "…" menu, when winget has the text of its release notes (§6.1). It slides in like the other pages, with a back arrow, and Esc goes back.
- It shows the app's icon and name, "2.4.1 → 2.5.0" with the changed part in the accent color and, for a security fix, the Security pill after it (§6.2), the notes, and "Open release page" when the package has a release notes URL; the link stays with the notes shown, even after the update installs. The footer has the row's own button (Update, Install, Retry, Close & update or Update anyway; none while the row is busy, and none once its app is no longer tracked), which does what the row's does and returns to Updates.
- A check that brings a newer version while the page shows keeps it on that app. The newer version's notes show from the top, or, when winget has none, the page says "No release notes for 2.6.0.", with "Open release page" only if that version has a link.
- The notes keep their lines. A line starting with "-", "*", "+" or "•" is a bullet; one indented further than the item above is a level deeper, however many spaces it uses, and so is a numbered line; an indented line under a list item continues it, lined up with its text. A Markdown heading is a heading, in bold, and so is a line underlined with "===" or "---", and a line that isn't a bullet, numbered or a bullet's continuation, doesn't end with a punctuation mark and comes before a bullet, blank lines aside. A run of blank lines is one gap, and so is a rule ("---", "***" or "___") on its own. Markdown marks such as "##" (closing ones too) and "**" are removed, and "__" around two or more words, but not the underscores in names such as `__init__`. Addresses stay plain, selectable text: nothing in the notes opens or runs (§8). winget allows at most 10,000 characters.
- Tiny Tracker's own update has no page: its What's new opens its GitHub release page (§6.5).

## 5. Architecture

### 5.1 Processes and trust boundaries

```
┌─ User session, normal rights ──────────────────────────────────────┐
│ TinyTracker.exe  (WinUI 3)                                         │
│   tray icon · flyout · toasts · scheduler · hotkey                 │
│   ├─ Core    rules, scheduling, settings, history (no UI, tested)  │
│   └─ WinGet  COM client (list, metadata, upgrades), and with the   │
│              limit on, CLI executor + speed relay (§6.4)           │
└───────────────┬────────────────────────────────────────────────────┘
                │ named pipe, ACL: this user + Administrators only
┌───────────────▼─ Elevated (UAC per batch, or silent-mode task) ────┐
│ TinyTracker.Helper.exe  (no UI, stateless)                         │
│   accepts: upgrade(id, source, expectedVersion, limit), limit,     │
│   cancel, stay, enableProxyOption, selfUpdate(version, limit)      │
│   registerTask, removeTask (silent mode)                           │
│   upgrades through COM, or with a limit through its own CLI        │
│   executor and speed relay                                         │
└────────────────────────────────────────────────────────────────────┘
```

- The tray app never runs elevated. Launched elevated, it starts itself again unelevated, with the same arguments, and exits (§6.7). Only its maintenance switches, which open no window, run as they were started, such as the uninstaller's `--uninstall` (§10).
- The helper runs only while there is admin work. It holds no data and writes nothing to disk, except a self-update's Setup in the admin-only update folder (§6.5): it reports everything over the pipe, and the app logs it. It exits when the app hangs up, 30 s after starting if no app connects, or after 10 minutes with nothing to do (time asleep doesn't count): no update running and nothing asked. While admin updates wait their turn behind others, the app asks it to stay every 5 minutes. Its only command line is `--pipe <name> --user <SID>` (§8).
- **Single instance:** a second launch (from the Start menu, the installer's "Launch Tiny Tracker", a toast activation, or a start at sign-in) redirects to the running copy with its arguments. A second `--startup` changes nothing; anything else opens the flyout.

### 5.2 Repository layout

```
src/
  TinyTracker.App/              WinUI 3 tray app (views, tray, windowing, icons)
  TinyTracker.Presentation/     view models, words, strings (no UI framework)
  TinyTracker.Core/             models, rules, scheduler, stores, install queue, helper messages
  TinyTracker.WinGet/           COM adapter, CLI executor, throttling relay, error mapping,
                                helper pipe and task, closing apps in use
  TinyTracker.Helper/           elevated worker
tests/
  TinyTracker.Core.Tests/
  TinyTracker.WinGet.Tests/
  TinyTracker.Presentation.Tests/   view-model tests
  TinyTracker.DummyApp/             a small windowed app that the closing tests start and close
  TinyTracker.FakeWinGet/           a console app that acts like winget's command line for the speed-limit tests
installer/TinyTracker.iss       the installer; its license page and licenses folder are built with it
assets/icon/                    hamster.svg, hamster-small.svg, generated .ico/.png
assets/installer/               generated installer pictures
tools/IconGen/                  renders the generated icons and pictures
scripts/                        build-installer.ps1, test-install.ps1, scan-leftovers.ps1, check-resources.ps1
docs/                           design.md (this document), manual-test-checklist.md, releasing.md, images/
.github/workflows/              ci.yml, installer.yml (the shared install test), release.yml
.github/ISSUE_TEMPLATE/         the bug report and idea forms
README.md  SECURITY.md  CONTRIBUTING.md  LICENSE
.editorconfig  .gitattributes  .gitignore  Directory.Build.props  Directory.Packages.props  global.json  TinyTracker.slnx
```

### 5.3 Key dependencies (all versions pinned)
- .NET 10 SDK and runtime, self-contained.
- Windows App SDK 2.5.1's Foundation, InteractiveExperiences and WinUI packages, pinned; they move up only after a re-test. The whole SDK package isn't used: its machine learning, widgets and search parts would add 60 MB the app never loads.
- The Windows SDK projection 10.0.26100.87, on CsWinRT 2.3.1 as the Windows App SDK is; the .NET SDK's default one warns when trimmed (§13).
- `Microsoft.WindowsPackageManager.ComInterop` 1.29.x, matching the minimum supported winget.
- Tray icon: raw `Shell_NotifyIconW` (NOTIFYICON_VERSION_4) on a hidden window; no third-party library.
- Toasts: `Windows.UI.Notifications` with our own HKCU `AppUserModelId` key. `AppNotificationManager` can't register in self-contained apps (WindowsAppSDK #6774), and the toolkit fallback pulls a vulnerable `System.Drawing.Common`.
- Task Scheduler (silent mode) and Restart Manager (Close & update) through their Windows APIs, with no package; the same for the process, package-identity and TCP-table calls of the speed limit (§6.4). COM goes through source-generated interfaces, as trimming turns the runtime's own COM support off (§13).
- CommunityToolkit.Mvvm.
- xUnit for tests.
- Inno Setup 7.1, downloaded on the runner from its GitHub release and checked against its SHA-256.

### 5.4 Data (per user)
- `%APPDATA%\Tiny Tracker\settings.json` holds:
  - the settings
  - the tracked apps (id, source, the app's own Auto choice when it has one, skipped version). A file from before Update apps automatically keeps an app that had Auto on as its own choice, and the switch starts off, so nothing changes.
  - bookkeeping: first-seen dates, cached release dates, last auto attempt, phantom flags, tips already shown, whether the app turned on winget's proxy option (§6.4), a self-update under way (from, to, automatic), the versions of Tiny Tracker already announced (§6.5), and for new apps (§4.3) the winget ids that aren't offered: installed at the first check, tracked at any time, or turned down
- Release notes and security marks aren't stored: each check reads them again (§6.1).
- A backup file (§4.5) is the user's own: Tiny Tracker writes it only when asked and never removes it.
- `history.json` keeps the last 90 days.
- `logs\app.log` plus one rotated file, 1 MB each (2 MB cap).
- All writes are atomic: write to a temp file, then replace. A corrupt file is kept once as `.bak`, and defaults are used with a notice.
- All times are stored in UTC.

## 6. Behavior

### 6.1 Checking
- **Triggers:**
  - the schedule (every N hours)
  - 1 minute after sign-in
  - **Check now**
  - opening the flyout when data is more than 15 minutes old
  - the network returning after being offline, if a check was due
  - waking from sleep, or a clock change, if a check came due (it runs a minute later)
- **Gating:** scheduled checks skip while offline or while Energy saver (Battery saver before Windows 11 24H2) is on, and run when the condition clears. Both come from Windows events.
- **Each check:**
  1. List installed packages correlated with the winget catalog (COM). The msstore catalog isn't used: winget reports no available versions for Store apps, and every lookup there is a slow network call. Then look up by id any tracked app the list didn't match, because the full list misses apps that only a targeted lookup matches. Read installed and available versions. The same list finds new apps (§4.3): the first list only notes what's installed.
  2. Filter out:
     - installed version "Unknown" (shown as "Version unknown")
     - skipped versions
     - phantom versions (§6.2)
  3. For each candidate, collect:
     - publisher, via COM
     - release notes text and URL, via COM, in the same metadata call. They're kept in memory only.
     - release date. The COM API doesn't expose it, so it's read from the package's public manifest in `microsoft/winget-pkgs` on GitHub, cached per version. If none is found, the first-seen date is used.
  4. Load icons into memory from each app's Windows uninstall entry (DisplayIcon / install location).
- winget must be version 1.29.280 or newer (security fix). If it's older or missing, the flyout shows a banner with a Microsoft Store link to App Installer.

### 6.2 Decision rules (Core, fully unit-tested)
- **Manual action:** Update or Update all queues immediately, ignoring the wait, the install window and the game rules.
- **Which apps are on Auto:** while Update apps automatically is on (§4.5), every tracked app; while it's off, none. The exception is an app with its own choice from its "…" menu, which keeps it. Changing an app's Auto there gives it its own choice, and setting it back to the switch's value clears it; flipping the switch keeps every app's own choice. Apps added from Choose apps, a new-app notice or Restore follow the switch, except an app restored on Auto from a file made with the switch off, while this PC's switch is off (§4.5).
- **Auto on:** the app queues automatically when all of these hold:
  1. release age ≥ the wait setting (release date, else first-seen date), unless it's a security fix and Security fixes first is on
  2. no full-screen app or presentation is running, when "Pause during games" is on (`SHQueryUserNotificationState`)
  3. the PC is online, and the connection isn't metered
  4. Energy saver is off
  5. no auto attempt on this version in the last 12 h
  6. it installs without a UAC prompt: silent mode is on, or the update needs no admin (§6.3)
  7. with the install window on, the local time is inside it
- **Which reason shows:** a row names the first rule that holds it back, in this order: needs admin, the wait, the install window, the network, metered, Energy saver, the 12 h since the last attempt, full screen.
- **When the rules run:** after each check, when a condition that held an app back clears, when the install window opens, after each install, and when Auto is turned on, for one app or with the switch.
- **One at a time:** auto-updates enter the queue one by one, and the rules run again before each, so a game that starts midway holds the rest. Full-screen state has no Windows event: it's read when a rule needs it, and once a minute only while an auto-update or a toast waits for it to end.
- **After a failure:** an auto-install that failed for a reason that can pass (no network, a failed or stalled download, winget unavailable, another install running, one that took too long, the admin helper stopping or not starting, winget refusing the speed limit's proxy, which turns the limit off, or GitHub not answering a self-update, §6.5) is tried again once its 12 hours are over. Other failures, and failed updates the user started, wait for the user.
- **Security fixes:** an update is a security fix when a line of its release notes' text (§6.1) has a CVE number or a GitHub advisory id (GHSA-…), "vulnerability" or "vulnerabilities", or "security" next to fix, issue, update, patch, bug, flaw, hole or advisory (such as "fixed a security issue" or "Security issue:"), or when a line, after any bullet, number, quote or heading mark, starts with "Security:" or is only "Security", in bold or not. Other uses of "security", such as "security keys", "Windows Security" or "the security settings", don't count, nor do words that say there's none, such as "no security fixes", "isn't a security update" or "Security: none", and a package without notes text is never one. With Security fixes first on (§4.5), it gets the Security pill, is listed right after the rows in progress, and an Auto app skips rule 1; the other rules hold. A skipped version stays skipped, and toasts don't change.
- **Install window:** with it on (§4.5), Auto apps install by themselves only from its start hour up to its end hour, in local time, so 22:00 to 06:00 ends at 05:59. An install that runs when it closes finishes; the next waits for the next window. While an update waits for it, one timer is set for its start, and set again after a clock or time-zone change, a daylight-saving shift or waking from sleep. Clicks, checks and toasts don't wait for it. With the PC off or asleep through the whole window, nothing installs by itself until the next one.
- **Auto off:** the app gets one toast per new version.
- **Phantom detection:** if a successful upgrade leaves the installed version unchanged, and winget still offers the same version, that version is flagged. It isn't auto-installed again, and the row offers **Update anyway**.
  - App Installer (`Microsoft.AppInstaller`) is winget itself: Windows can't switch it to the new version while its programs run, and Tiny Tracker keeps winget running. So its row says to quit Tiny Tracker and open it again, with no Update anyway (§4.3); once winget's process has closed, Windows finishes the update and the next check finds it. Measured on a PC: 1.29.379 → 1.29.380 finished after Tiny Tracker restarted.

### 6.3 Install pipeline
- **One package at a time,** in a visible queue, because parallel installers conflict.
- **Routing,** from `GetApplicableInstaller()` Scope/ElevationRequirement and the installed scope:

| Case | Default mode | Silent mode |
|---|---|---|
| ElevationProhibited | App (unelevated) | App |
| Machine scope, or ElevationRequired | Helper, one UAC prompt per batch | Helper, no prompt |
| ElevatesSelf | App (the installer prompts itself) | Helper |
| Unknown, User scope | App | App |
| Unknown, scope unknown | App | Helper |

- The first row that fits decides, so an installer that elevates itself for an app installed for all users goes through the helper.
- **Install** always goes through the helper, and so does an update after winget answered "needs admin" (0x8A150019), which shows as Needs permission.
- **Batches:** a click that queues an update routed to the helper (such as Update, Install, Update all or a toast's Install) starts the helper at once if none is running, so the prompt appears at the click, not when the queue gets there. In silent mode the app runs the task instead. Everything queued while the helper runs uses it, in the same queue and in click order. Once nothing queued needs it, the app hangs up and the helper exits.
- **Progress** (COM `UpgradePackageAsync`, or with the speed limit on, winget's command line, §6.4): Queued → Downloading (bytes and %) → Installing (% if known) → Finished.
  - Speed is a rolling average over the last 3 s.
  - Cancel is allowed while Queued or Downloading.
- **App in use** (0x8A150101 or equivalent installer codes) is handled by **Close & update**, a queue item like any other, so the app stays open while others install:
  1. Find the app's install folder: its uninstall entry's install location or its MSIX package folder, else the folder of its icon unless that's inside Windows or ProgramData. Never used: a drive root; the Windows, temporary and installer-cache folders and anything inside them; ProgramData, Program Files, the Store's `WindowsApps` folders (in Program Files and at the root of any drive), AppData, the user folders and every other folder Windows names (such as Downloads) themselves; and any folder holding Tiny Tracker. With no folder, the row says "Close Example Editor, then try again" with **Retry**.
  2. At its turn ("Closing Example Editor…"), send this user's processes running from that folder, but not those of other apps installed inside it (such as a launcher's games), the close request installers send through Restart Manager: windowed apps get their normal close, with the chance to save, and tray apps the request Windows sends at shutdown. Wait up to 10 s.
  3. If any still run, the row says "Example Editor didn't close" with **Force close** and ✕ Cancel. Force close ends them. If they close meanwhile, the update goes ahead; with no answer in 2 minutes, it counts as Cancel. Processes Tiny Tracker can't end (elevated ones) give "Close Example Editor, then try again" with **Retry**, and so does an update that still finds the app in use after Close & update (another user's copy, or a service, may hold it).
  4. Update.
  5. After the update, whatever its result, or after a Cancel, reopen what was closed, as the user and unelevated, each with its own command line and working folder and the user's own environment, except processes that another closed process had started, programs that another running app started (that app starts them again), and console programs, which would run their commands again. Nothing already running again is started twice. An executable that's gone after the update is skipped and logged. An MSIX app reopens through its Start menu entry.
- **Reboot detection:** installer codes 3010/1641, or winget's reboot-required result, produce "Restart to finish". COM always reports `RebootRequired` as false, so it isn't used. With the speed limit on, winget's reboot-required results count, and so does its sentence that an installer needs a restart (§6.4).
- **After each package:** re-query it to confirm the new version, write a History entry, and update the row.
- **Default mode, auto-updates needing admin:** these don't prompt on their own. They show the Needs permission state and wait behind the "need your permission" toast and the row's **Install** button. Needing admin here also covers installers that elevate themselves, and apps whose installed scope and elevation requirement are both unknown.
- **An auto-install never shows a prompt.** When silent mode's no-prompt path fails (its task is missing, or winget doesn't answer the helper), auto-installs that need admin wait as Needs permission too. A click falls back as §6.6 says.

### 6.4 Download speed limit
- winget's COM API has no proxy option. The CLI supports `--proxy`, but only once the admin setting `ProxyCommandLineOptions` is on. It's off by default, only an administrator can turn it on, and winget keeps it per user. It unlocks nothing but `--proxy` and `--no-proxy`.
- **Turning the limit on:** the app reads `winget settings export`.
  - If the option is already on (the user or a policy turned it on), the limit is saved on with no prompt.
  - Otherwise the helper turns it on for this user (`enableProxyOption`): one UAC prompt, or none through silent mode's task. While the prompt shows, the note under the switch says "Waiting for permission…"; a declined prompt turns the switch back off with "Permission was declined".
  - The app then reads the export again, and saves the limit on only if the option now reads on. Otherwise the switch goes back off with "The speed limit couldn't be turned on".
  - The option stays on until uninstall, so turning the limit off, or on again, never prompts.
- **Only where it works:** on a standard account, the prompt would turn the option on for the administrator who approves it, not for the user, so the switch is greyed with "Needs an administrator account". A policy that turns off winget, its command line or its proxy option greys it with "Blocked by your organization's policy". So does one that turns off winget's settings, unless a policy turns the proxy option on: then the limit works on any account, with no prompt.
- **The box** takes whole numbers from 100 to 1,000,000 KB/s. A value outside snaps to the nearest end, and text that isn't a number goes back to the saved value. It saves on Enter or when it loses focus. The MB/s next to it uses the rows' units (1 MB = 1024 KB).
- **Limit on:** each update runs through winget's command line, in the process that runs it (§6.3): the app's own updates in the app, and admin updates in the helper, each with its own relay. After the COM check of §8, it runs:
  `winget upgrade --id <id> --exact --source winget --version <v> --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --proxy http://127.0.0.1:<port>`
  - Arguments are kept as a list and quoted by one tested routine, never pieced together by hand.
  - winget starts paused, and runs only once it's checked to be App Installer's own `winget.exe` (§8). Otherwise it's ended before it runs, and the update fails with "Can't reach winget".
  - The proxy target is a relay. It listens on 127.0.0.1 only, on a port Windows picks. It tunnels https to port 443 only, and never decrypts TLS. When a mirror sends the download to a plain http address, it forwards that request to port 80 only; winget checks the installer's hash either way, as it does without a proxy. It accepts only connections owned by the winget process it serves, and exists only while that update runs. One token bucket enforces the KB/s limit across all its connections.
  - Progress comes from winget's output, which has no progress bars when piped. The row says "Waiting…" until winget prints `Downloading <address>`. It's Downloading from that line once bytes move through the relay, or 0.25 s pass with no other line: winget prints an installer's help link the same way, right before saying the installer failed. It counts the bytes through the relay since then, out of the size a HEAD request on that address reports (unknown if the request fails or returns a web page). It's Installing from the next line winget prints, or once winget starts another program. A dependency repeats the cycle.
  - Cancel stops the relay, so winget's downloads and its reads of its sources fail, and it ends by itself before it installs anything more. One that hasn't ended 10 s after its download failed is ended, unless it runs an installer. An installer already running finishes, as with COM, and its outcome stands; otherwise the update is Cancelled.
  - The result comes from winget's exit code, read like COM's codes (§6.3, §7). An installer that finishes but needs a restart gets winget's success code, and a sentence in the Windows language says so: the app knows that sentence in each of winget's 11 languages, and shows "Restart to finish". It reads what winget prints for up to 1 s after it exits, as that sentence can come last.
- **Changing the limit** applies to the download running now within about 0.1 s: to the app's relay directly, and to the helper's through `limit`. Turning the limit off lifts the cap of a limited download at once. Turning it on during a COM download applies from the next update.
- **Limit off:** upgrades use COM (§6.3).
- **Kept honest:** at start, and when Settings opens, while the limit is on, the app reads the export. If the option is off, the limit is saved off, and Settings says "The speed limit was turned off because winget's proxy option is off". An update that finds `--proxy` refused (0x8A150002) fails with "The speed limit was turned off", with **Retry**, and turns the limit off the same way.
- The limit also applies to self-update downloads.
- Known gap, stated in the README: web installers that fetch more data themselves are not capped.

### 6.5 Self-update
- **Only an installed copy** updates itself: the app running from `C:\Program Files\Tiny Tracker`, installed by its Setup. A build started elsewhere never checks, and Settings greys the switch with "Works once Tiny Tracker is installed". The demo offers a pretend update that installs nothing (§12).
- **Checking:** at start, then once a day, and with Check now: one call to GitHub's latest release for `Bikuuuu/tiny-tracker`, which skips drafts and pre-releases. Only a version newer than the running one counts. When GitHub doesn't answer, a scheduled check tries again later, with no notice.
- **Update Tiny Tracker automatically on:** it installs by itself when the rules for Auto apps hold (§6.2): the release is at least the wait setting old (GitHub's release date), it's inside the install window when that's on, no game or presentation runs, the PC is online and the connection isn't metered, Energy saver is off, no attempt on this version in the last 12 h (clicked or automatic, cancelled or not), and it needs no prompt, which means silent mode is on. Afterwards the toast says "Tiny Tracker was updated to 0.2.0" (§4.7).
- **Otherwise,** with the switch off or silent mode off, a row at the top of Updates (§4.3) and a toast once per version (§4.7) say the update is ready. **Update** installs it, with one prompt when silent mode is off.
- Its release notes aren't read, so it's never a security fix (§6.2), and its What's new opens the release page.
- **Never alongside app updates:** it isn't counted in Update all, it starts only when no other install runs ("Waits for the other updates"), and installs queued meanwhile wait until it's done, and after a restart on the old version until its Setup has finished.
- **Installing,** by the helper, started with the prompt or silent mode's task:
  1. The app saves a note in `settings.json`, "updating 0.1.0 → 0.2.0" and whether it's automatic, then sends `selfUpdate(version, limit)`: the version alone.
  2. The helper looks for Tiny Tracker running under another account. Its files would be in use, so the update stops with "Close Tiny Tracker for the other accounts on this PC, then try again", with **Retry**. The user's own copy is left to Restart Manager.
  3. It asks GitHub's API for that release itself, and requires it to be published and immutable, with one asset named `TinyTracker-Setup-<version>-x64.exe` that has a SHA-256 digest and is smaller than 200 MB.
  4. It empties `update\` inside the Program Files folder, which only admins can write, and downloads the asset there from GitHub's own hosts over TLS, at the speed limit when it's on, even when it's turned on or off during the download (§6.4), hashing it as it comes. A download whose SHA-256 doesn't match the digest is deleted and never run, and the row says "The download didn't match GitHub's record".
  5. It keeps the file open with writes denied, starts it with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`, tells the app, and exits, so that Setup can replace its files. The app marks its note "Setup started", with the time, and the row says "Installing… Tiny Tracker will restart". Setup holds a mutex while it runs. If it doesn't hold it 2 minutes after the helper started it and that Setup has stopped, the update counts as failed; a Setup still running, such as one a first virus scan slows, is waited for, up to an hour after it started.
  6. Setup's Restart Manager closes the app the way Quit does (§6.7), installs, and starts it again. A Setup that fails or rolls back starts the app again too, so Tiny Tracker keeps running either way.
  7. Setup marks its own copy for deletion at the next restart. The helper empties the folder before any later update, and the uninstaller removes it (§10).
- **Cancel** (✕) works while it waits or downloads: the helper deletes what it has and exits. Once Setup has started, there's nothing to cancel. Quit during the download cancels it.
- **An unexpected error** ends the attempt as failed, like the others: it's logged, goes in History, and the row offers **Retry**.
- **After the restart,** the app reads the note. Running the new version, it writes a History entry, "Tiny Tracker · 0.1.0 → 0.2.0", and for an automatic update shows the toast, once, even when the note can't be cleared. A note that Setup never started, such as after a shutdown during the download, goes with nothing to report, unless Setup still runs. Still on the old version once Setup has finished, or an hour after Setup started (a start dated after now counts as now), it counts as failed: a History entry, and "Couldn't update Tiny Tracker" with **Retry**. Either way the note is cleared. As for Auto apps (§6.2), a failed automatic update tries again after 12 h when its reason can pass, such as GitHub not answering; other failures wait for the user, and a toast says so once (§4.7). That holds across restarts: no automatic try and no second toast for that version until the user clicks Update, or a newer version comes out.
- Repo setting: **immutable releases** enabled.

### 6.6 Silent mode
- **Turning it on** starts the helper with one UAC prompt, and the helper registers this user's task. The switch is saved on once the task exists. While the prompt shows, the note under the switch says "Waiting for permission…"; a declined prompt turns it back off with "Permission was declined". The task:
  - is "Tiny Tracker Helper (<user SID>)" in a `\Tiny Tracker\` folder, one per user, so two admins on one PC keep their own
  - runs `…\TinyTracker.Helper.exe --pipe $(Arg0) --user <SID>`; each run gets a new pipe name as its argument
  - has RunLevel Highest and runs only when the user is logged on
  - has no triggers; the app starts it on demand through the Task Scheduler API. Runs may overlap; it keeps running on battery and stops after 24 h at most
  - can be read and run by the user; only Administrators and SYSTEM can change or delete it
- **Only where it's safe and works:**
  - The helper registers the task only when it runs from Program Files: a no-prompt elevated task that points at a file the user can overwrite would give any program admin rights. Elsewhere the switch is greyed with "Works once Tiny Tracker is installed".
  - On a standard account, "highest privileges" isn't admin, so the switch is greyed with "Needs an administrator account".
- **Turning it off** needs no prompt: the app runs the task once more, and the helper deletes its own task (`removeTask`). If that fails, one UAC prompt does it. Uninstalling deletes every user's task.
- **Kept honest:** at start and when Settings opens, the app checks that this user's task exists and runs this copy's helper. If not, silent mode counts as off and is saved off, and Settings says "Silent mode was turned off because its task is missing". A task that can't be started at a click is treated the same way, and that click uses the UAC prompt instead. Silent mode left on where it can't work, such as on an account that's no longer an administrator's, is saved off too, with no note: the greyed switch says why.
- **Why it's safe:** the helper validates every request (§8). The worst a hostile local process could do by triggering it is update apps that are already installed.
- **Fallback:** if elevated winget is unavailable, for example with Windows *Administrator Protection* enabled, updates the user starts fall back to unelevated runs with per-installer prompts, and auto-installs that need admin wait (§6.3). A one-time tip on Updates explains it: "Admin updates will ask for permission one by one, because winget doesn't answer the admin helper."

### 6.7 Startup, single instance, shortcut
- **Start with Windows:** a per-user `HKCU\…\Run` entry pointing to `TinyTracker.exe --startup`. The first check runs 1 minute later.
  - The switch shows on only while that entry starts this copy and Task Manager's Startup apps hasn't turned it off. Turning it on in Settings clears Task Manager's off mark.
  - `TinyTracker.exe --cleanup` removes this user's entry and its mark (only when they start this copy), the toast registration and the tray icon's entry; with `--remove-data`, also the settings, history and logs.
- **The installer's last page** runs the app as the user who ran the installer, unelevated:
  - "Start Tiny Tracker with Windows" runs `TinyTracker.exe --start-with-windows`, which turns the entry on. It shows on fresh installs only, so an upgrade never changes the user's choice. Setup running as SYSTEM, as managed installs do, skips it: SYSTEM's entry would start the app for no one.
  - "Launch Tiny Tracker" starts the app. On the first run, the flyout opens on Choose apps.
- **Maintenance switches** (`--start-with-windows`, `--cleanup`, `--uninstall`) open no window, do their work and exit, elevated or not.
- **Started elevated,** the app starts itself again unelevated through the Windows shell, with the same arguments, and exits. Where the shell can't do that, the Explorer that owns the taskbar starts it, without arguments, called by its full path. With no such Explorer, it doesn't start, since an Explorer it started would be an elevated shell. The copy the shell starts runs as it is, so a shell that's elevated itself can't make it loop.
- **A second start** hands its arguments to the running copy (§5.1). The Run entry and Windows' restart can both start the app at sign-in, so a second `--startup` changes nothing.
- **Restart:** at start, the app registers with Windows' restart feature (`RegisterApplicationRestart`) with `--startup`. Windows then offers to restart it after a crash, once it has run for 60 s; an installer that closed it can start it again; and with Windows' setting to restart apps after sign-in, it comes back quietly. The demo doesn't register.
- **Closing for an installer:** when Restart Manager asks the app to close (`ENDSESSION_CLOSEAPP`), it quits the way Quit does, even while it's still starting.
- **Focus:** the flyout takes the focus when it opens, even when another program started the app (such as the installer's "Launch Tiny Tracker"), with the steps it uses after a UAC prompt, so it closes when the user clicks elsewhere.
- **Shortcut:** `RegisterHotKey` on the tray icon's hidden window. While Settings records a new shortcut, the current one is paused.

## 7. Error handling

Every error shows in plain words, with a **Details** button that reveals the code (HRESULT or installer exit code).

| Condition | Behavior |
|---|---|
| winget missing or older than 1.29.280 | Banner "winget needs an update" with a Store button |
| COM server not responding | Retry after 1, 5 and 15 min; status "Can't reach winget right now, retrying" |
| Offline | Checks skip quietly; they run when the network returns |
| Download stalled (no progress for 2 min) | Cancel, retry once, then "Download stalled" |
| Still queued in winget after 2 min | "Waiting for another install to finish…"; not cancelled; the 30-minute cap applies |
| Another install in progress (0x8A150102 / MSI 1618) | Retry up to 3× at 2 min intervals |
| Waiting or installing over 30 min (time spent downloading doesn't count) | Move on; mark "Took too long" |
| App in use | Close & update (§6.3) |
| App doesn't close within 10 s | "Example Editor didn't close" with Force close and Cancel (§6.3) |
| Restart required | "Restart to finish" plus a toast |
| UAC declined | The items waiting on the helper go back with "Permission was declined", keeping their Update or Install. No re-prompt until the user acts |
| Elevated winget unavailable | Unelevated fallback (§6.6) |
| Package uninstalled / no longer in winget | "Not installed anymore" / "Not found in winget", with Stop tracking. An uninstalled app stops being tracked by itself a day later (§4.3) |
| Helper doesn't start (missing, the wrong process on the pipe, or no answer in 30 s) | Its items fail with "The admin helper didn't start", with **Retry** |
| Helper crash or pipe loss | The running item and those waiting on the helper fail with "The admin helper stopped", with **Retry**. The running one is still read again, so an update that landed shows as done. Nothing stays stuck |
| Silent mode's task missing | Silent mode counts as off (§6.6): a click uses the UAC prompt, and auto-installs wait |
| winget refuses `--proxy`, because its proxy option was turned off | The update fails with "The speed limit was turned off", with **Retry**; the limit is saved off, and Settings says why (§6.4) |
| The started winget isn't App Installer's own (§6.4) | Ended before it runs; "Can't reach winget", with **Retry** |
| GitHub unreachable (self-update) | A scheduled check tries again later with no notice; after a click, "Couldn't reach GitHub", with **Retry** (§6.5) |
| A self-update's file doesn't match its release | Deleted and never run; "The download didn't match GitHub's record", with **Retry** |
| Tiny Tracker runs under another account | Its self-update waits: "Close Tiny Tracker for the other accounts on this PC, then try again", with **Retry** |
| A self-update's Setup fails | The app starts again on its old version; "Couldn't update Tiny Tracker", with **Retry** |
| App crash | Windows offers to restart it (`RegisterApplicationRestart`, §6.7) |
| Corrupt settings | `.bak` kept once, defaults loaded, notice shown |

## 8. Security and privacy

- **Least privilege:** the UI is never elevated. The helper is elevated only while it has work, and it is stateless: it writes nothing to disk except a self-update's Setup, in the admin-only update folder (§6.5). The app's exe runs elevated only for its maintenance switches (§6.7), such as the uninstaller's `--uninstall` (§10): from Program Files, with fixed arguments and no window. It never changes a user's files or settings elevated: the uninstaller hands those to `--cleanup`, run as that user, since a folder a user controls can be made to point elsewhere.
- **Helper input validation:**
  - It accepts only its one command line and the listed request types. Messages are length-prefixed JSON of at most 16 KB; a malformed, oversized or unknown one ends the connection, and the helper exits.
  - For `upgrade`, the id and version must match winget's grammar and the source must be `winget`. It re-queries the package by id and source, and checks that it's installed and that winget offers exactly that version as an update, newer than the installed one. It never accepts installer arguments, overrides or paths.
  - The limit, in an `upgrade` or a `limit` message, must be 0 (no limit) or 100 to 1,000,000 KB/s. An `upgrade` with another value is refused, and a `limit` message with one changes nothing.
  - For `enableProxyOption`, it runs only `winget settings --enable ProxyCommandLineOptions`, then checks the result with `winget settings export`.
  - For `registerTask`, it checks that it runs from Program Files (§6.6).
  - For `selfUpdate`, it runs only from `C:\Program Files\Tiny Tracker`, where Setup installs, and takes a version alone, which must match SemVer's grammar and be newer than the installed app. It asks GitHub's API for that release itself, downloads only from GitHub's hosts, straight into the admin-only update folder, and runs the file only when the release is immutable and the file's SHA-256 matches its digest. It never accepts a path or an address (§6.5).
- **Pipe:** the helper creates it with a new random name each start, as its first and only instance, for one client; a program that took the name first makes the helper refuse to start. Its DACL grants only the app's user and Administrators, and network clients are refused. Before sending anything, the app checks that the pipe's server is `TinyTracker.Helper.exe` from its own folder, running elevated. The helper validates messages regardless of who sent them, and a client that stops reading can't make it hold messages without end: newer progress replaces older, and the helper exits once 32 messages wait unread.
- **Tamper resistance:** binaries live in Program Files, which only admins can write. Silent mode's task is registered only from there.
- **No command injection:** processes start with argument lists, and package ids are validated against winget's id grammar.
- **winget's command line:** the `winget.exe` in the user's folder is an alias that any program the user runs could replace. So the app and the helper start winget paused, and let it run only if its exe is `winget.exe` inside App Installer's package folder under `Program Files\WindowsApps`, and the process carries App Installer's package identity. Anything else is ended before it runs. This keeps the helper, above all in silent mode, from running another program elevated.
- **Relay:** listens on loopback only, allows CONNECT to 443 only and a plain http GET or HEAD to port 80 only, which it forwards without the proxy's headers, accepts only connections owned by the winget process it serves, and exists only while a limited download runs.
- **winget's proxy option** only lets winget accept `--proxy` and `--no-proxy`. It sets no proxy and changes nothing else. On a PC whose administrator set a default proxy for winget, `--no-proxy` could skip it, which is why winget keeps the option admin-only. A policy can block it, and the switch is then greyed (§6.4).
- **Release notes** come from the packages' winget manifests and show as plain text: nothing in them opens or runs. "Open release page" opens only http and https addresses.
- **Restored lists** (§4.5) are data: only winget ids, names, Auto and the switch are read, ids must match winget's grammar, names are at most 256 characters with no control characters, and only apps that are installed are added; a file over 1 MB or in any other shape is refused.
- **Closing apps:** graceful first; force-close only after explicit consent. Only this user's processes running from the app's own install folder are closed, never from the folders §6.3 rules out, and they reopen unelevated with their own command lines.
- **Privacy:**
  - No telemetry, no accounts, no ads.
  - Network traffic goes only to the winget sources and installer hosts (as winget itself does) and to GitHub (release dates, self-update).
  - No personal data leaves the PC.
- **Repo hygiene:**
  - CodeQL (C# and the workflows), Dependabot alerts and security updates, secret scanning with push protection, and immutable releases are on. Weekly Dependabot version updates start at 1.0.
  - main takes changes only through pull requests whose CI passed, and never a force push. Outside contributors' CI runs wait for approval.
  - Actions are pinned by SHA.

## 9. Resource budgets

These are measured with `scripts/check-resources.ps1` before each release, on the installed copy (or the one at `-Path`) with the flyout closed. After a quiet minute, it samples CPU, memory (Task Manager's figure), GPU and Efficiency mode every 15 s for 30 minutes. The CPU average and every memory and GPU sample must be within budget, with Efficiency mode on at every sample. A check or an install during those 30 minutes makes the run inconclusive, and it's run again. A release is blocked if a budget is missed.

| Condition | Budget | Technique |
|---|---|---|
| Idle, flyout closed | CPU ≈ 0% (< 0.1% average over 30 min) | Event-driven: one timer for the next check, and one for the install window's start while an update waits for it. The only polling is a 1-minute full-screen probe while something waits for a game to end |
| Idle, flyout closed | RAM < 40 MB in Task Manager | Working set trimmed after the flyout hides |
| Idle, flyout closed | GPU 0% | Cloaked window, no running animations |
| Idle | Efficiency mode (EcoQoS) on | `SetProcessInformation(ProcessPowerThrottling)`; turned off while the flyout is open or work runs |
| Check | A few seconds of CPU every N hours | Mostly winget's own work |
| Flyout open | Smooth 60 fps motion | Composition animations only, and one resize per height change (§4.2) |

## 10. Disk footprint and cleanup

| Item | Location | Size or lifetime |
|---|---|---|
| App binaries (self-contained, trimmed) | `C:\Program Files\Tiny Tracker\` | about 80 MB |
| Settings, history | `%APPDATA%\Tiny Tracker\` | KB |
| Logs | same folder | ≤ 2 MB |
| Icons | memory only | none on disk |
| Downloaded installers | winget's own temp folder, deleted by winget; the app keeps none | transient |
| Self-update file | `update\` inside the Program Files folder (admin-only). Setup marks its own copy for deletion at the next restart, the helper empties the folder before each update, and the uninstaller removes it (§6.5) | about 25 MB, until the next restart |

- **Registry and system entries:**
  - the uninstall entry and the Start menu entry
  - the HKCU Run value (if Start with Windows is on), and the mark Task Manager keeps for it under `Explorer\StartupApproved\Run`
  - the toast registration (its AUMID key; no COM activator)
  - the entry Windows keeps for the tray icon under `HKCU\Control Panel\NotifyIconSettings`
  - the silent-mode task, one per user in the `\Tiny Tracker\` folder (only while that user's silent mode is on)
  - winget's `ProxyCommandLineOptions`, per user, and only if the app turned it on: from the first time that user turned the speed limit on, until uninstall (§6.4)
- **Uninstall** (Settings > Apps): after Windows' prompt and Inno's own confirmation, it asks "Also remove your settings and history?" (default Yes; silent uninstalls remove them). Before any file goes, it runs `TinyTracker.exe --uninstall` as administrator, for what takes one:
  - it closes Tiny Tracker the way Quit does, ends it if it still runs 10 s later, and waits for its helper
  - it removes every account's silent-mode task and the `\Tiny Tracker\` folder
  - it turns `ProxyCommandLineOptions` off again for the account it runs as, if that account's settings say the app turned it on. The app never turns it on for a standard account (§6.4).
  - it then starts `TinyTracker.exe --cleanup` as the account signed in at the PC, unelevated, through the Windows shell, and waits up to a minute for it to finish. Once the shell has started it, none of its work runs elevated too. With that account's own rights, even when another administrator approved the prompt, it removes the account's Run value and Task Manager's mark (when they start this copy), its toast registration and its tray icon entry, and with Yes its settings, history and logs.
  - with no Windows shell (a remote or scripted uninstall), it removes those registry items and that settings folder for the account it runs as; a link, the folder itself or one inside it, goes as a link, and what it points to stays, even when a folder is swapped for a link while it runs
  - each step runs even when another fails
  - it says which of the two cleanups ran, which the install test checks (§12)

  Inno then removes the program files, the self-update folder, the Start menu entry and the uninstall entry. A file that another account's running copy still holds goes at the next restart.
- **Other accounts** that used Tiny Tracker keep their Run value (which then starts nothing), toast registration, tray icon entry, settings and history, and winget's proxy option if they turned the limit on: winget keeps it per user. The README says so.
- **Outside our control, stated in the README:** winget's own logs, leftovers created by third-party installers, Windows' own records of programs that ran (such as crash reports and the Start menu's tile cache), and the uninstaller's copy in the temp folder, which the next Inno Setup uninstaller removes.

## 11. Releases and distribution

- **Versioning:** SemVer, starting at 1.0.0. A `vX.Y.Z` tag, plain numbers only, triggers `release.yml`. Builds in between carry 0.1.0.
- **CI** (`ci.yml`, runner `windows-2025`):
  - on push and PR: restore → build (Release, x64) → unit tests; and the winget job: read-only integration tests, then real upgrades, the elevated helper and the speed limit on the runner's own apps
  - on pull requests to main: the install test (§12), defined once in `installer.yml` and shared with `release.yml`
- **Release** (`release.yml`):
  1. build, test, and `dotnet publish` self-contained and trimmed win-x64 with the tag's version. The helper runs on the app's copy of .NET, so the trimmer keeps what the helper uses too
  2. download Inno Setup 7.1 from its GitHub release, check its SHA-256, install it on the runner and compile `TinyTracker-Setup-<ver>-x64.exe`
  3. install tests on the runner (§12)
  4. once the owner approves it in the protected `release` environment, a GitHub Release named "Tiny Tracker <ver>" with `Setup.exe` under two names, `TinyTracker-Setup-<ver>-x64.exe` for the self-update and `TinyTracker-Setup-x64.exe`, so `releases/latest/download/TinyTracker-Setup-x64.exe` always downloads the newest: created as a draft, then published and marked latest, with notes listing the merged pull requests. A job of its own then checks that GitHub made it immutable and verifies both files, so a rerun only checks.

  A manual run does steps 1–3 with a given version and keeps `Setup.exe` with the run for 7 days; it publishes nothing. Only step 4 may write to the repository.
- **Installer** (Inno Setup, `installer/TinyTracker.iss`):
  - per-machine (`PrivilegesRequired=admin`), always into `C:\Program Files\Tiny Tracker`: there's no folder page, upgrades never move it, and it refuses another folder given with `/DIR`, since the helper must stay where only administrators can write (§8)
  - x64 Windows 11, version 22H2 or later, as the app itself requires: older Windows and Arm64 PCs get a message, and nothing installs
  - pages: Windows' UAC prompt; a license page to accept, with Tiny Tracker's MIT license and then the Microsoft terms of the Windows App SDK it includes; progress; and a last page with "Start Tiny Tracker with Windows" (fresh installs only) and "Launch Tiny Tracker", both ticked (§6.7). No Start menu folder page and no desktop icon.
  - after Windows' prompt, the wizard comes to the front: for its first 3 s it stays above the other windows and takes the foreground, as the flyout does after a prompt (§6.7), since Windows opens it behind the window used before
  - look: Inno's Windows 11 style, light or dark as Windows is, with the hamster's icon and pictures. English only.
  - it adds the program files, one Start menu entry, the uninstall entry (publisher "Bikuuuu") and a `licenses` folder with the license and notice files of everything it includes, taken from their packages at build time
  - upgrades: it asks a running copy to close through Restart Manager (§6.7), replaces the files, keeps Start with Windows as it was, and starts the app again. A self-update's Setup that fails or rolls back starts the app again too (§6.5).
  - silent installs (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`), so winget-pkgs can list it: the same, with no pages. A fresh one turns Start with Windows on, except as SYSTEM (§6.7), and none launches the app.
  - the uninstaller performs the cleanup in §10
- **Signing:** none (§3). The README explains the SmartScreen "More info → Run anyway" step, and that Smart App Control blocks unsigned apps. Each release is also sent to Microsoft's file submission portal.
- **README:**
  - a banner that links to the download, and badges
  - what it does and what it doesn't, with screenshots of the demo
  - install steps, the SmartScreen note, and checking a download (its SHA-256 on the release, `gh release verify-asset`)
  - starting it again after Quit (the Start menu), and the hidden-icons tip
  - features and privacy
  - uninstall: what goes, what stays for other accounts, and what's outside our control (§10)
  - with the speed limit, its first admin prompt and the web installers it doesn't cap (§6.4)
  - building from source
  - license, and the bundled parts' licenses
  - support on Ko-fi, and where downloads are genuine
- **After 1.0:** submit to `winget-pkgs` with wingetcreate, then automate later versions with winget-releaser.

## 12. Testing

| Layer | Scope | Where |
|---|---|---|
| Unit (the bulk, test-first) | Version compare and diff, decision rules, the security rule on real release notes, the install window, the notification levels, new apps, the backup file's rules, scheduler, sleep and resume, settings load/save/recovery, history retention, error-code mapping, token bucket accuracy, self-update's rules and start-up note, the helper's release checks and its download checked against a local fake GitHub, helper request validation, routing and batches, Close & update's steps, the uninstall's steps, a second start's arguments, and when large text moves a control onto its own line | CI, every push |
| View-model | State → texts, buttons and progress for every row state in §4.3, the new-app notices, Back up and Restore, the What's new page and its notes, and the toasts at each notification level | CI, every push |
| winget integration | Read-only listing and metadata against real winget | Locally; on CI if winget exists |
| Helper and closing | The pipe's permissions and message rules, the real helper exe (in demo mode only in Debug, so locally), closing and reopening the dummy windowed app | CI, every push, and locally |
| Speed limit | The command-line path against the fake winget (stages, speed at the limit, live changes, cancel, size, exit codes), the relay refusing other processes, the winget check refusing a stand-in exe; read-only: the checked real winget, `settings export`, and `--proxy` refused while the option is off | CI, every push, and locally |
| Elevated | Real upgrades through the helper; silent mode's task registered from a Program Files copy, started with no prompt, and removed (runners can't start it with an unelevated app's rights, so that's a manual check); the helper turning winget's proxy option on; limited upgrades in the app and through the helper, measured against the limit, and a cancel during one | GitHub-hosted runner, every push |
| Install tests | Snapshot → Setup refusing another folder (`/DIR`) → silent install → the installed app's `--self-check` → `--start-with-windows` as SYSTEM, which adds nothing → a silent upgrade over the running app, which closes and comes back → the installed helper: a real upgrade, silent mode's task from Program Files on and off, winget's proxy option and a limited upgrade → silent uninstall, which leaves nothing, the proxy option included, and cleans up through the Windows shell → no crash in Windows' event log, and every copy it closed exited with 0 → leftover scan (file, registry and task snapshot diff; Windows' own records of a program that ran are listed, not counted). Then self-update (§6.5), in fresh installs of the unmodified app with a stand-in GitHub that only that runner trusts (its hosts entries and certificate, removed right after): the previous release, and this code built with a lower version, each update themselves to this build with silent mode and Auto on; a release whose digest doesn't match installs nothing | GitHub-hosted runner, each pull request to main and each release |
| GitHub, read-only | The real latest release: its asset, SHA-256 digest, immutable flag and download address; skipped until the first release | CI, every push |
| Resource check | §9 budgets | A real PC, before each release |
| Manual checklist | `docs/manual-test-checklist.md`: the installer's pages, Launch and Start with Windows, real UAC prompt, UAC declined, silent mode's task started by the installed app with no prompt, dark/light mode and the tray menu in both, accent colors, 100/125/150/200% scaling, Text size at 200% and 225%, two monitors, keyboard-only, two contrast themes, game deferral, metered connection, Energy saver, the What's new page, the install window starting on time, the notification levels, new apps, Back up and Restore, self-update from the previous version, uninstall and a leftover scan, and the release steps | A real PC, before each release |

The runner limitations (Windows Server, no UAC prompt, no real desktop visuals) are covered by the manual checklist.

The Debug-only demo (`--demo`) runs made-up apps; its code and its tests build only in Debug, so they run locally, not on CI. Its helper is the real exe with the real UAC prompt. Started with `--demo`, which only Debug builds know, it fakes winget inside, so nothing installs. The demo's silent mode starts that helper unelevated and registers no task. The demo's speed limit asks the same helper to turn winget's proxy option on, which it fakes, and its made-up downloads run at the limit. The demo also offers a pretend Tiny Tracker update, which installs nothing, and its first check offers Proseware Draw as a new app. Its made-up apps have release notes, one with a security fix, one with only a link and one long, and one Auto app waits for the install window.

## 13. Platform findings

Measured with Windows App SDK 2.5.1 and winget 1.29.380:
- WinUI 3 windows refuse `WS_EX_TOPMOST`, through the presenter and through `SetWindowPos`, from inside the process or outside it (microsoft-ui-xaml#9990). So the flyout isn't always-on-top; it relies on the foreground rights that a tray click, the shortcut and a Start menu launch give it.
- `AppNotificationManager.Register` fails in a self-contained app and leaves registry keys it can't remove, so toasts use `Windows.UI.Notifications` (§5.3). Toast buttons work only while the process that showed them runs, so the app clears its toasts when it quits.
- winget's COM vectors don't support `foreach`: they're read by index. `GetApplicableInstaller()` gives a fresh install's scope, so updates use the installed scope.
- winget 1.11, preinstalled on GitHub's runners, throws `InvalidCastException` on `PackageManager.Version`; the app reads that as "winget needs an update" (§7), and every workflow updates winget first.
- Through the relay, a 2000 KB/s limit measured 1982 KB/s over 60.8 MB on a runner.
- A minimal WinUI 3 tray app idles at 0.006% CPU, 8.5 MB and 0% GPU with EcoQoS and its working set trimmed, and at 65 MB without that trim.
- Under the .NET 10 SDK, xunit.v3 runs through Microsoft.Testing.Platform (`global.json`), and `workflow_dispatch` works only once a workflow is on the default branch.
- Windows shows a WinUI window's new size a frame before WinUI draws at that size. A window that grows shows its new part black for that frame, also with acrylic that Windows draws itself (`DWMSBT_TRANSIENTWINDOW`) while the window is active, and one that shrinks now and then shows a dark frame at the bottom too. A resize on every frame, to glide, repeats that on every frame, and the footer floats by one frame's motion (10–20 px at 120 Hz). So the flyout resizes once per change (§4.2).
- Trimmed (`TrimMode=partial`), the published app goes from 308 files and 169 MB to 166 files and 81 MB, and Setup from 45 MB to about 22 MB. WinUI's native part, about 45 MB, doesn't trim.
- Trimming turns the runtime's own COM support off, and turning it back on brings a warning from the runtime itself (IL2026 in `ComActivator`), so COM goes through source-generated interfaces (§5.3). The .NET SDK's default Windows projection (10.0.26100.57, CsWinRT 2.2) warns 35 times (IL2081) in its generic fallbacks when trimmed; 10.0.26100.87, on CsWinRT 2.3.1, doesn't, and its analyzer wants arrays or lists where a collection expression makes a read-only list (CsWinRT1032).
- Trimmed, Tiny Tracker idled for 30 minutes with the flyout closed at 0.010% CPU on average, at most 7.8 MB and 0% GPU, with Efficiency mode on at every sample (§9).

## 14. Roadmap

- **Before 1.0:** release notes in the flyout, security fixes first, the install window and notification levels; then offering newly installed apps, and backing up and restoring the list; then fixes carried from earlier reviews; then polish: motion, keyboard use, high contrast and large text, the resource budgets (§9), and a trimmed install.
- **1.0:** then a listing in winget.
