function Select-InstallOptions([string]$PackageRoot, [bool]$IncludePcAgent) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $dialog = New-Object Windows.Forms.Form
    $dialog.Text = 'NDI Job Configurator Installation'
    $dialog.Size = New-Object Drawing.Size(740, 620)
    $dialog.MinimumSize = New-Object Drawing.Size(600, 480)
    $dialog.StartPosition = 'CenterScreen'
    $layout = New-Object Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'; $layout.ColumnCount = 1; $layout.RowCount = 4; $layout.Padding = 16
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Percent', 100)))
    foreach ($height in @(58, 40, 42)) { [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute', $height))) }
    $license = New-Object Windows.Forms.RichTextBox
    $license.Dock = 'Fill'; $license.ReadOnly = $true
    $serverLicense = Get-Content -LiteralPath (Join-Path $PackageRoot 'LICENSE.md') -Raw
    $agentLicense = Get-Content -LiteralPath (Join-Path $PackageRoot 'pc-onboarding\LICENSE.md') -Raw
    $component = New-Object Windows.Forms.CheckBox
    $component.Dock = 'Fill'; $component.Checked = $IncludePcAgent
    $component.Text = "Install PC Onboarding / PC Agent (recommended)`r`nAllows this PC to join server jobs without another confirmation."
    $consent = New-Object Windows.Forms.CheckBox
    $consent.Dock = 'Fill'; $consent.Text = 'I accept the displayed license agreements.'
    $install = New-Object Windows.Forms.Button
    $install.Dock = 'Right'; $install.Width = 140; $install.Text = 'Accept and Install'; $install.Enabled = $false
    $install.DialogResult = 'OK'
    $refresh = { $license.Text = $serverLicense + $(if ($component.Checked) { "`r`n`r`nPC AGENT LICENSE`r`n$agentLicense" }); $consent.Checked = $false }
    $component.Add_CheckedChanged($refresh)
    $consent.Add_CheckedChanged({ $install.Enabled = $consent.Checked })
    & $refresh
    $layout.Controls.Add($license, 0, 0); $layout.Controls.Add($component, 0, 1)
    $layout.Controls.Add($consent, 0, 2); $layout.Controls.Add($install, 0, 3)
    $dialog.Controls.Add($layout)
    try {
        if ($dialog.ShowDialog() -ne 'OK' -or -not $consent.Checked) { throw 'Installation cancelled.' }
        return $component.Checked
    } finally { $dialog.Dispose() }
}
