<#
.SYNOPSIS
  Prepares a Windows PC to receive a RoboSense Airy (manual 3.4): static IP on the adapter the LiDAR is
  plugged into, plus inbound firewall rules for MSOP/DIFOP so the Unity Editor and players can receive.

.EXAMPLE
  # List adapters (no admin needed)
  .\setup-windows.ps1 -List

  # Run in an elevated PowerShell
  .\setup-windows.ps1 -InterfaceAlias "Ethernet 2"

  # Undo: back to DHCP and remove the firewall rules
  .\setup-windows.ps1 -InterfaceAlias "Ethernet 2" -Revert
#>
[CmdletBinding()]
param(
    [string]$InterfaceAlias,
    [string]$IPAddress = "192.168.1.102",
    [int]$PrefixLength = 24,
    [int]$MsopPort = 6699,
    [int]$DifopPort = 7788,
    [switch]$List,
    [switch]$Revert,
    [switch]$SkipFirewall
)

$ErrorActionPreference = "Stop"
$RuleName = "RoboSense Airy LiDAR (UDP $MsopPort,$DifopPort)"

if ($List -or -not $InterfaceAlias) {
    Get-NetAdapter | Sort-Object Status, Name |
        Format-Table Name, InterfaceDescription, Status, LinkSpeed, MacAddress -AutoSize
    if (-not $List) { Write-Host "Pass -InterfaceAlias <Name> for the adapter the Airy interface box is connected to." }
    return
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

$adapter = Get-NetAdapter -Name $InterfaceAlias

if ($Revert) {
    Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -eq $IPAddress } | Remove-NetIPAddress -Confirm:$false
    Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -Dhcp Enabled
    Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "$InterfaceAlias is back on DHCP; firewall rule removed."
    return
}

# Static IPv4 on the LiDAR adapter. No gateway: the Airy link is a point-to-point/local subnet.
Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -Dhcp Disabled
Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -ne $IPAddress } | Remove-NetIPAddress -Confirm:$false
if (-not (Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $IPAddress -ErrorAction SilentlyContinue)) {
    New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $IPAddress -PrefixLength $PrefixLength | Out-Null
}
Write-Host "$InterfaceAlias -> $IPAddress/$PrefixLength"

if (-not $SkipFirewall) {
    # An unidentified network is classified Public; allow the ports on every profile so Unity is not blocked.
    Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol UDP `
        -LocalPort $MsopPort, $DifopPort -Profile Any | Out-Null
    Write-Host "Firewall: inbound UDP $MsopPort,$DifopPort allowed on all profiles."
    Write-Host "If Windows still asks when Unity first listens, tick both Private and Public networks."
}

Write-Host ""
Write-Host "Next: power the Airy, wait for the red and green LEDs on the interface box, then"
Write-Host "  ping 192.168.1.200"
Write-Host "  python tools\airy_monitor.py"
Write-Host "  browse http://192.168.1.200 for the Web UI"
