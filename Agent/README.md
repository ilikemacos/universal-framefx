# LarkquillAI in FrameFX

**LarkquillAI panel (1.4.1 Beta10).** Chat about FrameFX in the app and let LarkquillAI build, apply and change presets. It uses the LarkquillAI API (`https://larkquillai.vercel.app/v1`, OpenAI-compatible) with your own API key, which is stored encrypted with Windows DPAPI for your account. Hobby (free) gets Quill Wren (fast) and Quill Scribe (the default), Pro ($7.99 one-time) adds Quill Sage, and Max ($12.99 one-time) unlocks every model including Quill Sovereign. Model ids from earlier betas still work. A locked model shows an Upgrade link. Every change goes through FrameFX's own checks (Sanitize + anti-cheat) and a Confirm. With no key or no connection, the panel falls back to the offline preset model.

- `FrameFX.Agent/Cloud/`: `LarkquillClient` (streaming API client), model/plan table, `LarkquillSession` (cloud turn + offline fallback).
- `FrameFX.Agent.UI/LarkquillPanel.cs`, `DpapiKeyStore.cs`: the panel and key storage.
- `AppIntegration/LarkquillBootstrap.cs`, `MainForm.Larkquill.cs`: copy into the app; add the `_larkquill` button to the layout and call `WireLarkquill()`.

## LarkquillAI Local (experimental)

An optional, fully local assistant built into FrameFX. It can search the web, open a page, launch apps you allow, make and apply presets with the Preset Assistant, and start or stop FrameFX. Every action asks you to confirm first.

## Layout
- `FrameFX.Agent/`: the assistant core (net8.0).
- `FrameFX.Agent.UI/`: the chat window and Confirm dialog (WinForms).
- `AppIntegration/`: files that go into the app project. `MainForm.Agent.cs` adds the "LarkquillAI" button and the Start/Stop hooks. Wire it with `AgentBootstrap.Open(this)` on the button and `AgentBootstrap.Shutdown()` in `OnFormClosed`. These files need the 1.4.1-beta.8 app (Preset Assistant and anti-cheat APIs).
- `scripts/Setup-FrameFXAgent.ps1`: one-time download of the local runtime and model (about 2 GB). On MEHPC it installs to `W:\FrameFX\agent` by default. Use `-InstallDir` to choose another folder.
- `LICENSES/`: third-party licenses.
- `tests/`: xUnit tests. Run them with `dotnet test Agent/tests/FrameFX.Agent.Tests`.
