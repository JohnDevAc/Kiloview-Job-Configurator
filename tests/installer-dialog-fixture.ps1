param([Parameter(Mandatory)][string]$PackageRoot,[Parameter(Mandatory)][string]$EvidencePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DialogFixtureWindow {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
. (Join-Path $PSScriptRoot '..\installer\Select-InstallOptions.ps1')
$script:observations=@();$script:ticks=0;$script:licenceText=''
$timer=[Windows.Forms.Timer]::new();$timer.Interval=200
$timer.Add_Tick({
    $script:ticks++
    foreach($form in @([Windows.Forms.Application]::OpenForms)){
        $controls=@($form.Controls[0].Controls)
        $consent=$controls|Where-Object {$_ -is [Windows.Forms.CheckBox] -and $_.Text -like 'I accept*'}
        $install=$controls|Where-Object {$_ -is [Windows.Forms.Button]}
        $license=$controls|Where-Object {$_ -is [Windows.Forms.RichTextBox]}
        if(@($consent).Count -ne 1 -or @($install).Count -ne 1){throw 'Fixture could not identify the consent controls.'}
        if(@($license).Count -ne 1){throw 'Fixture could not identify the licence text control.'}
        $script:licenceText=$license.Text
        $script:observations+=@{tick=$script:ticks;visible=$form.Visible;nativeVisible=[DialogFixtureWindow]::IsWindowVisible($form.Handle);minimized=[DialogFixtureWindow]::IsIconic($form.Handle);foreground=([DialogFixtureWindow]::GetForegroundWindow() -eq $form.Handle);state=$form.WindowState.ToString();title=$form.Text;handleCreated=$form.IsHandleCreated;showInTaskbar=$form.ShowInTaskbar;bounds=$form.Bounds.ToString();consentChecked=$consent.Checked;installEnabled=$install.Enabled}
        if($script:ticks -ge 10){$form.DialogResult=[Windows.Forms.DialogResult]::Cancel;$form.Close()}
    }
})
$timer.Start()
try {
    [void](Select-InstallOptions -PackageRoot $PackageRoot -IncludePcAgent $true)
    throw 'Fixture unexpectedly accepted consent.'
} catch {
    if($_.Exception.Message -ne 'Installation cancelled.'){throw}
} finally {
    $timer.Stop();$timer.Dispose()
    @{cancelled=$true;observations=$script:observations;licenceText=$script:licenceText}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
