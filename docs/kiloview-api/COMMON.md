# Shared HTTP conventions and integration workflows

Historical application `0.8.9-dev.1` snapshot, 7 September 2026. Project behavior and examples refer to that baseline; see the [scope and current-source links](README.md) before applying them to later releases.

[Documentation home](README.md) · [N6](N6.md) · [N60](N60.md)

## 1. Addressing and session handling

**Project:** The adapters connect directly to `http://<device-address>` on the normal HTTP port. These are device endpoints, not the Job Configurator service at port 8091. HTTPS support and certificates have not been validated by this project. Use a trusted management network for the documented HTTP workflow; credentials and tokens travel over that connection.

JSON writes use `Content-Type: application/json`. Query strings must be URL encoded. Path and property casing must be preserved. Firmware uploads use multipart data instead of JSON. Login returns a token in the JSON body; do not assume that a client cookie jar will automatically construct the cookies required by later requests.

Supply the model-specific authenticated session for subsequent reads and writes unless explicitly described as a login request. The recipes assume that session has already been established.

Create independent session state per device. The adapters normally authenticate at the beginning of each high-level operation; no dependable token lifetime is established here. Refresh authentication after firmware, credentials or address changes. Avoid logging passwords, token cookies, raw Authorization values or KiloLink onboarding keys.

## 2. Response handling

The common success shape is illustrated below. Each endpoint's `data` shape is described in its model document.

```json
{
  "result": "ok",
  "data": {}
}
```

Project clients check HTTP success first, parse JSON, then reject a string `result` other than `ok`. An HTTP 200 response can still report an application error. Preserve the response body's error information when diagnosing failures.

Do not force every response through this envelope:

| Response | Handling |
| --- | --- |
| N60 mode read | Read text and remove optional surrounding JSON-string quotes |
| N60 codec status/configuration | Some responses are the payload at the root; others wrap the object in `data` |
| N60 KiloLink interfaces | Interface array is at root `list` |
| N6 KiloLink legacy read | Configuration object is at root `cfg` |
| N60 blank-preset selection | Firmware 2.45 can return an empty `result` string; handle only for that operation and verify output afterward |
| N6 firmware rejection | Duplicate `msg` properties can carry both explanation and numeric code; retain both |

Do not apply the N60 blank-preset exception to all mutations. Do not interpret an HTML login page, an empty body or a transport timeout as a confirmed configuration change.

### PowerShell JSON validation helper

Use this with the model-specific PowerShell login examples. It intentionally requires JSON and does not handle the N60 text-mode endpoint. It reports duplicate error messages through the original raw response body.

```powershell
function Invoke-KvJson {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [ValidateSet('GET', 'POST')] [string] $Method = 'GET',
        [hashtable] $Headers = @{},
        [Microsoft.PowerShell.Commands.WebRequestSession] $Session,
        [object] $Body,
        [int] $TimeoutSec = 8
    )
    $request = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        TimeoutSec = $TimeoutSec
        UseBasicParsing = $true
        ErrorAction = 'Stop'
    }
    if ($Session) { $request.WebSession = $Session }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
    }
    $response = Invoke-WebRequest @request
    $document = ConvertFrom-Json -InputObject $response.Content -ErrorAction Stop
    if ($document.result -is [string] -and $document.result -ine 'ok') {
        throw "Device rejected the request: $($response.Content)"
    }
    return $document
}
```

This is an example helper, not a complete SDK. Its error text may contain sensitive response data, so redact before sharing. Run the helper and a model's login example in the same PowerShell session.

## 3. Timing and retries

These are project implementation choices, not manufacturer rate limits or guaranteed deadlines.

| Operation | Existing implementation |
| --- | --- |
| Normal model requests | Typically 8-second HTTP timeout |
| Preset population | 45-second client timeout; up to 30 discovery passes, about one second apart |
| Firmware request | 20-minute HTTP timeout; separate reconnect/version verification |
| N6 mode transition | Up to 90 seconds after switching; requires five consecutive ready checks at roughly 2-second intervals, including the target codec service |
| N6 codec-proxy recovery during selected writes | Up to 45 seconds, retrying recognized availability errors about every 3 seconds |
| N60 rate-limited identity/discovery writes | Up to five attempts with increasing 750 ms multiples |
| Monitoring | About 15 seconds between monitor passes, plus the work in a pass |

Retry reads and specifically understood configuration operations with a bound. A retry of “append preset” can add another entry; read back first when its response was lost. Do not blindly resend firmware uploads, credential changes, network writes or role changes following a timeout.

## 4. Firmware upload contract

**Project:** Both models use the same multipart construction, with different URLs and binary field names.

| Model | Method and path | Binary field | Text field |
| --- | --- | --- | --- |
| N6 | `POST /api/firmware/upgrade.json` | `upload` | `path`, containing the firmware filename |
| N60 | `POST /api/systemctrl/system/upload` | `file` | `path`, containing the firmware filename |

Authenticate using the model-specific flow. Stream the matching model's `.bin` file. The multipart boundary parameter is unquoted in the project implementation. Names and filenames are quoted; `filename*` is omitted. The binary part must put `Content-Disposition` **before** `Content-Type` for the observed N6 parser.

Illustrative N6 multipart body below; replace the boundary, filename and binary content. Use CRLF line endings. The text in square brackets represents bytes, not literal upload content.

```http
Content-Type: multipart/form-data; boundary=----KvFirmwareExample

------KvFirmwareExample
Content-Disposition: form-data; name="upload"; filename="firmware-N6.bin"
Content-Type: application/octet-stream

[binary firmware bytes]
------KvFirmwareExample
Content-Disposition: form-data; name="path"

firmware-N6.bin
------KvFirmwareExample--
```

For N60, change `upload` to `file` and use the N60 firmware filename and upload URL. Do not assume any HTTP library's default multipart serialization matches this layout.

Hardware evidence: on 5 September 2026 the N6 initially rejected an upload with `0401003`. Correcting the multipart header order produced `result: "ok"`, and the device returned reporting `2.10.0011.0885`. The shared formatter is covered by the project's offline regression fixture. That day's N60s already matched the requested version and did not undergo flashing. See the [test report](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/E2E-ONBOARDING-TEST-2026-09-05.md).

An accepted upload is not proof the upgrade completed. Wait for reconnection, authenticate again, compare the returned serial with the intended device, and check `softwareVersion` against the selected package. Serial comparison is recommended for a standalone client; the current onboarding implementation preserves its saved device ID and checks that the package filename contains the returned software version before subsequent network/KiloLink writes. Its firmware wait allows up to 8 minutes; this is separate from the upload timeout.

The older [Kiloview firmware module reference](https://www.kiloview.com/en/docs/device-http-api/10-firmware-module/) corroborates the N6-style `upload` and `path` fields. Its wider device applicability should not be assumed from that shared endpoint alone.

## 5. Recommended provisioning sequence

The following summarizes the project workflow. A standalone controller should retain its own readbacks and recovery state.

1. Read device identity and existing configuration. Complete initial account setup and API access where required.
2. If upgrading, upload the correct image and wait for the intended firmware and serial to return.
3. Apply the planned static address. Reconnect at that address and confirm the same physical device.
4. Enter a ready encoder phase to configure the sender identity and probe HDMI input. N6 transitions need the preview safeguards described in its reference.
5. Configure KiloLink, hostname, NDI group/channel and Discovery Server. N60 onboarding deliberately selects dedicated HB mode.
6. Select the final encoder/decoder role. Wait for that codec service, then reapply and verify Discovery Server configuration for the final role.
7. Populate decoder presets from actual discovery results. Preserve the blank preset; do not invent source IDs, listener URLs or slot IDs.
8. Select the desired source and check decoder status. Verify the physical picture separately when the output matters.
9. Apply multicast as a separate transport operation if required. Verify stored parameters, decoder transport and actual media traffic.

Changing the Job Configurator's saved inventory is not a device API operation. Windows NDI settings belong to the separate PC Agent. Those server/agent APIs are outside this reference.

## 6. What a successful verification proves

| Check | What it establishes |
| --- | --- |
| HTTP/API acceptance | The request reached the service and was accepted according to that response |
| Configuration readback | The exposed settings retain the requested values |
| Correct serial after reconnect | You reached the intended physical unit |
| Selected source name/URL | The decoder API selected that source |
| HDMI/output resolution | The service reports a negotiated/configured output format; it does not prove visible pixels |
| Packet capture | Actual traffic destinations and transport during the observed interval |
| Visual display inspection | A picture is visible on the checked output at that time |

The 13 August N60 multicast report found saved allocation and on-wire destination disagreement. The 14 August N6 report verified media within its allocation. These findings must remain model-, firmware- and date-specific; see [Sources](SOURCES.md).
