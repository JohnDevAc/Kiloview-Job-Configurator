# Factory-reset Kiloview onboarding test — 5 September 2026

All three requested Kiloviews are onboarded, static, online, and free of reported
device or multicast errors. The initial run exposed an N6 firmware upload defect;
a corrected upload and N6-only retry completed the fleet. This was not an
uninterrupted success of the original dev.98 uploader.

| Model | Serial | Hostname | Address | Firmware | Role |
| --- | --- | --- | --- | --- | --- |
| N6 | 2007140023DC5 | LivewireTest-KV-001 | 192.168.0.90 | 2.10.0011.0885 | Decoder |
| N60 | 320113001F0A8 | LivewireTest-KV-002 | 192.168.0.91 | 2.45.0014.0170 | Decoder |
| N60 | 320113001F0AA | LivewireTest-KV-003 | 192.168.0.92 | 2.45.0014.0170 | Decoder |

## Configuration and scope

- Installed application: `0.8.0-dev.98`, Development channel.
- Selected adapter: Ethernet, `192.168.0.15/24`.
- Requested pool: `192.168.0.90–192.168.0.99`.
- Gateway: `192.168.0.1`; DNS: `8.8.8.8`.
- Job/NDI group: existing `LivewireTest`.
- KiloLink and NDI Discovery Server: `192.168.0.15`.
- NDI channels: `LivewireTest-90`, `LivewireTest-91`, `LivewireTest-92`.
- Only the three Kiloviews were selected for onboarding; the existing TeleTool
  and Windows endpoint inventory were retained. Clean-inventory deletion was not used.
- Studio Monitor was closed gracefully to satisfy the application's preflight.
  The NDI Discovery Server remained running.

## Firmware and execution

The files were uploaded from Downloads to the application's staging endpoint and
their SHA-256 digests verified:

- N6: `firmware-N6-kiloview-2.10.0011.0885-20260819.bin`, 306,934,129 bytes,
  SHA-256 `83954a6e225da51974696a4943273d78ef0ac96a3b5c0614f5980eca4eb5c904`.
- N60: `firmware-N60-kiloview-2.45.0014.0170-20260708 (1).bin`, 335,452,533 bytes,
  SHA-256 `cd49f929a9bd63f7290a39d6e38ead0796613889591edb453340bbdfdfa90369`.

Initial run `b304181d-f783-4716-bea0-92fa9d438b23` configured both N60s, which already
matched the staged firmware. The N6 firmware upload failed with `0401003`, and
the app stopped its onboarding before changing its address from `192.168.0.23`.

The N6's response contained two `msg` fields: an explanation identifying the wrong
Content-Type location, followed by the numeric error code. The existing JSON
reader retained only the last message. The N6 Web UI bundle and language catalogue
were read directly from the device to diagnose the upload contract. The vendor's
[firmware API reference](https://www.kiloview.com/en/docs/device-http-api/10-firmware-module/)
also documents the `upload` file and `path` fields used by this endpoint.

The embedded multipart parser requires the file part's Content-Disposition header
before Content-Type. A recovery request used the same API authorization and firmware
file with browser-compatible multipart formatting. Correcting that header order
changed the response to HTTP 200 with `result=ok`. The N6 subsequently reported
`2.10.0011.0885` and was retried through the installed application's onboarding API.

N6 retry `00f11c42-318f-465e-95b7-dc02b0c855cd` completed all 13 steps without errors,
including firmware readback, `.90` addressing, KiloLink registration, Discovery
Server configuration, hostname/channel assignment, role detection, and feed presets.

All three devices reported no live HDMI input and were assigned Decoder roles.
The identification endpoint selected cards for all three with zero errors. The
completion endpoint returned `completed=true`, `decoders=3`, and an empty error
list after its blank-preset checks. Subsequent monitoring showed all three online.
Physical screen images were not independently observed; identification and blanking
results above are application/device API verification.

## Dev.99 follow-up fixes

- Both model uploaders now share multipart formatting that places Content-Disposition
  before Content-Type, quotes form names, uses a bare boundary, and omits filename*
  extensions to match the browser uploader.
- Repeated firmware error-message fields retain both the explanation and code.
- Reconnect readback now preserves credentials and license-acceptance metadata.
  The live run exposed that dev.98 cleared the local `LicenseAccepted` flag during
  reconnect despite successful access/license provisioning. This is a local
  bookkeeping discrepancy; live records were not edited directly to mask it.
- Scratch C# under `artifacts/` is excluded from application compilation.

The user subsequently requested fix, push, and deployment. These corrections are
included in the source prepared for `v0.8.0-dev.99`; the hardware test itself ran on
dev.98 with the recovery upload described above. All 14 backend regression checks
pass, including strict multipart-parser compatibility, duplicate error messages,
and reconnect metadata preservation. The application Release build passes with
zero warnings or errors. An upgrade preserves existing fleet state and does not
retroactively rewrite license-acceptance flags lost by an older run.

Detailed plans, progress snapshots, final device summaries, and endpoint results
are retained in `artifacts/onboarding-test-2026-09-05/`. Existing August reports were
preserved.
