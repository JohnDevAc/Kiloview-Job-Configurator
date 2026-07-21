# Third-party licensing and trademark notices

Kiloview Job Configurator does not reference third-party NuGet packages. Its self-contained Windows distribution does, however, embed the Microsoft .NET and ASP.NET Core runtimes.

The corresponding upstream license and notice files distributed with the runtime packs used by this release are included here:

- `DOTNET-LICENSE.txt`
- `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`
- `ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt`

The application dynamically loads `Processing.NDI.Lib.x64.dll` from a separately installed copy of NDI Tools. The NDI runtime and NDI Tools are not redistributed with this project. Their installation and use are governed by Vizrt NDI AB’s terms. See <https://ndi.video/> and <https://ndi.video/tools/>.

NDI® is a registered trademark of Vizrt NDI AB.

Kiloview device firmware, KiloLink Server Pro, vendor documentation, and vendor binaries are not included in this repository or installer. Kiloview, KiloLink, and related product names belong to their respective owner. This project is not endorsed by or affiliated with Kiloview or Vizrt NDI AB.
