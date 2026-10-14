# FrameFX Assistant (experimental)

An optional, fully local assistant built into FrameFX. It can search the web, open a page, launch apps you allow, make and apply presets with the Preset Assistant, and start or stop FrameFX. Every action asks you to confirm first.

## Layout
- `FrameFX.Agent/`: the assistant core (net8.0).
- `FrameFX.Agent.UI/`: the chat window and Confirm dialog (WinForms).
- `AppIntegration/`: files that go into the app project. `MainForm.Agent.cs` adds the "FrameFX Assistant" button and the Start/Stop hooks. Wire it with `AgentBootstrap.Open(this)` on the button and `AgentBootstrap.Shutdown()` in `OnFormClosed`. These files need the 1.4.1-beta.8 app (Preset Assistant and anti-cheat APIs).
- `scripts/Setup-FrameFXAgent.ps1`: one-time download of the local runtime and model (about 2 GB). On MEHPC it installs to `W:\FrameFX\agent` by default. Use `-InstallDir` to choose another folder.
- `LICENSES/`: third-party licenses.
- `tests/`: xUnit tests. Run them with `dotnet test Agent/tests/FrameFX.Agent.Tests`.
