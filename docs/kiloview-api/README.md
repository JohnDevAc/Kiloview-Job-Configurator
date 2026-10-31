# Kiloview N6 and N60 API documentation

Practical device integration reference · 7 September 2026 snapshot · Published 31 October 2026

This reference describes application `0.8.9-dev.1` at commit `9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9`. References to the project or its current behavior below mean that dated snapshot. Later releases include further corrections; consult the [current N6 adapter](../../Devices/N6DeviceApi.cs) and [current N60 adapter](../../Devices/N60DeviceApi.cs) when integrating against today's application. Publication does not claim new hardware or firmware validation.

These documents describe the device HTTP APIs used by NDI Job Configurator, cross-checked against Kiloview's public references. They cover authentication, initial access, identity, networking, roles, NDI discovery, source selection, presets, multicast, HDMI status, blanking, KiloLink and firmware upload.

| Document | Contents |
| --- | --- |
| [N6 API](N6.md) | N6 requests, response fields, decoder layouts and firmware-specific behavior |
| [N60 API](N60.md) | N60 requests, response variants, stream profiles and firmware-specific behavior |
| [Shared conventions and workflows](COMMON.md) | HTTP handling, errors, timeouts, firmware multipart format and onboarding order |
| [Sources and verification](SOURCES.md) | Public references, project provenance, historical hardware evidence and documentation checks |

## Scope and evidence

This is an independently written integration reference, not an official Kiloview publication or an exhaustive specification of every firmware feature. It documents the implemented request shapes rather than claiming every field is mandatory in all firmware. Additional vendor modules are linked from each model's document.

Evidence labels used throughout:

| Label | Meaning |
| --- | --- |
| **Project** | Request or behavior is implemented in the inspected source. This alone does not prove execution on every firmware. |
| **Hardware** | A dated project report records a real-device result; its firmware and verification limits are stated. |
| **Vendor** | Found in an online Kiloview reference. It may describe older firmware. |
| **Compatibility probe** | A fallback attempted by the code. Its presence in a candidate list does not establish that the endpoint exists. |

All detailed request bodies and operational recipes are based on project code unless explicitly marked Vendor. Examples use illustrative addresses and values. They are not captured responses from the current fleet. No device requests were made while writing these documents.

## The differences that matter

| Area | N6 | N60 |
| --- | --- | --- |
| API login | `POST /api/user/authorize.json` | `POST /api/systemctrl/users/login` |
| Login username field | `user` | `username` |
| Session used by project | Token and identity cookies | Raw token in `Authorization`, plus cookies and `App` header |
| Role values | `encoder`, `decoder` | `encode`, `decode` |
| Mode response | `data.mode` in a JSON object | Bare text or JSON string |
| Sender profile identifiers | `ndihx`, `ndifull` in `types` | `main/ndi-hx`, `main_full/ndi-full` in URL |
| Sender transport field | `ndi_connection` | `connection` |
| Multicast TTL request type | JSON number, e.g. `1` | JSON string, e.g. `"1"` |
| Decoder transport | Per-source `trans_mode` in source-group API | `ndi_connection` via `decode/setConnection` |
| Manual discovery fields | `ip`, `group_name` | `manuals`, `groups` |
| Static address selector | `dynamic: "n"` | `method: "static"` |
| Blank colour key | `color` | Exact case: `BlankColor` |
| Firmware file field | `upload` | `file` |

## Firmware coverage

| Model / firmware | Evidence available |
| --- | --- |
| N6 `2.00.0009.0134` | 14 August 2026 encoder acceptance report, including multicast packet capture |
| N6 `2.10.0011.0885` | 5 September 2026 successful upgrade and decoder onboarding; identification/blanking verified by APIs |
| N60 `2.45.0014.0170` | 14 August decoder acceptance and 5 September decoder onboarding; already at target firmware in September, so no N60 flash was exercised in that run |

These are tested historical versions, not a statement of the latest firmware available for download. Documented source baseline: commit `9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9`, application `0.8.9-dev.1`, then on branch `development`.

The online API landing pages advertise [N5/N6 API v3.0](https://www.kiloview.com/en/docs/n6/api/) and [N60 Web API v2.01](https://www.kiloview.com/en/docs/n60/api/). The PDFs' cover dates are August and November 2023 respectively. Upload-directory dates do not establish firmware compatibility. See [Sources](SOURCES.md) for the precise references and discrepancies.

## Start here

1. Identify the model and firmware, then use that model's authentication example.
2. Read status, hostname, networking and role before making changes.
3. Use the model-specific request body, including exact field casing and JSON types.
4. Read back the changed state after the device's services recover.
5. For media verification, check the selected source, actual picture and transport independently of saved configuration.

Network changes, role changes, stream restarts, preset replacement and firmware uploads can interrupt output. The documentation identifies these effects beside the relevant requests.
