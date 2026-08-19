# IEM 3.1-0 Platform Coupling & Dependency Inventory Manifest

> **Baseline Release:** Internet Evidence Monitor `3.0.0-rc1`  
> **Target Cycle:** `3.1.0-rc1`  
> **Authority:** `docs/ROADMAP-3.1-LINUX-MASTER.md` (Â§4.0â€“Â§4.6, Â§11, Â§12, Â§15, Â§15A)  
> **Status:** `LOCKED`  
> **UNMAPPED_COUPLING:** `NONE`

---

## 1. Executive Summary

This document represents the deterministic platform-coupling baseline inventory produced during **Phase 3.1-0 Â· Linux Portability Baseline**. Every Windows-specific API, P/Invoke, registry lookup, named pipe, service host, and storage assumption across the repository has been enumerated and mapped to its target contract and target 3.1-x implementation phase.

---

## 2. Complete Inventory Matrix by Module

### 2.1 `src/IEM.Windows` (100% Platform Adapter Layer)

| Type | File | Platform Dependency | Category | Target Contract | Target Phase |
|---|---|---|---|---|---|
| `BoundPing` | `src/IEM.Windows/BoundPing.cs` | `iphlpapi.dll` (`IcmpCreateFile`, `IcmpSendEcho2Ex`, `IcmpCloseHandle`) | NativePInvoke / ICMP | `IBoundIcmp` / `LinuxBoundIcmp` (`socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)`) | 3.1-6 |
| `MeasurementPath` | `src/IEM.Windows/MeasurementPath.cs` | `NetworkInterface` (Win32 Id/OperationalStatus) | NetworkAdapterInfo | `IMeasurementPathResolver` / `LinuxMeasurementPath` (rtnetlink) | 3.1-4 |
| `NativeWifiRadio` | `src/IEM.Windows/NativeWifiRadio.cs` | `wlanapi.dll` (`WlanOpenHandle`, `WlanGetInterfaceCapability`, `WlanQueryInterface`) | NativeWifi | `IWifiRadioInspector` / `LinuxWifiRadio` (nl80211 / rfkill sysfs) | 3.1-7 |
| `RouteResolver` | `src/IEM.Windows/RouteResolver.cs` | `iphlpapi.dll` (`GetBestRoute2`) | NativePInvoke / Route | `IRouteResolver` / `LinuxRouteResolver` (`RTM_GETROUTE` FIB) | 3.1-4 |
| `WindowsLinkInspection` | `src/IEM.Windows/WindowsLinkInspection.cs` | `NetworkInterface` link rate/state | LinkInspection | `ILinkInspector` / `LinuxLinkInspection` (`RTM_GETLINK` / ethtool) | 3.1-4 |
| `WirelessDiagnostics` | `src/IEM.Windows/WirelessDiagnostics.cs` | `wlanapi.dll` (`WlanQueryInterface`, `WlanGetNetworkBssList`) | NativeWifi | `IWirelessDiagnosticsProvider` (nl80211 BSS / survey) | 3.1-7 |
| `WlanLinkInspector` | `src/IEM.Windows/WlanLinkInspector.cs` | `wlanapi.dll` link state | NativeWifi | `IWlanLinkInspector` / `LinuxWlanInspector` (nl80211) | 3.1-7 |
| `WlanScanCache` | `src/IEM.Windows/WlanScanCache.cs` | `wlanapi.dll` BSS scan cache | NativeWifi | `IWlanScanCache` / `LinuxWlanScanCache` | 3.1-7 |
| `WindowsCngKeyProvider` | `src/IEM.Windows/Crypto/WindowsCngKeyProvider.cs` | `CngKey`, `CngProvider` (Platform & Software KSP) | CngCrypto | `IEvidenceKeyProvider` / `LinuxEvidenceKeyProvider` (PKCS#8 ECDSA P-256) | 3.1-8 |
| `WindowsCngSigningIdentity` | `src/IEM.Windows/Crypto/WindowsCngSigningIdentity.cs` | `CngKey`, `ECDsaCng` | CngCrypto | `IEvidenceSigningIdentity` / `LinuxEvidenceSigningIdentity` | 3.1-8 |
| `WindowsNamedPipeTransport` | `src/IEM.Windows/Ipc/WindowsNamedPipeTransport.cs` | `NamedPipeServerStream`, `PipeSecurity`, `GetImpersonationUserName` | NamedPipes / Identity | `IIpcTransport` / `LinuxUnixDomainSocketTransport` (`/run/.../control.sock`, `SO_PEERCRED`, `SO_PEERGROUPS`) | 3.1-3 |
| `WindowsReparsePointGuard` | `src/IEM.Windows/Storage/WindowsReparsePointGuard.cs` | `kernel32.dll` (`GetFileAttributesW`, `FILE_ATTRIBUTE_REPARSE_POINT`) | NativePInvoke / Guard | `IStoragePathGuard` / `LinuxSymlinkGuard` (`lstat`, `O_NOFOLLOW`) | 3.1-8 |
| `WindowsSessionAclProvisioner` | `src/IEM.Windows/Storage/WindowsSessionAclProvisioner.cs` | `FileSystemAccessRule`, `DirectorySecurity`, Windows SID | WindowsAcl | `IStorageProtectionProvider` / `LinuxSessionModeProvisioner` (POSIX `0700`/`0750`) | 3.1-8 |
| `WindowsTimeObservationProvider` | `src/IEM.Windows/Time/WindowsTimeObservationProvider.cs` | `kernel32.dll` (`GetSystemTimePreciseAsFileTime`, `QueryInterruptTimePrecise`, `QueryUnbiasedInterruptTimePrecise`, QPC) | NativePInvoke / PreciseTime | `ITimeObservationProvider` / `LinuxTimeObservationProvider` (`clock_gettime(CLOCK_REALTIME/BOOTTIME/MONOTONIC)`, `/proc/.../boot_id`) | 3.1-5 |

---

### 2.2 `src/IEM.Service` (Service Host & Legacy IPC)

| Type | File | Platform Dependency | Category | Target Contract | Target Phase |
|---|---|---|---|---|---|
| `IemWindowsServiceLifetime` | `src/IEM.Service/IemWindowsServiceLifetime.cs` | `System.ServiceProcess.ServiceBase` | WindowsService | Generic Host + `UseSystemd` (`IEM.Service.Linux`) | 3.1-1 / 3.1-2 |
| `PowerEventBroker` | `src/IEM.Service/PowerEventBroker.cs` | `Microsoft.Win32.SystemEvents.PowerModeChanged`, `OperatingSystem.IsWindows()` | PowerEvents | `IPowerEventSource` / `LinuxLogindPowerSource` (`login1` D-Bus `PrepareForSleep`) | 3.1-5 |
| `Program` | `src/IEM.Service/Program.cs` | `OperatingSystem.IsWindows()`, EventLog | HostComposition | `IPlatformHostCompositionRoot` | 3.1-1 / 3.1-2 |
| `ServiceInstaller` | `src/IEM.Service/ServiceInstaller.cs` | `EventLogInstaller`, `sc.exe` | Packaging / Install | Linux Packaging (`.deb` / `.rpm` / systemd unit) | 3.1-13 |
| `StatusPipeServer` | `src/IEM.Service/StatusPipeServer.cs` | `NamedPipeServerStream`, `PipeSecurity`, `OperatingSystem.IsWindows()` | NamedPipes | Extracted behind `IIpcTransport` (Unified `control.sock` on Linux) | 3.1-1 / 3.1-3 |

---

### 2.3 `src/IEM.Storage` (Shared Storage & Installation Metadata)

| Type | File | Platform Dependency | Category | Target Contract | Target Phase |
|---|---|---|---|---|---|
| `ServiceContract` | `src/IEM.Storage/ServiceContract.cs` | `Microsoft.Win32.Registry`, `Environment.SpecialFolder.CommonApplicationData`, `Environment.SpecialFolder.DesktopDirectory`, `OperatingSystem.IsWindows()` | Registry / StorageLayout | `IPlatformStorageLayout` (`LinuxStorageLayout` / `LinuxPortableStorageLayout`) & `IPlatformInstallationProbe` | 3.1-1 / 3.1-8 |

---

### 2.4 `src/IEM.App` (WPF Desktop Presentation & Host Wiring)

| Type | File | Platform Dependency | Category | Target Contract | Target Phase |
|---|---|---|---|---|---|
| `ServicePipeClient` | `src/IEM.App/Hosting/ServicePipeClient.cs` | `NamedPipeClientStream` | NamedPipes / Client | `IIpcClientTransport` (`UnixDomainSocketClientTransport`) | 3.1-1 / 3.1-3 |
| `ServiceMonitorHost` | `src/IEM.App/Hosting/ServiceMonitorHost.cs` | `ServicePipeClient`, `ServiceContract.IsInstalled()` | Hosting / Installed | `IMonitorHost` (Installed Service Client via `IPlatformInstallationProbe`) | 3.1-1 |
| `InProcessMonitorHost` | `src/IEM.App/Hosting/InProcessMonitorHost.cs` | Direct instantiation of `IEM.Windows` adapters (`BoundPing`, `RouteResolver`, `WlanLinkInspector`, `WindowsCngKeyProvider`, `WindowsSessionAclProvisioner`, `WindowsTimeObservationProvider`, `WindowsReparsePointGuard`) | Hosting / InProcess | `InProcessMonitorHost` decoupled via `IPlatformAdapterFactory` | 3.1-1 |
| `App` | `src/IEM.App/App.xaml.cs` | `System.Windows.Application` (WPF), `System.Threading.Mutex` | WPF Application | Extraction of `IEM.Presentation` (ViewModels & logic) + Avalonia/WPF shells | 3.1-1 / 3.1-11 |

---

## 3. Canonical Layers Verification (`IEM.Core`, `IEM.Evidence`, `IEM.Legal`, `IEM.Verification`, `IEM.Verifier`)

- **`IEM.Core`**: Zero platform coupling found. 100% platform-neutral.
- **`IEM.Evidence`**: Zero platform coupling found. 100% platform-neutral.
- **`IEM.Legal`**: Zero platform coupling found. 100% platform-neutral (`LegalRegistry.cs` is domain regulatory registry, not Windows registry).
- **`IEM.Verification`**: Zero platform coupling found. 100% platform-neutral.
- **`IEM.Verifier`**: Zero platform coupling found. 100% platform-neutral CLI tool.

---

## 4. Unmapped Coupling Status

```text
UNMAPPED_COUPLING: NONE
```

All platform dependencies found in the repository have an exact 1:1 mapping to target abstractions and 3.1-x execution phases established in `ROADMAP-3.1-LINUX-MASTER.md`.
