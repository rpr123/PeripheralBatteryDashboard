# Peripheral Battery Dashboard agent instructions

These instructions apply to every automated coding agent working in this repository. User instructions remain authoritative, but do not weaken the device-safety and privacy rules below.

## Purpose and supported baseline

This is a Windows 10/11 x64 WPF tray application targeting .NET Framework 4.8. It reads battery state from Bluetooth/XInput devices and from narrowly matched HID collections. The application itself must continue to run without Codex or another LLM after installation.

The checked-in baseline supports these exact devices and transports:

| Device | Transport | Current built-in match |
|---|---|---|
| SteelSeries Arctis Nova 7 Gen 2 | 2.4 GHz USB dongle | VID `1038`, PID `227E`, MI `03`, Usage Page `FFC0`, Usage `0001` |
| AULA F108 Pro | 2.4 GHz USB dongle | VID `05AC`, PID `024F`, MI `03`, Usage Page `FF60`, Usage `0061` |
| VXE R1 SE+ | 2.4 GHz USB dongle | VID `373B`, PID `1085`, MI `01`, Usage Page `FF02`, Usage `0002` |
| Xbox Wireless Controller | Bluetooth/XInput | VID `045E`, PID `0B13`; Bluetooth GATT and XInput fallback |

Do not broaden a match or claim support for another hardware revision without evidence. A shared brand name or similar appearance does not prove protocol compatibility.

## Required reading and first actions

Before changing files, read in this order:

1. `README.md`
2. `CODEX-PROMPTS.md`
3. `DEVICE-ADDING.md`
4. `Profiles/builtin.devices.json`
5. `Plugins/README.md` and `Plugins/SamplePlugin.cs.txt` when plugin work is in scope

Start with read-only inspection. Check the working tree and preserve unrelated user changes. Identify the requested outcome, the exact device/transport, available evidence and the smallest appropriate layer:

- Existing exact match: diagnose before editing anything.
- Verified use of an existing battery protocol: add or update a JSON profile.
- Different request, response, report type or checksum: implement a separate `IBatteryProvider` only when supported by reliable protocol evidence.
- Unknown protocol without adequate evidence: collect redacted diagnostics, explain what evidence is missing and stop before device I/O experimentation.

For a local one-user adjustment, prefer a complete user profile under `%LOCALAPPDATA%\PeripheralBatteryDashboard\Profiles` or the GUI profile importer. Change `Profiles/builtin.devices.json` only when the user explicitly requests an upstream repository change and the hardware match has been validated.

## Non-negotiable HID safety rules

Treat every HID interrupt write, output report and Feature report as potentially state-changing. It may alter RF pairing, firmware mode, DPI, key maps, lighting or onboard profiles.

- Never fuzz, brute-force or scan command bytes or report IDs.
- Never send an unknown, guessed or merely similar device command.
- Use only a read-only battery/status request supported by manufacturer documentation, a user-supplied capture, or a reviewable and device-specific existing implementation.
- Match VID, PID, interface number, Usage Page and Usage as narrowly as the evidence permits before opening a device for a request.
- Validate report ID, complete length, fixed headers, checksum when present, and decoded value range. Do not turn malformed data into an estimated percentage.
- Apply cancellation and `EffectiveTimeoutMilliseconds` to every wait. Dispose every handle and `HidSession` deterministically.
- Treat sleep, disconnect and exclusive use by vendor software as normal unavailable states. Do not loop aggressively after failures.
- Do not ask the user to disable Windows security controls or install an unreviewed binary plugin.

If safe implementation requires a device command whose purpose cannot be established, report the blocker instead of trying it.

## Approval boundaries

Read-only repository and system inspection is allowed when it is relevant to the request. Explain the exact target and obtain the user's approval before:

- installing or downloading build tools, drivers, vendor applications or other software;
- changing the registry, Windows startup behavior, services, scheduled tasks or security settings;
- writing outside the repository or the app's documented user-profile folder;
- running the GUI for the first time when that may enable its default per-user auto-start entry;
- sending any device request not already present and validated in the checked-in provider for that exact hardware match;
- committing, pushing, opening a pull request, publishing artifacts or creating a release.

Do not interpret a request to diagnose or adapt a device as authorization to publish changes.

`--self-test` validates application structure without querying connected hardware. In contrast, `--diagnostics` and `--snapshot` can invoke a matched existing provider and send its already validated battery/status request to a real device. Describe that behavior and the exact matched provider before using those commands; never present them as passive file inspection.

Before the first real-device execution of any newly implemented command, show the user the exact VID, PID, interface number, Usage Page/Usage, report type and length, transmitted bytes, read-only evidence, expected response, and failure impact. Obtain separate approval for that execution. When the evidence is insufficient, `blocked` support plus a precise list of required evidence is a successful and preferred outcome.

## Privacy and repository hygiene

- Do not print, upload, commit or include in issues a complete HID device path, device serial number, Windows username, absolute user-home path, Bluetooth address, token, credential or unrelated machine inventory.
- Prefer the app's redacted `--diagnostics` output. Redact additional identifiers before quoting or saving it.
- Never commit `%LOCALAPPDATA%` settings, generated personal profiles, captured traffic, logs from the user's machine, build output or secrets.
- Review every third-party plugin source before loading it. A plugin DLL runs with the same rights as this application.
- Keep local compatibility changes scoped to the named device. Do not weaken matching rules to make an unsupported device appear connected.

## Implementation constraints

- Keep device identification in JSON profiles and protocol behavior in `IBatteryProvider` implementations.
- External plugins must reference `PeripheralBatteryDashboard.Runtime.dll`, never either EXE.
- A plugin `ProviderId` must be unique and must exactly match its profile.
- Preserve Windows 10/11 x64 and .NET Framework 4.8 compatibility unless the user explicitly requests a migration.
- Preserve the 15/30/60/120-second polling choices, failure backoff and Bluetooth cache behavior unless the request specifically concerns them.
- Do not edit generated distribution folders as source. Make source/document/profile changes at the repository root and rebuild.

## Validation

Use an isolated output directory when practical so existing artifacts and a running installed copy are not overwritten:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -OutputDirectory dist-agent-check
.\dist-agent-check\PeripheralBatteryDashboard.Diagnostics.exe --self-test
```

When hardware is present and the user requested a live check, run redacted diagnostics and manually compare the reading with known device state. Do not infer protocol correctness from compilation or one plausible percentage alone.

Before finishing:

1. Review the diff for unrelated changes and private identifiers.
2. Report build and self-test results accurately, including anything not run.
3. State which device revision and connection were actually validated.
4. Separate confirmed support from assumptions and list remaining risks.
5. Leave publishing and any further external changes pending unless explicitly authorized.

## Model selection guidance

A lightweight coding model such as Codex Luna can usually follow the documented install/build flow, run diagnostics, and add a profile when the existing protocol is already verified. Prefer a stronger reasoning model for interpreting new protocol evidence or implementing and reviewing a new provider. Model strength never permits unsafe HID experimentation.
