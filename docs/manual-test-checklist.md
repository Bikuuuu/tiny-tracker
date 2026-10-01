# Manual test checklist

Run it on a real Windows 11 PC before each release, with a Setup.exe of that version, and its last part once the release is out (see [releasing.md](releasing.md)). The runner covers the rest (see the design's §12). Take a leftover snapshot first, from an admin PowerShell: `scripts\scan-leftovers.ps1 -Record $env:TEMP\before.json`. It lists what's on this PC, so keep it out of the repository.

## Install

- [ ] SmartScreen's "More info → Run anyway" works, and Windows' prompt names the Setup file.
- [ ] After Windows' prompt, Setup opens in front of the window you started it from.
- [ ] The license page shows the MIT license, then the Windows App SDK's terms, and Install stays off until you accept.
- [ ] The last page offers "Start Tiny Tracker with Windows" and "Launch Tiny Tracker", both ticked.
- [ ] Launch opens the flyout on Choose apps, with the focus: a click elsewhere closes it.
- [ ] Back on Updates after Choose apps, the hidden-icons tip shows once, and its ✕ closes it for good.
- [ ] The Start menu has one "Tiny Tracker" entry, which starts it after Quit.
- [ ] After a restart, Tiny Tracker starts with Windows, with no flyout.
- [ ] Setup run again over the running app closes it, updates it and starts it again; Start with Windows stays as it was.
- [ ] The Start menu entry's Run as administrator starts it unelevated: Task Manager's Details tab shows Elevated: No.

## Admin rights

- [ ] An update that needs admin shows Windows' real prompt. Yes installs it.
- [ ] No at the prompt shows "Permission was declined".
- [ ] Turning silent mode on takes one prompt; then an admin update installs with none.
- [ ] Turning silent mode off takes no prompt.

## Looks and input

- [ ] Light and dark mode, and a change of accent color, while the flyout is open. The tray icon's right-click menu follows each mode.
- [ ] 100%, 125%, 150% and 200% scaling.
- [ ] Text size (Settings > Accessibility > Text size) at 200% and 225%: every page reads in full, with nothing cut off or overlapping.
- [ ] Two monitors, with the taskbar on each.
- [ ] Keyboard only: the shortcut, Tab, arrows, Enter and Esc. The flyout opens with the focus on **Check now**, each page on its first control, and **Back** returns it to the button that opened the page, with no tooltip left floating.
- [ ] After **Track** or **No thanks**, the focus moves to the next notice's **Track**, else to **Check now**; after **Show older** in History, to the first entry it added.
- [ ] In Settings, record a shortcut another running app already uses: "Shortcut in use", and the old shortcut still opens the flyout.
- [ ] Two contrast themes, such as Aquatic and Desert: every page reads, and the Security and Auto pills look different.
- [ ] Settings' footer reads "v<version> · GitHub · MIT License" and ends with a heart that says "Support Tiny Tracker" on hover, and each opens its page in the browser: the repository, the license and the Ko-fi page.

## Release notes and notifications

- [ ] What's new on an app with release notes opens its page, with its headings and bullets, **Open release page** and the row's own button. Tab stops on the notes once, and a selection can run across their lines. On an app with only a link, it opens the browser.
- [ ] With an Auto app that has release notes waiting for the install window, open its What's new just before the window opens: the button goes while it installs, and then the notes and **Open release page** stay, with no button.
- [ ] With **Show What's new links** off, the rows show no What's new, and the "…" menu still has it.
- [ ] An update whose notes mention a security fix shows the Security pill and comes first.
- [ ] Notification levels: with **When I need to act**, an update that installs shows no toast; with **Only failures**, only a failed one does; **Off** shows none.

## Your app list

- [ ] Install two apps winget can update, then click **Check now**: each gets a "New app: <name>" notice, in name order, with **Track** and **No thanks**.
- [ ] **Track** one: its notice goes, and its row shows, on Auto only while **Update apps automatically** is on. **No thanks** the other: it isn't offered again, even after a restart.
- [ ] With four or more such apps new, **Check now** shows three "New app" notices, in name order and above any tip; **No thanks** on one brings the fourth.
- [ ] Turn on **Update apps automatically** in Settings: every row gets the Auto pill. Turn Auto off for one app in its "…" menu, then turn the switch off and on again: that app stays off. Turn the switch off again.
- [ ] In Choose apps, search for part of a name and click **Select all**: only the apps shown get ticked, and the link reads **Deselect all**. Click it: "Stop tracking N apps?" opens under it. Esc keeps the ticks, and **Stop tracking** unticks only those apps, with the focus still on the link.
- [ ] Under **Updated elsewhere**, an app winget has packages of a similar name for says "No exact match in winget", and one it has nothing like says "Not in winget".
- [ ] When App Installer has an update, track it and update it: if Windows still reports the old version, its row says to quit Tiny Tracker and open it again, and after that the next check finds it up to date.
- [ ] Turn Auto on for one app, then click **Back up** in Settings: Windows' Save dialog suggests "Tiny Tracker apps.json", and the flyout stays open behind it.
- [ ] Stop tracking two apps, one with Auto on, then **Restore** the file: the Open dialog keeps the flyout open too, and the card says "Finding which apps are installed…". Then "Added 2 apps." shows, and both are back with their Auto.
- [ ] Restore the same file again: "Every app in this file is tracked already."
- [ ] Turn **Update apps automatically** on and click **Back up** again. Turn the switch off, stop tracking two apps and **Restore** that file: both come back without the Auto pill, and turning the switch on gives them both the pill. Turn it off again.
- [ ] In Notepad, add `{ "id": "Example.Missing", "name": "Example Missing" }` to the file's apps and restore it: "No apps were added. Not installed here: Example Missing." Restoring any other .json file says "This file isn't a Tiny Tracker app list."
- [ ] Make `%APPDATA%\Tiny Tracker\settings.json` read-only, then tick an app in Choose apps: the tick is undone, and "Your change couldn't be saved." has **Details** with a code. Clear read-only again.

## Conditions

- [ ] The install window: set to start at the next hour, an Auto app says "Installs automatically at" that hour, and installs once it comes.
- [ ] With an Auto app waiting for the window, set the clock or the time zone past its start, or sleep and wake past it: it installs at once, not at the old hour.
- [ ] A full-screen game holds Auto updates until it closes.
- [ ] On a metered connection, Auto rows say "Waits for an unmetered connection" and wait.
- [ ] With Energy saver on, Auto rows say "Waits for Energy saver to turn off" and wait.
- [ ] The speed limit: turning it on takes one prompt the first time, and a download runs near the limit.

## Resources

- [ ] With the flyout closed, `scripts\check-resources.ps1` says PASS for the installed copy's budgets in the design's §9. If it says INCONCLUSIVE, a check or an install ran meanwhile: run it again.

## Uninstall

- [ ] Settings > Apps > Tiny Tracker > Uninstall: Windows' prompt, Inno's confirmation, then "Also remove your settings and history?".
- [ ] Yes removes the app, the Start menu entry, Start with Windows, the notification registration, the tray icon's entry and `%APPDATA%\Tiny Tracker`.
- [ ] With silent mode on when it uninstalls, Task Scheduler has no `Tiny Tracker` folder afterwards.
- [ ] If the speed limit was on, winget's proxy option is off again: `winget settings export` shows `"ProxyCommandLineOptions": false`.
- [ ] `scripts\scan-leftovers.ps1 -Compare $env:TEMP\before.json`, from an admin PowerShell, finds nothing left by Tiny Tracker.

## After publishing

The self-update, once this release is out. Install the previous release from its page on GitHub, with silent mode off, and keep the flyout closed until a step opens it.

- [ ] A minute after Tiny Tracker starts, a toast says "Tiny Tracker <version> is available", once. The flyout then shows "Tiny Tracker <version>" at the top of Updates.
- [ ] Update installs it after Windows' prompt: the row says "Installing… Tiny Tracker will restart", then Tiny Tracker comes back on the new version with its icon where it was and no flyout, and History shows "Tiny Tracker · <previous> → <version>". Task Manager's Details tab shows it with Elevated: No.
- [ ] The apps on Auto before the update still have the Auto pill, and **Update apps automatically** is as it was.
- [ ] Uninstall it, removing the settings and history, and install the previous release again. Turn silent mode on (one prompt): with "Update Tiny Tracker automatically" on, it updates by itself with no prompt, and a toast says "Tiny Tracker was updated to <version>". What's new opens the release's page, and Task Manager shows Elevated: No.
