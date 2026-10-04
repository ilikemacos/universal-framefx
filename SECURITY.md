# Security policy

Universal-FrameFX's app is open source so it can be audited, and reports are welcome.

## Reporting a vulnerability

Please report security issues **privately**. Don't open a public issue for them.

1. On GitHub, open this repository's **Security** tab and choose **"Report a vulnerability"** (private
   vulnerability reporting). Only the maintainers can see the report.
2. Please include:
   - the FrameFX version (Settings shows it, or run `Universal-FrameFX.exe --version`)
   - your Windows version
   - steps to reproduce, and what you expected to happen
   - any relevant lines from `%LOCALAPPDATA%\Universal-FrameFX\updates\update.log` or `crash.log`
     (check them for anything private first)

We'll acknowledge your report, keep you updated while we work on a fix, and credit you in the release notes if you'd
like.

## What we especially want to hear about

- Any way to make the updater or installer run or install a file that doesn't match the SHA-256 published on
  chopstickshq.com
- Any network traffic from FrameFX apart from the update check described in the README
- Any file FrameFX writes outside the locations listed in the README
- Anything that makes FrameFX touch other processes beyond what the README describes

## Supported versions

Only the latest release on https://chopstickshq.com/universal-framefx/ gets security fixes. The built-in updater
offers it automatically.
