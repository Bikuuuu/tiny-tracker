# Security

Tiny Tracker runs a small helper with administrator rights to install updates, so security reports matter a lot here.

## Reporting a problem

Please report it privately, through [Report a vulnerability](https://github.com/Bikuuuu/tiny-tracker/security/advisories/new) in this repository's Security tab. Please don't open a public issue for it.

Helpful details:
- the Tiny Tracker version, and Windows' version
- what an attacker could do, and what they'd need first (for example, a standard account on the same PC)
- the steps to reproduce it

You'll get a reply as soon as possible, usually within a week. Once a fix is released, the advisory is published, with credit to you if you'd like it.

## What's covered

The latest release. Areas of most interest:
- the elevated helper, `TinyTracker.Helper.exe`, and its named pipe
- silent mode's scheduled task
- how winget is started and checked
- the download speed limit's local relay
- the installer and the uninstaller
