# Peripheral Battery Dashboard agent instructions

These instructions apply to every automated coding agent working in this repository. User instructions remain authoritative, but do not weaken the device-safety and privacy rules below.

## Purpose and distribution baseline

This is a Windows 10/11 x64 WPF tray application targeting .NET Framework 4.8. It reads battery state from Bluetooth/XInput devices and from narrowly matched HID collections. The application itself must continue to run without Codex or another LLM after installation.

The public distribution intentionally starts with zero active device profiles. `Profiles/builtin.devices.json` must remain a valid SchemaVersion 1 document with an empty `Profiles` array. The provider catalog still contains implementations previously validated against these exact identities so an agent may reuse them only after proving the current device uses the same protocol and creating a per-PC user profile:

| Provider reference device | Transport | Previously validated identity (not auto-registered) |
|---|---|---|
| SteelSeries Arctis Nova 7 Gen 2 | 2.4 GHz USB dongle | VID `1038`, PID `227E`, MI `03`, Usage Page `FFC0`, Usage `0001` |
| AULA F108 Pro | 2.4 GHz USB dongle | VID `05AC`, PID `024F`, MI `03`, Usage Page `FF60`, Usage `0061` |
| VXE R1 SE+ | 2.4 GHz USB dongle | VID `373B`, PID `1085`, MI `01`, Usage Page `FF02`, Usage `0002` |
| Bluetooth SIG standard Battery Service | Bluetooth LE | Provider `builtin.bluetooth.gatt-battery`; service `180F`, characteristic `2A19`; per-PC local service ID required, optional VID/PID as an additional AND condition |
| Xbox Wireless Controller | Bluetooth | VID `045E`, PID `0B13`; exact GATT, with unbound XInput disabled by default |

These are reusable protocol implementations, not default device registrations. Do not broaden a match or claim support for another hardware revision without evidence. A shared brand name or similar appearance does not prove protocol compatibility. For every discovered peripheral, the agent must research and establish the read-only battery protocol, then register a complete user profile; if the request/response differs, it must implement and validate a separate provider before registration.

Missing protocol material is a required research task, not an immediate blocker. Never stop merely because the repository lacks a protocol or the user did not provide documentation, captures or command bytes. Request approval for the redacted network research scope, then directly search manufacturer documentation/support/downloads, legally reviewable web-driver assets, auditable device-specific open source and public raw source using the query matrix below. A `blocked` result without the concrete queries, URLs and source classes actually checked is a failed task.

## Required reading and first actions

Before changing files, read in this order:

1. `README.md`
2. `CODEX-PROMPTS.md`
3. `DEVICE-ADDING.md`
4. `Profiles/builtin.devices.json`
5. `Plugins/README.md` and `Plugins/SamplePlugin.cs.txt` when plugin work is in scope

Start with read-only inspection. Check the working tree and preserve unrelated user changes. Identify the requested outcome, every target device/transport, available evidence and the smallest appropriate layer:

- A request may list zero, one or multiple devices. Treat every listed device and every newly discovered unsupported candidate as part of the same batch. Track identity, evidence, implementation and validation separately for each device; parallelize independent research when safe. One device becoming blocked or failing validation must not cause the remaining devices to be skipped. Finish with an explicit per-device disposition.

- Existing exact match: diagnose before editing anything.
- Unsupported or uncertain match: run the redacted passive `--inventory` first. It enumerates descriptor metadata for USB dongles, wired devices and Bluetooth HID collections plus Windows-registered standard Bluetooth Battery Service interfaces. It performs no provider/battery request, HID input read/output/Feature I/O, or GATT characteristic value read. Use it to establish HID VID/PID/interface/Usage/report lengths and standard BAS VID source/VID/PID or per-PC local service ID without exposing a full device path, serial number, Bluetooth address or username. Require exit code 0, `complete=true`, and `profileWarningCount=0`; otherwise treat the inventory as incomplete rather than as proof that no device exists. Check `coverage` before making discovery claims. It does not guarantee discovery of XInput-only, audio-only or Bluetooth devices without the standard Battery Service; use safe Windows metadata and the minimum product-name question for those cases.
- Treat any `bluetoothBatteryServices` entry as the standard BAS fast path. Register `builtin.bluetooth.gatt-battery` with `Transport=bluetooth-gatt` before attempting vendor-protocol research. Always copy that entry's `localServiceId` into `BluetoothServiceId` in the per-PC user profile; add exact VID/PID only as supplemental AND conditions when exposed. Never put the pseudonymous local service ID in a web query, issue, shared profile or upstream default. If needed, narrow further with a reviewed friendly-name AND condition; never take the first match.
- When the installation prompt contains no device list, do not stop for missing input. Inventory all exposed HID collections. Locally review `bestEffortSanitizedProductString` for private text before using it in an external query, then research a clear product string plus VID/PID immediately. Treat `researchCandidate=true` and broad profile selectors as investigation candidates; an exact selector match alone does not prove that its provider is registered or operational. For a generic receiver, disclose and obtain approval for a before/after inventory while the user toggles only that device or removes only that dongle, then compare stable `deviceGroupId` values and HID identity tuples. If it remains ambiguous, ask once for the exact product name or a label photo with serials/barcodes redacted. Exclude a wired or batteryless device only with manufacturer specifications or safe Windows metadata as evidence.
- In that blank-list flow, HID coverage is not the stopping point. Disclose and obtain approval for a second read-only, redacted Windows metadata pass over present PnP, Bluetooth, audio-endpoint and XInput/game-controller metadata. It must not invoke a battery provider or send a device request. If non-HID peripherals still cannot be established, ask one combined minimum question about additional battery-powered peripherals instead of silently omitting them.
- Do not require the user to supply protocol documentation as the default next step and do not mark a device `blocked` merely because the built-in list does not contain its name. After disclosing the research scope and receiving approval for network access, proactively search official manufacturer documentation and support/download pages, inspect vendor web-driver assets when legally and technically reviewable, and then consult auditable device-specific open-source implementations. Record the source URL, exact version/commit or retrieval date, applicable hardware revision and license.
- Build a small research query matrix before concluding that evidence is absent: exact VID:PID in colon and `VID_xxxx&PID_xxxx` forms, product string, MI/Usage/report lengths, every stable captured byte prefix, and any packet-internal device/model ID in both hexadecimal and decimal. Search raw source/code as well as prose pages. A `blocked` result must list the query variants and source classes actually checked; one retail-model repository or the first VID/PID result is not an exhaustive search.
- Resolve identity conflicts, not just the first search hit. Shared receiver VID/PID values may cover several retail models. If a passive capture or public implementation exposes an internal device/model ID, map that ID with multiple sources. If the conflict remains, use a proven family label or report the exact model as unresolved; never choose a retail name arbitrarily.
- Verified use of an existing battery protocol: add or update a narrowly matched JSON profile and its fixture tests.
- Different request, response, report type or checksum: implement a separate `IBatteryProvider` and profile only when the research establishes a device-specific, read-only battery protocol; add mock-response fixtures before any real-device request.
- Unknown protocol after a documented search, or conflicting evidence that cannot identify the exact hardware revision: explain what was searched and what evidence is still missing, then report `blocked` support before any device I/O experimentation.

For every local device, use a complete user profile under `%LOCALAPPDATA%\PeripheralBatteryDashboard\Profiles` after disclosing and obtaining approval for the exact file write. Use the GUI profile importer only after the GUI startup/device-I/O approval below. Do not add personal or product-specific active profiles to `Profiles/builtin.devices.json`, including in upstream changes; the empty file is a public-distribution contract. Provider implementations, fixtures and documentation may be contributed upstream after validation without turning a device into an active default.

## Non-negotiable HID safety rules

Treat every HID interrupt write, output report and Feature report as potentially state-changing. It may alter RF pairing, firmware mode, DPI, key maps, lighting or onboard profiles.

- Never fuzz, brute-force or scan command bytes or report IDs.
- Never send an unknown, guessed or merely similar device command.
- Use only a read-only battery/status request supported by manufacturer documentation, a user-supplied capture, a legally reviewable vendor web-driver asset, or an auditable and device-specific existing implementation.
- A public implementation is evidence only after its exact device/revision, transport, report type and bytes are traced. Record its URL, pinned revision and license; do not copy code whose license is absent or incompatible.
- Match VID, PID, interface number, Usage Page and Usage as narrowly as the evidence permits before opening a device for a request.
- For a passive input-only protocol with zero transmitted bytes, use `HidSession.OpenReadOnly` and `ReadInputReportAsync`; do not open the HID collection with write access. Validate that the provider source contains no output, interrupt-write or Feature call.
- Validate report ID, complete length, fixed headers, checksum when present, and decoded value range. Do not turn malformed data into an estimated percentage.
- Turn every supplied or approved capture and every distinct documented state/subtype into a named regression fixture. If evidence includes separate charging, discharging, wired, sleeping or unavailable forms, either parse and map each form correctly or explicitly leave that form unsupported; never silently accept one form and drop another. Reject unknown state values, but do not require padding or trailing bytes to be zero unless the evidence establishes them as fixed protocol bytes.
- Apply cancellation and `EffectiveTimeoutMilliseconds` to every wait. Dispose every handle and `HidSession` deterministically.
- Treat sleep, disconnect and exclusive use by vendor software as normal unavailable states. Do not loop aggressively after failures.
- Do not ask the user to disable Windows security controls or install an unreviewed binary plugin.

If safe implementation requires a device command whose purpose cannot be established, report the blocker instead of trying it.

## Approval boundaries

Read-only repository and system inspection is allowed when it is relevant to the request. Explain the exact target and obtain the user's approval before:

- using network access for unsupported-device research; state the intended source classes and that no downloaded executable will be run;
- installing or downloading build tools, drivers, vendor applications or other software;
- changing the registry, Windows startup behavior, services, scheduled tasks or security settings;
- writing outside the repository or the app's documented user-profile folder;
- running or restarting the GUI when it may create or change its per-user auto-start entry, load a newly placed profile or plugin, or start exact-match provider I/O;
- sending any device request not already present and validated in the checked-in provider for that exact hardware match;
- committing, pushing, opening a pull request, publishing artifacts or creating a release.

Do not interpret a request to diagnose or adapt a device as authorization to publish changes.

Before the first GUI run, ask whether per-user auto-start should be enabled. Explain the complete settings file or HKCU Run value that will be written, and that the monitor starts immediately and can invoke exact-match existing providers. Obtain separate approval for the settings/registry change and the first GUI/device-I/O execution. If auto-start is declined, prepare a complete `StartWithWindows=false` settings file only after approval; if the user does not approve the required write or GUI run, leave that step pending. Apply the same disclosure and approval before restarting the GUI to load a newly placed profile or plugin.

`--self-test` validates application structure without querying connected hardware. `--inventory` is the first unsupported-device discovery step: it enumerates redacted HID descriptor and standard Bluetooth Battery Service interface metadata and performs no provider/battery request, HID input read/output/Feature I/O, or GATT characteristic value read. Explain its target and redacted fields and obtain approval before collecting the inventory. In contrast, `--diagnostics` and `--snapshot` can invoke a matched existing provider and send its already validated battery/status request to a real device. Describe that behavior and the exact matched provider, then wait for the user's explicit confirmation before using those commands; never present them as passive file inspection.

Before the first real-device execution of any newly implemented command, show the user the exact VID, PID, interface number, Usage Page/Usage, report type and length, transmitted bytes, read-only evidence, expected response, and failure impact. Obtain separate approval for that execution. When the evidence remains insufficient after the required public research, `blocked` support plus the searched sources and a precise list of missing evidence is a successful and preferred outcome.

## Privacy and repository hygiene

- Do not print, upload, commit or include in issues a complete HID/Bluetooth service path, device serial number, Windows username, absolute user-home path, Bluetooth address, per-PC `localServiceId`, token, credential or unrelated machine inventory.
- Prefer the app's redacted `--inventory` output for initial discovery and its redacted `--diagnostics` output only when provider execution is actually needed. The HID ProductString is arbitrary device-supplied text: `bestEffortSanitizedProductString` removes known sensitive patterns but is not a privacy guarantee. Review and redact it locally before putting it in a web query, quote, saved artifact or report.
- Never commit `%LOCALAPPDATA%` settings, generated personal profiles, captured traffic, logs from the user's machine, build output or secrets.
- Review every third-party plugin source before loading it. A plugin DLL runs with the same rights as this application.
- Keep local compatibility changes scoped to the named device. Do not weaken matching rules to make an unsupported device appear connected.

## Implementation constraints

- Keep device identification in JSON profiles and protocol behavior in `IBatteryProvider` implementations.
- Reusing a ProviderId requires tracing its complete `ReadAsync` control flow, including discovery, connection gating and identity binding. A helper that can parse GATT or XInput data is not enough. In particular, do not attach a provider that scans every XInput slot to multiple profiles unless it can bind each slot to the intended VID/PID without duplicate readings.
- Every HID profile that can reach provider I/O must specify exact VID/PID, Usage Page/Usage and either a numeric `InterfaceNumber` or `RequireNoInterfaceNumber=true` when the enumerated device path genuinely has no MI component. A null interface without that explicit flag is a broad research selector and is blocked from provider I/O.
- The only accepted transports are `hid`, `bluetooth-gatt` and `xinput`. Built-in HID providers must use `hid`, the generic BAS provider must use `bluetooth-gatt`, and the Xbox provider must use `xinput`; do not alter transport text to bypass a selector gate.
- Generic BAS profiles require a valid per-PC `BluetoothServiceId`; VID/PID are optional supplemental AND conditions. Use `BluetoothNameContains` only as a reviewed additional AND condition and fail closed when the name is missing or different. The standard 2A19 value proves percentage only; do not infer charging state. Preserve presence when the exact service exists but the value is temporarily unreadable, and reject ambiguous or incompletely enumerated multiple-match candidates.
- A custom provider for Bluetooth devices without BAS may still use `Transport=bluetooth-gatt`, but it must not reuse the BAS `BluetoothServiceId`; require exact VID/PID and make the vendor service/characteristic identity explicit in the provider and its evidence-backed tests.
- The Xbox provider does not supply an implicit Microsoft VID/PID. Exact Bluetooth GATT use requires explicit profile VID/PID; only a separately verified fixed `XInputUserIndex` plus `AllowUnboundXInput=true` may use the non-identity XInput fallback.
- External plugins must reference `PeripheralBatteryDashboard.Runtime.dll`, never either EXE.
- A plugin `ProviderId` must be unique and must exactly match its profile.
- Preserve Windows 10/11 x64 and .NET Framework 4.8 compatibility unless the user explicitly requests a migration.
- Preserve the 15/30/60/120-second polling choices, failure backoff and Bluetooth cache behavior unless the request specifically concerns them.
- Do not edit generated distribution folders as source. Make source/document/profile changes at the repository root and rebuild.

## Validation

Use an isolated output directory when practical so existing artifacts and a running installed copy are not overwritten:

```powershell
pwsh -NoProfile -File .\build.ps1 -Configuration Release -OutputDirectory dist-agent-check
.\dist-agent-check\PeripheralBatteryDashboard.Diagnostics.exe --self-test
```

If Windows PowerShell blocks the script by execution policy, do not add `-ExecutionPolicy Bypass` or change machine policy. Use an already installed, trusted `pwsh` as above; if it is unavailable, report the build as blocked and request approval for any proposed tool installation.

When hardware is present and the user requested a live check, first describe the exact matched ProviderId and device I/O and wait for the user's explicit confirmation as required above. Only then run redacted diagnostics and manually compare the reading with known device state. Do not infer protocol correctness from compilation or one plausible percentage alone.

Before finishing:

1. Review the diff for unrelated changes and private identifiers.
2. For unsupported-device work, list the sources searched, URLs, exact revisions or retrieval dates, licenses and how each source maps to the exact hardware identity and read-only protocol.
3. Report fixture, build and self-test results accurately, including anything not run.
   A change is not complete when compilation or required tests did not run or returned a nonzero exit code.
   List each supplied/approved capture or documented state and the exact fixture that covers it; a partial parser must be reported as partial support rather than complete support.
4. State which device revision and connection were actually validated.
5. Separate confirmed support from assumptions and list remaining risks.
6. For any newly inferred protocol or new provider, obtain an independent second-pass review that reopens the raw evidence, source URLs, parser, every fixture and the complete `ReadAsync` path without relying on the implementer's conclusion. The reviewer must explicitly check state/subtype semantics, identity binding, report access mode and forbidden writes. If no independent review is available or it finds a mismatch, keep the change uninstalled and report it as unverified/partial rather than supported.
7. Leave publishing and any further external changes pending unless explicitly authorized.

## Model selection guidance

A lightweight coding model such as Codex Luna can follow this documented workflow when it has local tools and approved web access: collect a read-only HID descriptor inventory, research public evidence, add a narrowly matched profile or implement a well-documented provider, and run fixtures/build/self-test. A stronger reasoning model is recommended for conflicting evidence, reverse engineering or complex provider review, but model size is not a reason to skip the required research and immediately report `blocked`. Model strength never permits unsafe HID experimentation.
