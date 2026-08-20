# Internet Evidence Monitor 3.1 — Linux Master Architecture & Implementation Plan

> **Status:** Planned / 3.1-0 adapter inventory locked 2026-08-19  
> **Base release:** Internet Evidence Monitor `3.0.0-rc1`  
> **Scope:** Linux enablement + Windows/Linux semantic parity  
> **Principle:** **One Evidence Engine — Multiple Platform Adapters**  
> **Language:** Serbian (technical identifiers remain English)  
> **Target milestone:** `Internet Evidence Monitor 3.1.0-rc1`  
> **Locked decisions:** one master document · Windows→Linux adapter inventory first · default without `CAP_NET_RAW` + explicit capability preflight · `iem-users` + session-owner authorization · rtnetlink path snapshot + per-cell preflight + TOCTOU (241–248) · nl80211 observations only, Core attribution (249–254) · golden parity on canonical meaning, whitelist-by-path (255–260) · app-owned control.sock, StateDirectory 0700, canonical roles, missing-owner fail-closed (261–268) · Tier A = Ubuntu 26.04 / Debian 13 / Fedora 44; three CI lanes; no *-latest; GATE INCOMPLETE ≠ PASS (269–273 with exact .NET SDK pin) · two execution modes only; no systemd --user; installed+down ≠ portable (274–282)

---

# 0. Executive Summary

IEM 3.1 treba da bude **isključivo Linux i cross-platform parity ciklus**.

Ne treba ga mešati sa:
- ISP automatskim profilima,
- BEREC/EU regulatornim profilima,
- novim funkcionalnostima,
- naprednim Wi-Fi congestion funkcijama,
- drugim feature proširenjima.

Sve to treba pomeriti u `3.2+`.

Cilj 3.1 je samo:

> **Da isti Internet Evidence Monitor na Windowsu i Linuxu proizvodi dokaz sa istim kanonskim značenjem, integritetom, kvalitetom, claim semantikom, report semantikom i verifikacionim pravilima.**

Linux nije zaseban proizvod niti fork.

Linux je novi platform adapter nad postojećim dokaznim jezgrom.

Odluke ciklusa su zaključane u §4.0 i §15A. Adapter inventar, preflight, `iem-users` autorizacija i dva execution moda (bez `systemd --user`) više nisu otvorena pitanja — odavde se samo implementiraju.

---

# 1. Osnovni arhitektonski princip

## 1.1 One Evidence Engine — Multiple Platform Adapters

```text
                    ┌──────────────────────────────┐
                    │          IEM.Core            │
                    │ canonical semantics          │
                    │ measurement contracts        │
                    │ analysis / quality / claims  │
                    └──────────────┬───────────────┘
                                   │
             ┌─────────────────────┼─────────────────────┐
             │                     │                     │
       IEM.Evidence           IEM.Storage         IEM.Verification
             │                     │                     │
             └─────────────────────┼─────────────────────┘
                                   │
                         PLATFORM BOUNDARY
                     ┌─────────────┴─────────────┐
                     │                           │
                IEM.Windows                 IEM.Linux
                     │                           │
             Win32 / WLAN / CNG        rtnetlink / nl80211
             Named Pipes               AF_UNIX / SO_PEERCRED
             Windows Service           systemd
             Windows power events      logind D-Bus
                     │                           │
              Windows Host                  Linux Host
                     │                           │
              Service | InProcess          systemd system
                                           | InProcess portable
                     │                           │
                  WPF App                  Avalonia / CLI
```

Linux **nema** `systemd --user`. Portable je isti engine u procesu, ne treći servis (§15A).

Kanonski slojevi nikada ne smeju postati OS-specific.

---

# 2. Šta ostaje zajedničko i zamrznuto

Sledeći domeni ostaju zajednički između Windows i Linux implementacije:

- `IEM.Core`
- `IEM.Evidence`
- `IEM.Storage`
- `IEM.Verification`
- `IEM.Verifier`
- `IEM.Legal`
- canonical measurement semantics
- canonical evidence model
- evidence chain
- manifest
- digital signature contract
- canonical serialization
- analysis
- quality model
- claims
- reports
- cases
- redaction
- verification
- release/evidence trust separation

Postojeće invarijante **1–210 ostaju nepromenjene**.

Linux ne sme da reinterpretira ni jednu postojeću dokaznu semantiku.

---

# 3. Ciljna struktura solution-a

```text
src/
│
├── IEM.Core/
├── IEM.Evidence/
├── IEM.Storage/
├── IEM.Verification/
├── IEM.Verifier/
├── IEM.Legal/
├── IEM.Cli/
│
├── IEM.Presentation/            # NOVO
│   ├── PresentationSnapshot
│   ├── ViewModels
│   ├── SemanticVisualTokens
│   └── Presentation contracts
│
├── IEM.Service.Runtime/         # NOVO
│   ├── MonitorWorker
│   ├── SpeedWorker
│   ├── session orchestration
│   ├── command dispatcher
│   └── lifecycle-neutral runtime
│
├── IEM.Windows/
│   ├── Network/
│   ├── Wifi/
│   ├── Crypto/
│   ├── Storage/
│   ├── Time/
│   ├── Power/
│   └── Ipc/
│
├── IEM.Linux/                   # NOVO
│   ├── Network/
│   ├── Wifi/
│   ├── Crypto/
│   ├── Storage/
│   ├── Time/
│   ├── Power/
│   ├── Ipc/
│   └── Host/
│
├── IEM.Service.Windows/         # tanak Windows host
├── IEM.Service.Linux/           # tanak systemd *system* host (nema --user)
│
├── IEM.App/                     # postojeći Windows WPF
└── IEM.App.Linux/               # Avalonia; portable ide InProcessMonitorHost
```

Napomena:

Postojeći `IEM.Service` ne treba agresivno preimenovati na početku 3.1. Prvo izdvojiti platform-neutral runtime iza characterization testova, pa zatim razdvojiti hostove.

---

# 4. Platform Capability Contracts

## 4.0 Zaključane odluke (2026-08-19)

Ove odluke više nisu predlozi. Sledeći slojevi ovog dokumenta i 3.1 implementacija ih tretiraju kao date.

1. **Jedan master dokument.** Ovaj fajl ostaje izvor istine. Fajlovi iz §41 (`LINUX-ARCHITECTURE.md` i ostali) se izdvajaju tek kada bi izdvajanje smanjilo, a ne povećalo, rizik od divergencije.
2. **Prvi implementacioni sloj je Windows → Linux adapter inventar.** Nema novih kanonskih semantičkih tipova dok svaki postojeći Windows adapter nema imenovan Linux par, ugovor, fallback i test gate.
3. **Default privilege: bez `CAP_NET_RAW`, bez `CAP_NET_ADMIN`, bez root.** Capability se sme dodati samo kao eksplicitan, dokumentovan, preflight-om označen opcioni drop-in. Nedostajuća capability nikada ne postaje mrežni failure, `0`, ni outage.
4. **Autorizacija: `iem-users` + vlasnik sesije.** Socket ACL nije autorizacija (invarijanta 223). `StartSession` i read komande: član `iem-users` (ili `iem-admin` / uid 0). `StopSession` / `FinalizeSession` / `RetryTimestamp` / `CreateExport`: vlasnik sesije, `iem-admin` ili uid 0.
5. **Dva execution moda, nema `--user`.** Installed system service (full) i portable in-process (XDG, slabiji continuity). `systemd --user` je van 3.1. Installed+unreachable ≠ tihi portable.

## 4.1 Pravilo granice

> OS-specifičan kod ne sme biti razbacan po Core / Evidence / Analysis / Storage / Verification slojevima kroz `OperatingSystem.IsWindows()` / `IsLinux()` grananja.

Platform selection se radi **samo** u composition root-u (`IEM.Service.Windows`, `IEM.Service.Linux`, CLI host, test harness).

Kanonski sloj sme da *zabeleži* ime platforme kao provenance (`ManifestBuilder` već radi to). Ne sme da *grana* semantiku po njoj.

## 4.2 Ugovori koji već postoje — ne izmišljati duplikate

Sledeći ugovori su već u jezgru. Linux 3.1 ih implementira, ne zamenjuje.

| Ugovor | Gde živi | Windows implementacija | Linux implementacija |
|---|---|---|---|
| `IRouteResolver` | `IEM.Core.Probes` | `IEM.Windows.RouteResolver` (`GetBestRoute2`) | `IEM.Linux.Network.LinuxRouteResolver` (`RTM_GETROUTE`) |
| `IBoundIcmp` | `IEM.Core.Probes` | `IEM.Windows.BoundPing` (`IcmpSendEcho2Ex`) | `IEM.Linux.Network.LinuxBoundIcmp` (`SOCK_DGRAM` / `IPPROTO_ICMP`) |
| `ILinkInspector` | `IEM.Core.Probes` | `SystemLinkInspector` + `WlanLinkInspector` | `SystemLinkInspector` + `LinuxWifiLinkInspector` |
| `IWirelessRadio` | `IEM.Core.Probes` | `NativeWifiRadio` | `LinuxNl80211Radio` |
| `ITimeObservationProvider` | `IEM.Core.Time` | `WindowsTimeObservationProvider` | `LinuxTimeObservationProvider` |
| `IIpcTransport` | `IEM.Core.Ipc` | `WindowsNamedPipeTransport` | `LinuxUnixDomainSocketTransport` |
| `IEvidenceKeyProvider` | `IEM.Evidence.Crypto` | `WindowsCngKeyProvider` | `LinuxEvidenceKeyProvider` |
| `IEvidenceSigningIdentity` | `IEM.Evidence.Crypto` | `WindowsCngSigningIdentity` | `LinuxEvidenceSigningIdentity` |
| `IStorageProtectionProvider` | `IEM.Storage.Layout` | `WindowsSessionAclProvisioner` | `LinuxSessionModeProvisioner` |
| `ILocalAddressMap` | `IEM.Core.Speed` | `SystemLocalAddressMap` (već portable) | isti tip — nema Linux forka |
| `IClock` | `IEM.Core.Time` | `SystemClock` (već portable) | isti tip — nema Linux forka |
| `WirelessDetailReader` | `IEM.Core.Probes` | nije adapter; tu žive attribution pravila | isti tip, netaknut |

`NullRouteResolver` ostaje test / degradacioni fallback. Na Linuxu se sme koristiti samo kada kernel route lookup nije moguć, uz `PlatformFallbackProvenance` (invarijanta 239).

## 4.3 Ugovori koji se uvode u 3.1

Novi interfejs se uvodi samo kada postojeći ne pokriva ponašanje.

| Novi ugovor | Zašto postojeći nije dovoljan | Windows izvor danas | Linux implementacija |
|---|---|---|---|
| `INetworkChangeObserver` | `RouteResolver.Invalidate()` se zove ad-hoc; nema pretplate | implicitno / polling | `LinuxRtnetlinkObserver` (`RTMGRP_LINK` / `IPv4_ROUTE` / `IPv6_ROUTE` / `NEIGH`) |
| `IPowerEventSource` | `PowerEventBroker` živi u `IEM.Service` sa `OperatingSystem.IsWindows()` | `PowerEventBroker` + SCM power | `LinuxLogindPowerSource` (`PrepareForSleep`) |
| `IPlatformStorageLayout` | `ServiceContract.DefaultOutputRoot` koristi `SpecialFolder.CommonApplicationData` → na Linuxu `/usr/share` | `ProgramData\...\Sesije` ili Desktop portable | **dve** Linux implementacije: `LinuxStorageLayout` (`/var/lib`) i `LinuxPortableStorageLayout` (XDG). Composition bira po modu (§15A) |
| `IPlatformInstallationProbe` | `ServiceContract.IsInstalled()` je boolean + Windows registry; na Linuxu uvek `false` | SCM key | `InstallationPresence` + `ServiceReachability` (§15A.5); **ne** `File.Exists` u shared kodu |
| `ISymlinkSafetyGuard` | `WindowsReparsePointGuard` je Win32 | `WindowsReparsePointGuard` | `LinuxSymlinkGuard` (`lstat`, no-follow) |

### 4.3.1 Šta se namerno ne uvodi

| Predloženo ranije | Odluka | Razlog |
|---|---|---|
| `IBoundProbeExecutor` | **ne u 3.1 default** | TCP / DNS / HTTP već binduju source IP u `IEM.Core` (`FastProbes`, `DnsQuery`, `MeasurementHttpClient`). To je Windows paritet sa `IcmpSendEcho2Ex` + source address. `SO_BINDTODEVICE` je jači od Windowsa i zahteva `CAP_NET_RAW` — ostaje opcioni drop-in, ne default. |
| `IPlatformIdentityProvider` | **ne** | `IIpcTransport` već proizvodi `PlatformPeerIdentity`. Identitet dolazi iz transporta, ne iz drugog providera. |
| `IBootIdentityProvider` | **ne** | `ITimeObservationProvider.CaptureBootObservation` već nosi boot identity. Linux provider čita `/proc/sys/kernel/random/boot_id`. |
| `IWirelessDiagnostics` kao Core ugovor | **ne** | `WirelessDiagnostics` na Windowsu je host dijagnostika, nije measurement contract. Linux dobija `LinuxWirelessDiagnostics` u `IEM.Linux`, istog ranga. |
| systemd `--user` / linger | **ne u 3.1** | Treći lifecycle. System service već pokriva kontinuitet. Vidi §15A.1. |

## 4.4 Windows → Linux adapter inventar

Svaki red je implementacioni ugovor: postojeća Windows klasa, Linux par, kanonski izlaz, privilege, fallback, test gate.

### 4.4.1 Mreža i putanja

| Windows | Linux par | Kanonski izlaz | Kernel / API | Privilege (default) | Fallback ako nije moguće | Gate |
|---|---|---|---|---|---|---|
| `RouteResolver` | `LinuxRouteResolver` | `ProbePath` (`InterfaceId`, `SourceAddress`, `Resolved`) | `NETLINK_ROUTE` `RTM_GETROUTE`; odgovor: `RTA_OIF`, `RTA_PREFSRC` / `RTA_SRC`, `RTA_GATEWAY` | nijedna (`AF_NETLINK` je dozvoljen u unit-u) | `ProbePath.Unresolved` + provenance `RouteLookupUnavailable` | ista destinacija → isti `Resolved`/`Unresolved` ishod kao Windows nad istim tabelarnim činjenicama |
| `RouteResolver` cache 2s | isti TTL | keš nije evidence | in-process | — | invalidate na `INetworkChangeObserver` | route change unutar sesije se vidi u narednom uzorku, ne fabricira outage |
| `MeasurementPath` | `LinuxMeasurementPath` (tanki wrapper) | `MeasurementRoute` preko `SpeedPath.ResolveRoutes` | koristi `IRouteResolver` | nijedna | `MeasurementRoute.Unchecked` (kao Windows kad DNS padne) | pravila ostaju u `SpeedPath`, ne u adapteru |
| `BoundPing` / `IBoundIcmp` | `LinuxBoundIcmp` | `IcmpEcho?` | `Socket(Dgram, ICMP)` / `ICMPV6`; source bind kao Windows | **bez** `CAP_NET_RAW`; `net.ipv4.ping_group_range` mora da pokrije gid `iem` (isti sysctl na savremenom kernelu pušta i `IPPROTO_ICMPV6`) | capability failure → `IcmpEcho` non-timeout (Core → `Skipped`); **ne** `null` (to pali unbound `Ping`) | preflight: socket create; `EPERM` ⇒ `IcmpCapability=Unavailable` |
| `SystemLinkInspector` | isti (portable) | `LinkSnapshot` bez wireless | `NetworkInterface` | nijedna | `LinkSnapshot.Unavailable` | Ethernet / unknown medium ostaje merljiv bez nl80211 |
| — | `LinuxRtnetlinkObserver` | event: link / addr / route / neigh | `RTMGRP_*` multicast | nijedna | reconciliation poll 2s, provenance `NetlinkSubscriptionUnavailable` | event ima prednost; poll nije autoritet |

**Mapiranje polja `ProbePath`:**

```text
Windows GetBestRoute2
    interface LUID/index  →  InterfaceId (adapter.Id)
    preferred source      →  SourceAddress
    status != 0           →  Unresolved

Linux RTM_GETROUTE
    RTA_OIF ifindex       →  InterfaceId (stabilan ifname koji .NET vidi za taj ifindex;
                             ifindex se čuva u platform provenance, ne kao kanonski Id)
    RTA_PREFSRC / SRC     →  SourceAddress
    NLM_F / ENETUNREACH   →  Unresolved
```

Ime interfejsa (`wlp2s0`) sme da se promeni; `ifindex` i MAC su session-identity činjenice (`NetworkEnvironment.MacAddress` već postoji). Route matching ide preko ifindex u adapteru, zatim se preslikava u `InterfaceId` koji `SystemLinkInspector` koristi.

**Forced path (`MeasurementIntent.MeasureRequestedInterface`):**

Windows veže source adresu, ne ifindex. Linux 3.1 default radi isto: `socket.Bind(source)`. Ako tražena adresa nije na traženom interfejsu → `NotExecuted` / `NoRouteFromRequestedInterface`. `SO_BINDTODEVICE` se ne koristi u default unit-u.

### 4.4.2 Wi-Fi

Judgements ostaju u `WirelessDetailReader` (Core). Adapter samo puni `IWirelessRadio`.

| Windows | Linux par | `IWirelessRadio` metoda | nl80211 / sysfs | Fallback |
|---|---|---|---|---|
| `NativeWifiRadio.IsRadioOn` | `LinuxNl80211Radio.IsRadioOn` | `bool?` | rfkill na **tom** wiphy; vidi §7.5 | `null` — nikad `false` zbog odsustva API-ja |
| `ReadAssociation` | isto | `WirelessAssociation?` | `GET_INTERFACE` + associated BSS + `GET_STATION`; §7.6 | `null` = nije povezan **ili** nije poznato; provenance razlikuje |
| `ReadAccessPoint` | isto | `WirelessAccessPoint?` | `GET_SCAN` BSS tačno po BSSID | `null` |
| `IsSsidVisible` | isto | `bool?` | `LinuxWifiScanCache` iz `GET_SCAN`; freshness first-class | `null` ako nema validnog svežeg snapshot-a |
| `RequestUrgentScan` | isto | void | **3.1 default: no-op**. `TRIGGER_SCAN` je opcioni capability | `ScanTrigger=Unsupported` |
| `WlanScanCache` (aktivni scan 45s/8s, max 3 min) | `LinuxWifiScanCache` | isti `MaximumAge` (3 min) | čita kernel BSS keš; ne pokreće scan loop | zastareo/nepotpun keš = unknown, ne „nije vidljiv“ |
| `WlanLinkInspector` | `LinuxWifiLinkInspector` | `LinkSnapshot` + `Wireless` | dekorator; `WirelessDetailReader` ostaje Core | ako medium nije Wireless, inner snapshot netaknut |
| `WindowsLinkInspection.Create` | `LinuxLinkInspection.Create` | composition | bez nl80211: samo `SystemLinkInspector` | Ethernet / no radio ostaje merljiv |
| `WirelessDiagnostics` | `LinuxWirelessDiagnostics` | dijagnostika, nije evidence | enumeracija wiphy/iface/rfkill | prazna lista, ne exception |

`SsidVisibleInScan` i `RadioOn` ostaju nullable. Kršenje invarijante 1 (`UNKNOWN_NEVER_BECOMES_CONFIRMED`) ovde je najskuplje: lažan `RadioOn=false` prebacuje krivicu na korisnika.

NetworkManager D-Bus sme da doda `reason=` u supplementary provenance. Ne sme da popuni `RadioOn`, `SsidVisibleInScan`, BSSID ili route.

### 4.4.3 Vreme, suspend, boot

| Windows | Linux par | Kanonski izlaz | Linux izvor | Fallback |
|---|---|---|---|---|
| `WindowsTimeObservationProvider` wall | `LinuxTimeObservationProvider` | `CapturedUtc`, `WallClockSource` | `clock_gettime(CLOCK_REALTIME)` | `DateTimeOffset.UtcNow` samo ako clock_gettime padne; source se onda menja |
| QPC monotonic | isto | `MonotonicTimestamp` / `Frequency` | `Stopwatch` (`QueryPerformanceCounter` na .NET/Linux = `CLOCK_MONOTONIC`) | — |
| `QueryInterruptTimePrecise` | `BootElapsedIncludingSuspend` | `CLOCK_BOOTTIME` | ako nema: `Unknown` elapsed, ne nagađati |
| `QueryUnbiasedInterruptTimePrecise` | `ActiveElapsedExcludingSuspend` | `CLOCK_MONOTONIC` (ne broji suspend) | ako se izjednači sa boot elapsed: provenance `SuspendSplitUnavailable` |
| `win-boot-{utc-origin}` | `linux-boot-{boot_id}` | `BootInstanceId` | `/proc/sys/kernel/random/boot_id` | bez `boot_id`: `Ambiguous`, ne sintetisati (invarijanta 100) |
| `PowerEventBroker` | `LinuxLogindPowerSource` : `IPowerEventSource` | HostSuspending / HostResumed | logind `PrepareForSleep` | `HostObservabilityGap` / `SuspendSignalUnavailable` |
| SCM service power | systemd notify + isti broker | — | Generic Host `UseSystemd` | — |

`/etc/machine-id` **nije** boot identity. Preživljava reboot i pomešao bi restart sa istim bootom.

Kratki delay inhibitor na `PrepareForSleep(true)` je dozvoljen samo da se zabeleži `HostSuspending` pre spavanja. Inhibitor se pušta odmah posle zapisa. IEM ne sme da sprečava sleep.

### 4.4.4 IPC i identitet

Windows danas ima **dva** kanala:

| Kanal | Ime | Gde | Ko ga koristi |
|---|---|---|---|
| Status pipe | `InternetEvidenceMonitor.status` | `IEM.Service.StatusPipeServer` | `IEM.App` (`ServicePipeClient`) |
| Control pipe | `IEM_Service_Pipe` | `WindowsNamedPipeTransport` | novi `IIpcTransport` ugovor |

Linux **ne ponavlja** tu podelu.

```text
Jedan socket:
  /run/internet-evidence-monitor/control.sock

Jedan transport:
  LinuxUnixDomainSocketTransport : IIpcTransport

Jedan protokol:
  IpcRequestEnvelope / IpcResponseStatus
```

`StatusPipeServer` se u 3.1-1 izdvaja iza `IIpcTransport`. Windows sme privremeno da zadrži stari status pipe zbog 3.0 klijenata. Linux kreće sa jednim socketom.

| Windows | Linux | Beleška |
|---|---|---|
| Named pipe ACL (Users ReadWrite) | UDS `0660` `iem:iem-users` | prvi filter, nije autorizacija |
| `GetImpersonationUserName` → SID | `SO_PEERCRED` → uid, gid, pid | payload se ignoriše |
| claims ad-hoc (`Admin` substring) | strukturirani claims, vidi §4.6 | `IpcAuthorizationPolicy` se u 3.1-3 prebacuje na `role:admin` / `role:operator` bez promene semantičke matrice |
| `PlatformPeerIdentity.CreateWindows` | `CreateUnix` + supplementary groups | kanonski autoritet redosled: `SO_PEERCRED` za uid/gid/pid → `SO_PEERGROUPS` za stvarne grupe peer procesa na konekciji → `/proc/<pid>/status` kontrolisani fallback → fail-closed za zavisne role ako se grupe ne mogu pouzdano utvrditi. `getgrouplist(uid)` ostaje samo dijagnostika (account baza), ne autoritet za autorizaciju |

Stale socket čišćenje je **isključivo po proceduri iz §11.3** (`lstat`, mora biti `S_IFSOCK`, ne symlink, owner mora biti `iem`, pokušaj `connect()`, i tek za očekivani stale inode nakon `ECONNREFUSED`/`ENOTCONN` sme kontrolisani `unlink` u očekivanom runtime dir-u; nikad naslepo i nikad za tuđi/nepoznati inode).

### 4.4.5 Storage, zaštita, ključevi

| Windows | Linux par | Kanonski efekat | Linux mehanizam | Fallback |
|---|---|---|---|---|
| `ServiceContract.DefaultOutputRoot` (`CommonApplicationData`) | `LinuxStorageLayout.SessionsRoot` | session folders | `/var/lib/internet-evidence-monitor/sessions` | fail-closed ako StateDirectory nije zapisan |
| `ServiceContract.IsInstalled()` registry | `LinuxInstallationProbe` | `Presence` + `Reachability` | unit file ≠ socket healthy | installed+down → `ServiceUnavailable`, **ne** portable |
| `WindowsSessionAclProvisioner` | `LinuxSessionModeProvisioner` | `StorageProtectionObservation` | owner/mode, ne DACL | `NotEstablished` sprečava start sesije (invarijanta 81) |
| `WindowsReparsePointGuard` | `LinuxSymlinkGuard` | odbij symlink/mount-preko-putanje | `lstat` / `O_NOFOLLOW` na svakom segmentu | violation → ne piši |
| DACL: System/Admin Full, Users Read, Exports Modify | mode matrica ispod | **nije** bit-po-bit ekvivalent: system store je stroži (`0700`, GUI nema ACL). Portable je XDG (stroga validacija apsolutnih putanja), user-owned (§15A) | POSIX | — |
| `WindowsCngKeyProvider` | `LinuxEvidenceKeyProvider` | `IEvidenceSigningIdentity` | PKCS#8 ECDSA P-256, `0600` `iem:iem` | postojeći ključ neotvoriv → `SigningIdentityUnavailableException` |
| TPM-first pa software | samo software u 3.1 | `KeyProtectionLevel.SoftwareProtected` | filesystem ACL | TPM2 je 3.2+; prisustvo `/dev/tpm0` **nije** `TpmBacked` |

**POSIX matrica zona** (3.1 baseline, stroža od ranijeg nacrta sa `0750`/`iem-users` read):

Kanonski store je **samo servis**. GUI ne čita i ne piše `/var/lib/...` preko ACL-a. Export je IPC komanda: u Installed modu servis kreira verifikovani export paket u service-owned staging-u, klijent ga preuzima preko IPC-a (bounded/chunked stream) i sam klijentski proces upisuje finalni fajl na korisnički izabranu putanju (servis ne piše u `~/Documents/...` direktno, čime `ProtectHome=yes` ostaje čist). Detalj i lifecycle su u §15 i §15A.

```text
/var/lib/internet-evidence-monitor/              iem:iem         0700
  keys/                                          iem:iem         0700
    evidence-signing-v1.p8                       iem:iem         0600
  sessions/<id>/                                 iem:iem         0700
    Raw/  Evidence/  Derived/  Exports/          iem:iem         0700
  cases/                                         iem:iem         0700
  state/                                         iem:iem         0700

/run/internet-evidence-monitor/                  iem:iem-users   0750
  control.sock                                   iem:iem-users   0660

/etc/internet-evidence-monitor/appsettings.json  root:iem        0640
```

### 4.4.6 Host, servis, prezentacija

Ovo nisu measurement adapteri, ali su Windows-only host komadi koje 3.1 mora da razdvoji.

| Windows host komad | 3.1 radnja | Linux par |
|---|---|---|
| `IEM.Service` + `IemWindowsServiceLifetime` | izdvojiti runtime; ostaviti tanak Windows host | `IEM.Service.Linux` + `UseSystemd` |
| `PowerEventBroker` OS grananje | iza `IPowerEventSource` | `LinuxLogindPowerSource` |
| `StatusPipeServer` early-return na `!IsWindows()` | iza `IIpcTransport` | isti socket kao control |
| `ServiceInstaller` (install/uninstall verbs) | ostaje Windows | `internet-evidence-monitor.service` + maintainer scripts |
| `IEM.App` WPF | ostaje; ViewModels u `IEM.Presentation` | `IEM.App.Linux` Avalonia (3.1-9/10) |
| `ServicePipeClient` named pipe | apstrakcija klijenta | UDS klijent, isti envelope |
| Tray / `SystemEvents` | Windows-only UI | Linux: opciono StatusNotifierItem kasnije; nije 3.1 blocker |

### 4.4.7 Curenja koja nisu adapteri

Ovo su `OperatingSystem.IsWindows()` / SpecialFolder / Registry tačke koje 3.1-0 mora da inventariše i 3.1-1 da izbaci iz kanonskih projekata.

| Mesto | Šta radi danas | Zašto je problem | 3.1 cilj |
|---|---|---|---|
| `IEM.Storage.ServiceContract.DefaultOutputRoot` | `SpecialFolder.CommonApplicationData` | na Linuxu `/usr/share/...` — read-only, pogrešan tree | `IPlatformStorageLayout` |
| `IEM.Storage.ServiceContract.IsInstalled` | `Registry.LocalMachine\...\Services\...` | na Linuxu uvek `false`; boolean meša presence i reachability | `IPlatformInstallationProbe` (§15A.5) |
| `IEM.Storage.ServiceContract.PortableOutputRoot` | Desktop `\InternetEvidence` | Desktop nije XDG; na Linuxu često prazan | `$XDG_STATE_HOME/internet-evidence-monitor` (§15A.3); Desktop/Documents samo export copy |
| `IEM.Service.Program` | management verbs samo na Windows | Linux CLI verbs moraju da rade preko UDS | verbs u `IEM.Cli` / host-neutral |
| `IEM.Service.Program` | `ConfigureWindowsService` iza `IsWindows()` | composition root sme ovo; izdvojiti u Windows host | Linux host zove `UseSystemd` |
| `IEM.Service.StatusPipeServer` | `if (!IsWindows()) return` | Linux GUI ostaje bez status kanala | `IIpcTransport` |
| `IEM.Service.PowerEventBroker` | `if (!IsWindows())` preskače desktop events | Linux suspend ostaje neprimećen | `IPowerEventSource` |
| `IEM.Evidence.ManifestBuilder` | zapisuje `"Windows"` / `"Linux"` | **dozvoljeno** — provenance, ne semantika | ostaje |
| `IEM.Windows.csproj` | `net10.0-windows` | Linux CI ne sme da mora da ga kompajlira | `IEM.Linux` je `net10.0`; Windows ostaje TFM-windows |

## 4.5 Capability preflight

Default unit **nema** `CAP_NET_RAW`, `CAP_NET_ADMIN`, niti `AmbientCapabilities`.

Pri startu servisa, pre prve sesije, `LinuxCapabilityPreflight` meri šta je stvarno moguće i zapisuje `PlatformCapabilityObservation` (provenance, ne verdict).

Mrežne ćelije nisu jedan boolean. Tačna matrica `(ProbeKind × AddressFamily × ExecutionPath)`, ICMP datagram, `ping_group_range`, bind i TOCTOU rute zaključani su u §5.

```text
za svaku sposobnost:
  Success
  Unavailable          (kernel/API nema, ili EPERM)
  Unsupported          (distro/kernel namerno nema)
  PermissionDenied     (postoji, ali ovaj nalog ne sme)
  TemporarilyUnavailable
  ProviderFailure
  Unknown
```

Obavezne preflight stavke:

| Sposobnost | Kako se proverava | Default očekivanje | Ako padne |
|---|---|---|---|
| ICMP IPv4 | `socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)` | Success ako je `ping_group_range` pokrio `iem` | ICMP probe `NotExecuted`; TCP/DNS/HTTP nastavljaju |
| ICMP IPv6 | `SOCK_DGRAM` / `IPPROTO_ICMPV6` | isto | isto, po familiji |
| Source bind | bind na lokalnu unicast adresu | Success | `NotExecuted` / `NoRouteFromRequestedInterface` |
| `SO_BINDTODEVICE` | **ne radi se** u default | `Unsupported` (namerno) | — |
| Netlink route | `RTM_GETROUTE` dump jedne destinacije | Success | `ProbePath.Unresolved` |
| nl80211 | `NL80211_CMD_GET_INTERFACE` | Success ili Unsupported na Ethernet-only | Wi-Fi enrichment absent |
| logind | D-Bus `login1.Manager` | Success na desktop/systemd | `SuspendSignalUnavailable` |
| `CLOCK_BOOTTIME` | `clock_gettime` | Success | suspend split unknown |
| `/proc/sys/kernel/random/boot_id` | read | Success | boot identity Ambiguous |
| UDS bind | bind + `SO_PEERCRED` get | Success (inače servis ne sme da tvrdi da sluša) | fail-closed start |
| Key file | open `0600` key ili first-provision | Success / first-run create | `SigningIdentityUnavailableException` |

Paket **sme** da isporuči `sysctl.d` drop-in za unprivileged ICMP datagram sokete:

```text
net.ipv4.ping_group_range = <iem_gid> <iem_gid>
```

Debian/RPM paketi sistemske UID/GID vrednosti alociraju dinamički (npr. `adduser --system --group iem`), pa servisni nalog `iem` nema jedan fiksni/hardkodovani broj na svim sistemima. Zato `.deb`/`.rpm` paket ne sme imati statički `ping_group_range = 123 123`. Paket tokom instalacije (nakon kreiranja/rezolvovanja `iem` grupe) dinamički i idempotentno generiše `/etc/sysctl.d/99-internet-evidence-monitor.conf` sa stvarnim numeričkim GID-om. To nije capability već unprivileged ICMP, i to je 3.1 default put. Stvarni `socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)` poziv na preflight-u ostaje jedini runtime autoritet.

Opcioni hardened drop-in (`internet-evidence-monitor-netraw.conf`):

```ini
[Service]
CapabilityBoundingSet=CAP_NET_RAW
AmbientCapabilities=CAP_NET_RAW
```

sme da postoji u `3.1`, ali:

- nije deo default paketa enable-a,
- preflight mora da zabeleži `BindDeviceCapability=Present` / `IcmpRaw=Present`,
- evidence i report smeju da kažu samo da je capability bila prisutna, nikad da je merenje „pouzdanije“ zbog toga (invarijanta 232).

## 4.6 Linux authorization matrix

### Nalozi i grupe

```text
User  iem          nologin, home=/var/lib/internet-evidence-monitor
Group iem          servisni nalog
Group iem-users    klijenti (GUI, CLI)
Group iem-admin    Stop/Finalize/Export bez vlasništva sesije
```

Maintainer skripte (`postinst`) u `.deb`/`.rpm` paketima su **striktno neinteraktivne**: paket kreira sistemskog korisnika `iem` i grupe `iem`, `iem-users`, `iem-admin`. Članstvo konkretnog desktop korisnika u `iem-users` dodaje se kroz eksplicitnu installer/setup radnju kada je identitet korisnika poznat, ili kroz standardno post-install uputstvo. Novo članstvo u supplementary grupama postaje efektivno nakon novog logina/sesije korisnika.

### Identitet (authentication provenance)

```text
SO_PEERCRED (uid, gid, pid sa kernela)
        ↓
SO_PEERGROUPS (stvarne supplementary grupe peer procesa na konekciji)
        ↓  (fallback: /proc/<pid>/status Groups; getgrouplist(uid) je samo dijagnostika)
PlatformPeerIdentity.CreateUnix(
    uid,
    gid,
    pid,
    claims:
      role:operator     ako je član iem-users
      role:admin        ako je uid==0 ili član iem-admin
      group:iem-users   …
      group:iem-admin   …
      gid:{gid}
)
```

Ako se peer grupe ne mogu pouzdano utvrditi, sistem ide u **fail-closed** za sve role koje zavise od njih (`role:operator`, `role:admin` preko `iem-admin`).

Klijentski payload koji tvrdi uid/role se ignoriše.

`IpcAuthorizationPolicy` u 3.1-3 ide na `PolicyVersion = 2` na **obe** platforme:

- prestaje `Contains("Admin")` / `Contains("root")`; gleda tačan claim `role:admin` / `role:operator`;
- Windows transport emituje iste role (Administrators → `role:admin`, authenticated pipe user → `role:operator`);
- **rupa se zatvara:** `string.IsNullOrEmpty(sessionOwnerPrincipalRef)` više **nije** Allow. Missing owner → fail-closed za session-control komande.

Ovo je shared-core security fix, ne Linux-only semantika. Characterization testovi koji su računali na praznog vlasnika moraju da se ažuriraju.

Pet slojeva i komande: §11.4.

## 4.7 Composition root

```text
Zajednički (oba moda, isti runtime):
    IRouteResolver              → LinuxRouteResolver
    IBoundIcmp                  → LinuxBoundIcmp
    ILinkInspector              → LinuxLinkInspection.Create(...).Inspector
    IWirelessRadio              → LinuxNl80211Radio
    INetworkChangeObserver      → LinuxRtnetlinkObserver
    ITimeObservationProvider    → LinuxTimeObservationProvider
    IPowerEventSource           → LinuxLogindPowerSource
    IStorageProtectionProvider  → LinuxSessionModeProvisioner
    IPlatformInstallationProbe  → LinuxInstallationProbe
    ISymlinkSafetyGuard         → LinuxSymlinkGuard
    ILocalAddressMap            → SystemLocalAddressMap
    IClock                      → SystemClock

Samo Installed (IEM.Service.Linux):
    IIpcTransport               → LinuxUnixDomainSocketTransport
    IEvidenceKeyProvider        → SystemInstallationIdentity / LinuxEvidenceKeyProvider
    IPlatformStorageLayout      → LinuxStorageLayout            (/var/lib, /run)

Samo Portable (InProcessMonitorHost):
    IIpcTransport               → nema (nema control.sock)
    IEvidenceKeyProvider        → PortableUserIdentity
    IPlatformStorageLayout      → LinuxPortableStorageLayout    (XDG)
```

Nijedan od ovih tipova se ne registruje iz `IEM.Core` / `IEM.Evidence` / `IEM.Storage` / `IEM.Verification`. Nema trećeg composition root-a za `systemd --user`.

## 4.8 Gate za 3.1-0

3.1-0 je zatvoren tek kada:

- ovaj inventar ima par za svaki Windows adapter iz `src/IEM.Windows/`,
- svako curenje iz §4.4.7 ima imenovan ciljni ugovor,
- characterization testovi 3.0 i dalje prolaze bez izmene ponašanja,
- architecture test skelet zabranjuje `IEM.Core` → `IEM.Windows` / `IEM.Linux`.

---

# 5. Linux Networking Architecture — rtnetlink + probe preflight

Zaključano 2026-08-19. Ovo je fact layer: šta IEM zna o measurement path-u **pre** izvršenja probe, i šta sme da tvrdi **posle**.

nl80211 i golden parity se ne otvaraju dok ovaj sloj ne stoji. Oni nasleđuju `ProbePath`, preflight stanja i TOCTOU kontinuitet odavde.

## 5.1 Kernel je primarni autoritet

Kanonski network facts dolaze iz `NETLINK_ROUTE`, ne iz parsiranja CLI izlaza.

Ne koristiti kao evidence authority:

```bash
ip route
ip addr
nmcli
iw
ping
```

Ti alati smeju da postoje u troubleshooting dokumentaciji. Nijedan bajt njihovog stdout-a ne ulazi u `ProbePath`, preflight, ni raw evidence.

Implementacija je ručni netlink u `IEM.Linux` (P/Invoke + `SafeHandle`), bez `libnl`. Ulaz sa kernela je spoljašnji input: bounds check, poravnanje atributa, odbacivanje sakaćenih `nlmsg` (§35).

## 5.2 Šta ovaj sloj ne sme da uradi

- Ne sme da uvede globalni boolean `LinuxNetworkingWorks`.
- Ne sme da pretvori `PermissionDenied` / `Unsupported` / `Unavailable` / `ProviderFailure` u network failure, loss, `0 ms`, ni outage.
- Ne sme da tvrdi da je probe `Bound` ako source bind nije uspeo.
- Ne sme da posle izvršenja prepravi `ProbePath.Resolved` unazad jer se ruta promenila.
- Ne sme da izjednači IPv4 i IPv6 capability.
- Ne sme da izjednači ICMP sa TCP/DNS/HTTP.
- Ne sme da proglasi `PathContinuity.Held` ako multicast pretplata nije uspela.

## 5.3 Kanonski tipovi koje ovaj sloj puni — ne zamenjuje

Postojeći Core ugovor ostaje izvor značenja.

| Tip | Polje | Značenje ostaje |
|---|---|---|
| `ProbePath` | `InterfaceId` | adapter koji je kernel izabrao u trenutku resolve-a |
| `ProbePath` | `SourceAddress` | `RTA_PREFSRC` (preferred source), ili `null` |
| `ProbePath` | `Resolved` | lookup je uspeo **pre** izvršenja. Nikad se ne vraća na `false` zbog kasnijeg eventa |
| `ProbePath` | `Bound` | soket je stvarno bindovan na taj source. Predikcija nije bind |
| `ProbePath.ProvesLink` | `Resolved && InterfaceId != null` | resolve-time tvrdnja; **nije** garancija da je ista ruta držala tokom slanja |
| `ProbeResult.Outcome` | `Success` / `Failed` / `TimedOut` / `Skipped` | činjenica izvršenja |
| `ProbeResult.WasAttempted` | `Outcome != Skipped` | samo ovo ulazi u `ProbeTally` |
| `ProbeTally.IsSilent` | `Attempted == 0` | familija nije rekla ništa; quorum ide na ostale familije |
| `ProbeCycle.AnyExternalReachability` | OR svežih uspeha ICMP ∨ TCP ∨ TLS ∨ HTTP ∨ public DNS | ICMP skip ne gasi reachability |

Linux dodaje **provenance pored** `ProbePath`, ne umesto njega. Vidi §5.10.

## 5.4 Dva netlink soketa

```text
LinuxNetlinkRouteSocket          query, request/response
LinuxRtnetlinkObserver           multicast events + generation counter
        │
        ▼
LinuxIfindexMap                  ifindex → {IfName, InterfaceId, Mac, OperState, Kind}
LinuxAddrCache                   ifindex → unicast adrese (bez tentative)
LinuxRouteResolver : IRouteResolver
LinuxCapabilityPreflight         po (ProbeKind × AddressFamily × ExecutionPath)
```

Query i observer se ne mešaju na istom fd: `nlmsg_seq` matching na query soketu ne sme da se sudara sa multicast porukama.

Oba: `socket(AF_NETLINK, SOCK_RAW | SOCK_CLOEXEC, NETLINK_ROUTE)`.

Default unit već dozvoljava `AF_NETLINK`. `CAP_NET_ADMIN` **nije** očekivani zahtev za receive-only rtnetlink observation. Jezgro za `NETLINK_ROUTE` ima `NL_CFG_F_NONROOT_RECV`: neprivilegovani multicast receive je dozvoljen kada familija tako kaže. Observer to **meri**, ne pretpostavlja iz UID-a. Ako konkretan kernel, LSM ili socket setup odbije pretplatu ili event stream postane nepouzdan, prelaz je na polling (§5.7.3).

## 5.5 Query: RTM_GETROUTE / GETLINK / GETADDR

### 5.5.1 RTM_GETROUTE — fib lookup, ne dump tabele

Svaki `IRouteResolver.Resolve(destination)` šalje **jedan** `RTM_GETROUTE` za tu destinaciju. To je Linux ekvivalent `GetBestRoute2`: pita kernel koju rutu bi **sada** izabrao, ne „koje rute postoje“.

Ne sme:

- `NLM_F_DUMP` cele tabele pa birati najduži prefiks u userspace,
- čitati samo `table 254` (main) i ignorisati policy routing,
- pretpostaviti default rutu ako destinacija nema match.

Poruka (IPv4 primer):

```text
nlmsghdr
    nlmsg_type  = RTM_GETROUTE          (26)
    nlmsg_flags = NLM_F_REQUEST
    nlmsg_seq   = monotonic per-socket counter
    nlmsg_pid   = 0

rtmsg
    rtm_family  = AF_INET               (2)   | AF_INET6 (10)
    rtm_dst_len = 32                    | 128
    rtm_src_len = 0
    rtm_tos     = 0
    rtm_table   = RT_TABLE_UNSPEC (0)   // kernel radi fib lookup, uključujući ip rule
    rtm_protocol, rtm_scope, rtm_type, rtm_flags = 0

rta
    RTA_DST     = 4 ili 16 bajtova destinacije
```

IPv4 i IPv6 su **odvojeni upiti**. Jedan `Resolve` poziv, jedna familija — `IPAddress.AddressFamily` odlučuje.

Odgovor: prvi `RTM_NEWROUTE` čiji je `nlmsg_seq` jednak zahtevu. `NLMSG_ERROR` sa `-ENETUNREACH` / `-ESRCH` / `-EHOSTUNREACH` → `ProbePath.Unresolved`. To **nije** network outage; to je „nema šta da se veže za path“.

### 5.5.2 Mapiranje atributa → ProbePath + RouteResolutionObservation

| Atribut | Kanonsko polje | Provenance polje | Pravilo |
|---|---|---|---|
| `RTA_OIF` | `ProbePath.InterfaceId` preko `LinuxIfindexMap` | `Ifindex` | bez OIF → ne tvrdi se link |
| `RTA_PREFSRC` | `ProbePath.SourceAddress` | `PrefSrc` | preferred source; ovo se binduje |
| `RTA_SRC` | — | `FibSrc` | nije bind target ako PREFSRC postoji |
| `RTA_GATEWAY` | — | `Gateway` | nije deo `ProbePath` |
| `RTA_PRIORITY` | — | `Priority` | metric |
| `RTA_TABLE` ili `rtm_table` | — | `TableId` | 254 = main, 253 = default, ostalo = policy |
| `rtm_type` | — | `RouteType` | `RTN_UNICAST`, `RTN_LOCAL`, `RTN_BLACKHOLE`, … |
| `rtm_protocol` | — | `RouteProtocol` | kernel / ra / static / dhcp |
| `RTA_MULTIPATH` | vidi §5.11 | `Multipath` | |

`Resolved = true` samo kada postoji bar `RTA_OIF` ili `RTA_PREFSRC`.

```text
OIF + PREFSRC     → Resolved=true, InterfaceId=map(OIF), SourceAddress=PREFSRC
OIF, nema PREFSRC → Resolved=true, InterfaceId=map(OIF), SourceAddress=null, Bound nemoguć
PREFSRC, nema OIF → Resolved=true, InterfaceId=null, SourceAddress=PREFSRC
                    ProvesLink=false; bind je moguć, atribucija linka nije
ništa            → Unresolved
RTN_BLACKHOLE /
RTN_UNREACHABLE /
RTN_PROHIBIT      → Unresolved + provenance RouteType=…
ENETUNREACH       → Unresolved + provenance NetlinkError=ENETUNREACH
```

`InterfaceId` mora biti **isti string** koji `SystemLinkInspector` stavlja u `LinkSnapshot.InterfaceId`. Mapiranje:

```text
ifindex
  → IFLA_IFNAME (wlp2s0)
  → NetworkInterface.GetAllNetworkInterfaces()
       match Name ili Id
  → ProbePath.InterfaceId = ni.Id          // .NET-ov Id, da se slaže sa LinkSnapshot
  → ako nema .NET match: IfName, provenance IfindexUnmappedToDotNet
```

ifindex ostaje u provenance. Ime sme da se promeni; generation se tada povećava.

### 5.5.3 RTM_GETLINK — ifindex mapa

Dump pri startu (`NLM_F_DUMP`, `RTM_GETLINK`) i refresh na `NEWLINK`/`DELLINK`.

Čita se:

```text
ifi_index, ifi_flags (IFF_UP, IFF_RUNNING, IFF_LOWER_UP)
IFLA_IFNAME
IFLA_ADDRESS          → MAC
IFLA_OPERSTATE        → IF_OPER_UP / DORMANT / DOWN / UNKNOWN
IFLA_LINKINFO
    IFLA_INFO_KIND    → wireguard | tun | tap | veth | bridge | vlan | …
```

`Kind` je signal za `TunnelIndication` (već inference u 3.0). Prefix imena (`wg0`, `tun0`) ostaje slabi signal. `IFLA_INFO_KIND` je primarni.

`DELLINK` briše ifindex iz mape. Sledeći `Resolve` koji ga vrati ne sme da reciklira staro ime.

### 5.5.4 RTM_GETADDR — validacija prefsrc

Dump pri startu i refresh na `NEWADDR`/`DELADDR`.

Čita se: `ifa_index`, `ifa_family`, `IFA_ADDRESS` / `IFA_LOCAL`, `ifa_flags` / `IFA_FLAGS`.

Ne koristiti kao bind source:

- `IFA_F_TENTATIVE` (IPv6 DAD nije gotov),
- `IFA_F_DADFAILED`,
- `IFA_F_DEPRECATED` sme, ali provenance `DeprecatedPrefSrc`,
- link-local IPv6 kao prefsrc za globalnu destinaciju — samo ako je kernel baš to vratio; ne izmišljati.

Pre bind-a: ako `PREFSRC` više nije u `LinuxAddrCache` za taj ifindex → ne bindovati, `Bound=false`, provenance `PrefSrcGone`. Probe sme da se izvrši.

### 5.5.5 Keš 2 s, invalidacija odmah

Isti TTL kao Windows `RouteResolver` (2 s). Keš **nije** evidence.

Ključ keša: `(AddressFamily, destination bytes)`.

Invalidacija:

- svaki matching observer event (§5.7.2),
- `INetworkChangeObserver` signal,
- resume iz suspenda,
- ifindex remap.

Keširani `ProbePath` nosi `RouteGeneration` iz trenutka upisa. Ako se generation promenio, keš se ne sme vratiti.

## 5.6 Source-address bind — kanonski 3.1 parity mehanizam

Windows `IcmpSendEcho2Ex` i Core `socket.Bind(source)` vežu **adresu**, ne ifindex. Linux 3.1 default radi isto.

```text
Resolve(dest)  →  PREFSRC
                     │
                     ▼
            socket.Bind(PREFSRC, port 0)
                     │
          uspeh                    neuspeh
             │                         │
        Path.Bound=true          Path.Bound=false
        probe ide                probe sme da ide UNBOUND
                                 ili da se Skip-uje
                                 ZAVISNO OD RAZLOGA (§5.9)
```

`SO_BINDTODEVICE` se ne koristi. To bi bilo jače od Windowsa i zahtevalo `CAP_NET_RAW`.

Bind i route lookup su odvojeni koraci:

| Situacija | Resolved | Bound | Probe |
|---|---|---|---|
| Lookup uspeo, bind uspeo | true | true | izvršava se |
| Lookup uspeo, bind `EADDRNOTAVAIL` | true | false | TCP/DNS/HTTP: izvrši unbound; ICMP: vidi §5.9 |
| Lookup uspeo, nema PREFSRC | true | false | izvrši unbound |
| Lookup neuspeo | false | false | izvrši unbound ako familija sme; bez atribucije |
| Bind `EACCES` / `EPERM` | true | false | `Skipped` / `PermissionDenied`, ne Failed |

**Core hardening (3.1-5, obe platforme):** `FastProbes.TcpConnectAsync` danas hvata `SocketException` na celom `Bind+Connect` i vraća `Failed`. Bind greška mora da postane `Skipped` (local execution), ne network failure. Invarijante 33 i 55 već to zahtevaju; Linux ih čini neizbežnim.

**ICMP i `IBoundIcmp`:** `SendAsync` vraća `null` samo kada adapter ne može da pokuša (pogrešna familija). Capability failure **ne sme** da vrati `null`, jer `FastProbes.IcmpAsync` tada pada na unbound `Ping` i `BoundIfSourced` može da stavi `Bound=true` iako bind nije bio. Linux vraća `IcmpEcho(Succeeded=false, TimedOut=false, Status=errno)`. Postojeći mapping u `FastProbes` to pretvara u `Skipped`.

## 5.7 Observer: NEW/DEL LINK, ADDR, ROUTE

### 5.7.1 Multicast grupe

```text
setsockopt(SOL_NETLINK, NETLINK_ADD_MEMBERSHIP):

RTNLGRP_LINK            (1)     NEWLINK / DELLINK
RTNLGRP_NEIGH           (3)     NEWNEIGH / DELNEIGH     // reconcil., nije path authority
RTNLGRP_IPV4_IFADDR     (5)     NEWADDR / DELADDR
RTNLGRP_IPV4_ROUTE      (7)     NEWROUTE / DELROUTE
RTNLGRP_IPV6_IFADDR     (9)
RTNLGRP_IPV6_ROUTE      (11)
```

Poruke: `RTM_NEWLINK` (16), `DELLINK` (17), `NEWADDR` (20), `DELADDR` (21), `NEWROUTE` (24), `DELROUTE` (25), plus `NEWNEIGH`/`DELNEIGH` za dijagnostiku.

`RTM_GET*` se ne šalje na ovaj soket.

### 5.7.2 Route generation — TOCTOU osnova

Observer drži `ulong RouteGeneration` (počinje od 1). Inkrementira se kada event **može** da promeni measurement path:

```text
NEW/DEL ROUTE     bilo koja familija, bilo koja tabela
NEW/DEL ADDR      unicast na ifindex koji je u IfindexMap
NEW/DEL LINK      UP/LOWER_UP/name/kind promena, ili nestanak
```

NE inkrementira se za:

- `NEWNEIGH` / `DELNEIGH` (promena ARP/ND keša nije promena rute),
- link statistiku (`RTM_NEWLINK` samo sa IFLA_STATS),
- multicast / anycast adrese.

Svaki `Resolve` upisuje:

```text
RouteResolutionObservation
    Destination
    AddressFamily
    ResolvedAtMonotonicTicks
    RouteGenerationAtResolve
    NlMsgSeq
    Ifindex, PrefSrc, Gateway, TableId, Priority, RouteType
    Multipath (bool, NexthopCount, DistinctIfindexCount)
    InterfaceId
```

Ovo je **snapshot pre izvršenja**. Nije garancija puta tokom slanja.

### 5.7.3 Runtime detection, ne teorija privilegija

`INetworkChangeObserver` **prvo** pokušava kernel multicast subscription za grupe iz §5.7.1.

```text
bind NETLINK_ROUTE
  → NETLINK_ADD_MEMBERSHIP za svaku grupu posebno
  → čekaj prvi validan event ili kratak self-check

Live kad:
  membership uspeo ZA GRUPE koje nose path (LINK, IPv4/IPv6 ROUTE, IPv4/IPv6 IFADDR)
  i stream nije prekinut

Inače:
  ObserverMode = PollingReconciliation
  Continuity   = Unknown za svaku probu     // NIKAD Held
```

Membership se provera **po grupi**. Uspeh na `RTNLGRP_LINK` a neuspeh na `RTNLGRP_IPV4_ROUTE` znači Live samo za link/addr, ne za rute. `Held` sme samo ako su path-relevantne grupe stvarno pretplaćene.

Razlozi prelaska na polling (svi se mere, nijedan se ne pretpostavlja):

```text
ADD_MEMBERSHIP errno          → NetlinkSubscription=<errno name>
stream prekinut / ENOBUFS     → NetlinkSubscription=Unreliable
nema eventa i dump se razilazi sa pretplatom
                              → NetlinkSubscription=Unreliable
```

Dokument **ne sme** da tvrdi da je `CAP_NET_ADMIN` normalan zahtev za ovaj receive path. Ako membership uspe kao `iem` bez extra cap-ova — a to je očekivani ishod na kernelu sa `NL_CFG_F_NONROOT_RECV` — observer ostaje Live. Ako ne uspe, to je runtime capability failure te grupe, ne dokaz da „Linux uvek traži CAP_NET_ADMIN“.

Polling: `RTM_GETLINK` + `RTM_GETADDR` (+ jeftin `RTM_GETROUTE` samo kad treba reconcilovati) svake 2 s. Promena invalidira keš i diže generation. Polling **ne sme** da tvrdi da između dve probe nije bilo rute — zato Continuity ostaje `Unknown`.

`PathContinuity.Unknown` **ne sme** da oslabi 3.0 atribuciju (invarijanta 247). Samo dokazan `ChangedDuringExecution` smanjuje atribuciju.

## 5.8 TOCTOU semantika rute

```text
t0  RTM_GETROUTE                 ResolvedBeforeExecution
    zabeleži generation G0, ticks T0
        │
        │   ← kritični prozor
        │
t1  probe execution              Outcome je činjenica
    CompletedAtTicks T1
        │
t2  uporedi observer evente
    čiji je monotonic ∈ [T0, T1]
    i koji se poklapaju sa destinacijom / ifindex / familijom
        │
        ├── nema matching event, observer Live
        │       PathContinuity = Held
        │
        ├── matching event u prozoru
        │       PathContinuity = ChangedDuringExecution
        │       PathAttribution = Indeterminate
        │       ProbeResult.Outcome SE NE MENJA
        │       ProbePath.Resolved SE NE MENJA
        │
        └── observer nije Live
                PathContinuity = Unknown
                atribucija kao u 3.0
```

Matching event:

| Event | Match ako |
|---|---|
| `NEWROUTE` / `DELROUTE` | ista `rtm_family` i (prefiks pokriva destinaciju ili `dst_len=0` default) |
| `NEWADDR` / `DELADDR` | isti ifindex kao `RTA_OIF`, ili adresa == PREFSRC |
| `NEWLINK` / `DELLINK` | isti ifindex, i promena UP / LOWER_UP / imena / kind, ili nestanak |

Default-route zamena (`dst_len=0`) tokom sesije je matching event za **svaku** destinaciju te familije u kritičnom prozoru.

Posledice:

```text
ChangedDuringExecution
    Path.Resolved ostaje true          // to je bilo tačno u t0
    Path.Bound ostaje kako jeste       // bind se desio ili nije
    ProvesLink ostaje resolve-time
    PathAttribution = Indeterminate
    Quality: Reduced, ne Invalid
    Classifier: rezultat probe i dalje ulazi u tally
    Claim o „ovaj konkretan link je kriv“ se ne sme izvesti iz tog uzorka
```

Novi (opciono na `ProbePath` ili sidecar, default `Unknown` da Windows ostane netaknut):

```text
enum PathContinuity { Unknown, Held, ChangedDuringExecution }

enum PathAttribution
{
    None,             // !Resolved
    Predicted,        // Resolved && !Bound
    Attributed,       // Resolved && Bound && (Unknown | Held)
                      // 3.0 značenje: sme da podrži postojeći ProvesLink claim
    Confirmed,        // Resolved && Bound && Held
                      // jači provenance; nije novi claim, samo bolji trag
    Indeterminate     // ChangedDuringExecution
}
```

`Confirmed` postoji samo uz Live observer. Windows bez observera ostaje `Attributed`. `ProvesLink` se ne menja. `Unknown` kontinuitet ne sme da spusti claim ispod 3.0 nivoa.

## 5.9 Probe preflight — po familiji, po address family, po execution path

Nema `bool CanNetwork`.

```text
CapabilityKey =
    ProbeKind          Icmp | TcpConnect | Tls | Dns | Http
  × AddressFamily      IPv4 | IPv6
  × ExecutionPath      RouteLookup | SourceBind | IcmpDatagram
                       | TcpSocket | UdpSocket | TlsHandshake | HttpClient
```

Svaki ključ ima sopstveni `PlatformCapabilityObservation` (§4.5 stanja).

### 5.9.1 Šta se meri

| Ključ | Kako | Default očekivanje |
|---|---|---|
| `RouteLookup / IPv4` | `RTM_GETROUTE` ka `1.1.1.1` | Success |
| `RouteLookup / IPv6` | `RTM_GETROUTE` ka `2606:4700:4700::1111` | Success ili Unsupported ako nema IPv6 rute |
| `SourceBind / IPv4` | bind na lokalni IPv4 unicast (ne tentative) | Success ako ima adresu |
| `SourceBind / IPv6` | bind na globalni IPv6 | Success ako `Ipv6Availability` |
| `IcmpDatagram / IPv4` | `socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)` | Success ako `ping_group_range` pokriva gid `iem` |
| `IcmpDatagram / IPv6` | `socket(AF_INET6, SOCK_DGRAM, IPPROTO_ICMPV6)` | isto, odvojeno od IPv4 |
| `TcpSocket / IPv4` | `socket(AF_INET, SOCK_STREAM, TCP)` | Success |
| `TcpSocket / IPv6` | `socket(AF_INET6, SOCK_STREAM, TCP)` | Success ako ima IPv6 |
| `UdpSocket / *` | DNS soket | Success |
| `IcmpRaw / *` | **ne radi se** u default | `Unsupported` namerno |

`ping_group_range` se čita iz `/proc/sys/net/ipv4/ping_group_range` kao **dijagnostika**, ne kao autoritet (paket ga dinamički podešava za alocirani numerički `iem` GID). Autoritet je isključivo `socket()` rezultat. Isti sysctl na savremenom kernelu pušta i ICMPV6, ali IPv4 i IPv6 se **ipak** proveravaju odvojeno: jedan može `EPERM`, drugi ne, ili IPv6 stack ne postoji.

`Ipv6Availability` ostaje filter **rasporedа** IPv6 meta (postojeći Core). To nije ICMP capability. Mašina može imati globalni IPv6 i zabranjen ICMPV6 datagram.

### 5.9.2 Kada se meri

- jednom pre prve sesije,
- posle suspend/resume,
- posle `EPERM` na soketu koji je ranije bio Success (re-preflight te ćelije),
- reconciliation svakih 10 min,
- **ne** na svakoj probe — to bi bilo merenje, ne preflight.

Rezultat preflight-a je provenance sesije, ne verdict sesije.

### 5.9.3 Matrica izvršenja

| Uslov | Outcome | Bound | Tally / loss | Atribucija |
|---|---|---|---|---|
| ICMP datagram Success, ruta resolved, bind uspeo | izvrši ICMP | true | Eligible | po §5.8 |
| ICMP datagram Success, ruta unresolved | izvrši ICMP unbound | false | Eligible | `None` |
| ICMP `PermissionDenied` / `Unavailable` / `Unsupported` | `Skipped` (`NotExecuted`) | false | **ne ulazi** (`IsSilent`) | — |
| ICMP `ProviderFailure` | `Skipped` | false | ne ulazi | — |
| IPv4 ICMP denied, IPv6 ICMP Success | IPv4 skip; IPv6 izvrši | po IPv6 bind | samo IPv6 ICMP u tally | odvojeno |
| IPv4 ICMP denied, TCP/DNS/HTTP Success | ICMP skip; ostali idu | po njihovom bind | TCP/DNS/HTTP Eligible | normalno |
| TCP bind nemoguć, connect moguć unbound | izvrši TCP | false | Eligible | Predicted ili None |
| TCP bind `EPERM` | `Skipped` | false | ne ulazi | — |
| DNS/HTTP dostupni, ICMP nije | oni nastavljaju | njihovo | njihovo | njihovo |
| `PermissionDenied` bilo koje ćelije | nikad `Failed` / TimedOut-as-loss | false | ne ulazi | — |

`System.Net.NetworkInformation.Ping` se **ne** koristi kao tihi fallback kada je `IcmpDatagram` denied. To bi ili opet puklo, ili (gore) uspelo unbound i onda izgledalo kao merenje koje smo tvrdili da ne radimo.

Razlozi na `ProbeResult.Detail` (stabilni kodovi, ne slobodan tekst kao jedini signal):

```text
IcmpCapability=PermissionDenied
IcmpCapability=Unavailable
IcmpCapability=Unsupported
SourceBind=EADDRNOTAVAIL
SourceBind=PermissionDenied
RouteLookup=Unresolved
RouteLookup=ProviderFailure
PrefSrcGone
```

## 5.10 Provenance uz svaki fallback

Svaki fallback piše **eksplicitno** šta je pokušano, šta je vraćeno, i šta je urađeno umesto toga. Invarijanta 239.

| Fallback | Obavezan provenance |
|---|---|
| `ProbePath.Unresolved` | `RouteLookup` state + native errno/name |
| ICMP skip | `IcmpDatagram/{family}` state + `ping_group_range` snapshot (dijagnostika) |
| Unbound TCP/DNS posle neuspelog bind | `SourceBind/{family}` state + native error + `ExecutedUnbound=true` |
| Observer polling | `NetlinkSubscription=<measured reason>` + `ObserverMode=PollingReconciliation` |
| `IfindexUnmappedToDotNet` | ifindex + ifname + razlog |
| `NullRouteResolver` (samo test / total failure) | `RouteResolver=Null` — production Linux ovo ne sme kao tihi default |
| IPv6 target nije raspoređen | postojeći `Ipv6Availability=false`, nije capability failure |
| Multipath divergent ifindex | `Multipath=true; DistinctIfindexCount=N` |
| Prefsrc nestao između resolve i bind | `PrefSrcGone` + ifindex |

Provenance je supplementary. Ne sme da promeni `ProbeOutcome` sem kroz pravila u §5.9.3.

## 5.11 VPN, default route, multipath tokom sesije

### VPN / tunnel

`IFLA_INFO_KIND ∈ {wireguard, tun, tap, ipip, gre, sit, …}` se beleži u link provenance.

Zamena default rute sa `wlan0` na `wg0`:

- generation++,
- keš prazan,
- naredni `Resolve` vraća novi OIF,
- `NetworkEnvironment` se menja (postojeći 3.0 mehanizam),
- to **nije** outage,
- probe koje su u letu u kritičnom prozoru dobijaju `ChangedDuringExecution`.

Split tunnel: različite destinacije → različiti OIF. `ProbeCycle.MultiplePathsInUse` već to hvata. Atribucija jednom linku se gasi (`AgreedInterfaceId == null`). To je ispravno.

### Default route flap

`NEWROUTE`/`DELROUTE` sa `rtm_dst_len=0` je session-level event, ne incident. Classifier vidi posledice kroz stvarne probe ishode, ne kroz sam event.

### Multipath / ECMP

Ako odgovor nosi `RTA_MULTIPATH`:

```text
struct rtnexthop { rtnh_len, rtnh_flags, rtnh_hops, rtnh_ifindex } + RTA_GATEWAY
```

- `ProbePath` uzima **izabrani** nexthop koji je kernel vratio uz fib lookup (obično prvi / onaj koji bi sada koristio).
- Provenance: `NexthopCount`, skup ifindex-a.
- Ako nexthop-ovi imaju **različite** ifindex-e: `PathAttribution` najviše `Reduced`, čak i uz bind. Bind na PREFSRC ne zaključava ifindex kada više interfejsa deli adresu.
- ECMP može da promeni hop **bez** `NEWROUTE`. Live observer to ne vidi. Zato multipath nikad ne dobija `Confirmed` samo na osnovu generation-a. `Confirmed` zahteva `DistinctIfindexCount == 1`.

## 5.12 Quorum kada familija nije izvršena

Ovo je postojeći Core. Linux sme samo da ga **hrani ispravnim Outcome-ima**.

`ProbeCycle.Tally` ignoriše `!WasAttempted`. Dakle `Skipped` ne povećava `Attempted`, ne pravi `AllFailed`, ne pravi loss.

| Šta se desilo | Tally | Classifier |
|---|---|---|
| Svi IPv4 ICMP skip (nema capability), TCP 2/2 success | `ExternalIcmp.IsSilent`, `ExternalTcp.AllSucceeded` | `AnyExternalReachability=true` → nije outage |
| IPv4 ICMP skip, IPv6 ICMP 0/2 timeout, TCP success | samo IPv6 ICMP u ICMP tally; TCP drži reachability | nije outage; ICMP IPv6 sme da uđe u degradation, ne u „sve je mrtvo“ |
| Gateway ICMP skip, nema external success | `Gateway.IsSilent` | `InternetDown` („gateway state is unknown“), **ne** `GatewayDown` |
| Gateway ICMP skip, external TCP success | gateway silent, internet radi | OK; gateway koji ne odgovara dok linija radi već se ignoriše |
| ICMP 0/3 timeout (capability Success, nema reply) | `ExternalIcmp.AllFailed` | loss-eligible; TCP i dalje može da spasi od outage (postojeće ICMP-filter pravilo) |
| DNS assigned skip zbog bind, public DNS success | assigned silent | **nije** `DnsIspFailure` — `AllFailed` zahteva `Attempted > 0` |

`TargetProbeStatistics`: skip/capability failure je `LocalExecutionFailure` ili se uopšte ne upisuje kao attempt. `EligibleCount = ExecutedCount - LocalFailureCount`. `NoReplyRatio` se ne računa preko nula imenioca (`null`, ne `0`).

Jedna address family se nikad ne prosečava u drugu (invarijanta 34, invarijanta 15).

IPv4 ICMP unavailable **nije** razlog da se IPv6 ICMP, niti ijedan TCP, preskoči.

## 5.13 Redosled jednog probe ciklusa

```text
preflight cells (već poznate, nisu deo ciklusa)
        │
PathTo(target)
        │  RTM_GETROUTE
        │  RouteResolutionObservation { G0, T0, OIF, PREFSRC }
        │
da li IcmpDatagram/{family} sme?
        ├── ne → Skip + provenance; STOP za tu ICMP metu
        └── da
              │
         PREFSRC postoji i SourceBind/{family} Success?
              ├── da → Bind; Bound=true ako bind prođe
              └── ne → Bound=false; ICMP i dalje sme da pošalje ako datagram živi
              │
         send / wait
              │
         Outcome = Success | TimedOut | Failed | Skipped
              │
         matching events in [T0, T1] ?
              ├── da → Continuity=ChangedDuringExecution
              ├── observer Live, ne → Held
              └── inače → Unknown
              │
         Record(ProbeResult)     činjenica
         Record(path sidecar)    atribucija
```

`ProbeScheduler` i dalje rešava rute **pre** runde (`planned = targets.Select(PathTo)`). Generation se uzima tada. Eventi tokom `WhenAll` idu u isti prozor za sve probe te runde.

## 5.14 Test gate ovog sloja

Network-namespace harness (§24) mora da pokrije, bez GUI-ja:

```text
IPv4 ICMP datagram otvoren, IPv6 ICMP EPERM
    → IPv4 ICMP attempted; IPv6 ICMP skipped; TCP obe familije idu

ping_group_range isključuje iem
    → sav ICMP skipped; TCP/DNS/HTTP i dalje klasifikuju outage/OK

RTM_GETROUTE ENETUNREACH, TCP meta ipak sluša
    → Path.Unresolved; TCP attempted; nije GatewayDown

default route wlan0 → wg0 tokom ICMP RTT
    → Outcome ostaje; Continuity=ChangedDuringExecution; nije outage zbog VPN-a

DELADDR prefsrc između GETROUTE i bind
    → Bound=false; PrefSrcGone; probe sme

ECMP dva ifindexa
    → nikad PathAttribution=Confirmed

multicast membership odbijen ili stream nepouzdan
    → ObserverMode=Polling; nijedan Held
    → razlog je izmereni errno/state, ne pretpostavljeni CAP_NET_ADMIN

bind EADDRNOTAVAIL na TCP
    → Skipped ili unbound sa Bound=false; nikad Failed-as-loss
```

Architecture test: `IEM.Linux.Network` ne referencira Avalonia / WPF / `IEM.App`.

---

## 5.15 Invarijante 241–248 (draft, ovaj sloj)

## 241

`ROUTE_LOOKUP_IS_A_PRE_EXECUTION_SNAPSHOT_NOT_A_PATH_GUARANTEE`

`RTM_GETROUTE` govori šta je kernel odlučio u trenutku resolve-a. To nije garancija puta tokom slanja.

## 242

`ROUTE_CHANGE_DURING_PROBE_NEVER_REWRITES_PROBE_OUTCOME`

Event u kritičnom prozoru sme da smanji atribuciju. Ne sme da promeni `Success`/`TimedOut`/`Failed`/`Skipped`.

## 243

`UNRESOLVED_ROUTE_NEVER_PREVENTS_PROBE_EXECUTION`

Nema rute ≠ nema merenja. Probe sme da ide; ne sme da tvrdi link.

## 244

`SOURCE_BIND_FAILURE_NEVER_BECOMES_NETWORK_FAILURE`

Neuspeo bind je local execution / unbound / skip. Nikad loss.

## 245

`ICMP_CAPABILITY_IS_NEVER_A_GLOBAL_NETWORKING_BOOLEAN`

Capability je ćelija `(ProbeKind × AddressFamily × ExecutionPath)`. IPv4 ICMP denied ne gasi IPv6, TCP, DNS, HTTP.

## 246

`SKIPPED_PROBE_NEVER_ENTERS_LOSS_OR_QUORUM_DENOMINATOR`

`Skipped` nije attempt. `ProbeTally` i `EligibleCount` ga ne vide.

## 247

`PATH_CONTINUITY_UNKNOWN_NEVER_DOWNGRADES_PRE_31_ATTRIBUTION`

Odsustvo observera nije razlog da 3.0 claim postane slabiji. Samo dokazan `ChangedDuringExecution` smanjuje atribuciju.

## 248

`NETLINK_SUBSCRIPTION_FAILURE_NEVER_SYNTHESIZES_PATH_HELD`

Bez izmerene Live multicast pretplate na path-relevantnim grupama nema `Held`. Polling ne sme da izmisli kontinuitet. Neuspeh pretplate se beleži kao izmereni razlog, ne kao zaključak da je potreban `CAP_NET_ADMIN`.

---

# 6. NetworkManager politika

NetworkManager je:

> **optional enrichment provider**

Nije kanonski routing autoritet.

Arhitektura:

```text
Kernel / rtnetlink
        ↓
canonical network facts

NetworkManager D-Bus
        ↓
optional explanatory metadata
```

Primer:

```text
Kernel:
interface=wlp2s0
route changed
gateway=192.168.1.1

NetworkManager:
reason=ssid-disconnected

IEM:
kernel fact = primary observation
NM reason = supplementary provenance
```

Sistem bez NetworkManager-a mora ostati potpuno funkcionalan.

Podržati:

- NetworkManager
- systemd-networkd
- manual networking
- servers without desktop stack
- custom distro networking
- VPN/tunnel setups

bez promene kanonske semantike.

---

# 7. Linux Wi-Fi Architecture — nl80211 + RadioOn / SsidVisibleInScan

Zaključano 2026-08-19. Attribution layer: adapter proizvodi **observations**. `WirelessDetailReader` i `StateClassifier` u Core-u jedini donose značenje.

Linux ne uvodi `WiFiEnabled`, `LocalWirelessUnavailable`, ni novi `NetworkState`. Popunjava postojeće ulaze.

## 7.1 Centralno pravilo

```text
nl80211 / rfkill     →  činjenice (nullable)
WirelessDetailReader →  WirelessSnapshot
StateClassifier      →  NetworkState
```

Nema generičkog `WiFiStateProvider` koji sve spljošti u par boolean-a. `GET_WIPHY`, `GET_INTERFACE`, `GET_STATION`, `GET_SCAN` i `TRIGGER_SCAN` su različite operacije i ostaju različite.

## 7.2 Postojeći Core ugovor — šta Linux sme da popuni

`IWirelessRadio` (već u `IEM.Core.Probes`):

| Metoda | Povrat | Značenje koje već postoji |
|---|---|---|
| `IsRadioOn(interfaceId)` | `bool?` | radio **tog** adaptera. `null` = nije utvrđeno |
| `ReadAssociation(interfaceId)` | `WirelessAssociation?` | trenutna asocijacija, ili `null` |
| `ReadAccessPoint(ssid, bssid)` | `WirelessAccessPoint?` | BSS **tačno tog BSSID-a** iz poslednjeg upotrebljivog skena |
| `IsSsidVisible(ssid)` | `bool?` | da li je imenovani SSID bio u poslednjem **upotrebljivom** skenu |
| `RequestUrgentScan()` | void | molba za brži sken; platforma sme da je ignoriše |

`WirelessDetailReader` već radi:

- pamti SSID 10 min posle drop-a (`RememberedSsidLifetime`),
- `SsidVisibleInScan` pita za taj (živi ili zapamćeni) SSID,
- AP gleda po asociranom BSSID-u, ne po najglasnijem istoimenom,
- `Read()` vraća `null` (nema wireless snapshot) kad nema ni živog ni zapamćenog SSID-a,
- `NoteTrouble()` zove `RequestUrgentScan()`.

`WlanLinkInspector` zove `NoteTrouble()` kad `!link.IsUp || IsSignalWeak`. Linux dekorator radi isto. Da li to postaje `TRIGGER_SCAN` odlučuje Linux radio, ne Core.

`StateClassifier.ClassifyLocalLink` (jedini Wi-Fi attribution):

```text
ako je link UP          → nije lokalna Wi-Fi atribucija
ako je link DOWN:
    Medium==Wireless
    && SsidVisibleInScan == false     // strogo false, ne != true
    && RadioOn == true                // strogo true, ne != false
        → NetworkState.WifiRadioDown
    inače
        → NetworkState.AdapterDown
```

Komentar na `NetworkState.WifiRadioDown` („adapter stayed up“) **nije** autoritet. Autoritet je classifier. Linux se pokorava classifieru.

`IncidentEvidenceCollector` sužava `_ssidVisible` / `_signalHealthy` samo kad je vrednost non-null. Odsutan sken nije „mreža je nestala“.

## 7.3 Tri činjenice, ne jedan WiFiEnabled

Linux observation, **pre** mapiranja na `IWirelessRadio`:

| Činjenica | true | false | unknown |
|---|---|---|---|
| `InterfacePresent` | `GET_INTERFACE` našao ifindex / wiphy za taj adapter | wiphy/iface ne postoji (izvučen dongle, `ENODEV` posle nestanka) | `GET_INTERFACE` nije pouzdan (`EPERM`, `EOPNOTSUPP`, malformed) |
| `RadioOn` | rfkill za **taj** wiphy postoji i nije hard/soft blocked | rfkill za taj wiphy je hard ili soft blocked | nema rfkill čvora, rfkill se ne može pročitati, ili wiphy nije nađen |
| `Associated` | pozitivna asocijacija (§7.6) | `GET_INTERFACE` uspeo, IFTYPE station, nema SSID i nema BSS_STATUS=associated | get-interface/station/scan associated-BSS nije pouzdan |

Ovo **nisu** sinonimi.

```text
InterfacePresent=false  ≠  RadioOn=false
iface DOWN              ≠  RadioOn=false
nije Associated         ≠  RadioOn=false
nema scan rezultata     ≠  nije Associated
```

Mapiranje na postojeći ugovor:

| Linux činjenica | `IWirelessRadio` |
|---|---|
| `InterfacePresent` false ili unknown | `IsRadioOn=null`, `ReadAssociation=null` (kao Windows: adapter nije među navedenim) |
| `RadioOn` | `IsRadioOn` |
| `Associated=true` | `ReadAssociation` = SSID/BSSID/quality |
| `Associated=false` ili unknown | `ReadAssociation=null` — Windows već spaja „nije povezan“ i „API fail“ na ovom mestu. Provenance razlikuje; Core interfejs ne |

## 7.4 nl80211 površina — odvojene operacije

Generic netlink familija `nl80211`. Family id i multicast group id-ovi se razrešuju preko `CTRL_CMD_GETFAMILY`, ne hardcode-uju.

```text
LinuxNl80211Socket          GENL, jedan query fd + jedan event fd
LinuxNl80211Radio           : IWirelessRadio
LinuxWifiScanCache          GET_SCAN snapshot + freshness
LinuxWifiLinkInspector      dekorator, WirelessDetailReader
LinuxWirelessDiagnostics    host dijagnostika
```

Komande koje 3.1 koristi:

| Komanda | Svrha | Nije |
|---|---|---|
| `NL80211_CMD_GET_WIPHY` | wiphy id, ime, capability, veza ka rfkill | radio on/off sam po sebi |
| `NL80211_CMD_GET_INTERFACE` | ifindex, iftype, MAC interfejsa, SSID ako je predat, freq | BSSID (to je BSS/station) |
| `NL80211_CMD_GET_STATION` | station info za konkretan MAC (BSSID) | scan lista |
| `NL80211_CMD_GET_SCAN` | kernel BSS keš / poslednji završeni sken | trigger |
| `NL80211_CMD_TRIGGER_SCAN` | **opciono**, nije 3.1 default put | uslov za evidence |

Multicast grupe (runtime detection, isti stav kao §5.7.3):

```text
nl80211 scan         NEW_SCAN_RESULTS / SCAN_ABORTED
nl80211 mlme         CONNECT / DISCONNECT / ROAM / MICHAEL_MIC_FAILURE / …
nl80211 config       NEW_INTERFACE / DEL_INTERFACE / SET_INTERFACE
nl80211 regulatory   (dijagnostika, nije attribution)
```

Eventi osvežavaju keš. Ako membership ne uspe, čitanje ide GET_* na tajmeru. Neuspeh pretplate nije `RadioOn=false`.

Ne koristiti kao kanonski izvor: `iw`, `nmcli`, `wpa_cli`, `/proc/net/wireless` parsiranje.

## 7.5 RadioOn — samo rfkill tog wiphy

Windows: `EnumerateInterfaceConnections()` → `IsRadioOn` za taj GUID. Null ako adapter nije na listi ili API padne.

Linux `false` sme samo uz **pozitivan** dokaz da je radio ugašen:

```text
rfkill (wiphy-scoped, preko /dev/rfkill ili sysfs rfkill
        vezanog za phy koje je GET_WIPHY imenovao)

hard-block OR soft-block  →  RadioOn = false
rfkill prisutan, oba clear →  RadioOn = true
```

`RadioOn = null` kad:

- `InterfacePresent` nije true,
- wiphy nema rfkill uređaj,
- čitanje rfkill-a padne (`EPERM`, `ENODEV`, malformed),
- `GET_WIPHY` padne,
- nije jasno koji rfkill pripada kom wiphy.

**Nikad `unknown → false`.**

Šta **nije** `RadioOn=false`:

- iface administratively DOWN,
- nije associated,
- `EOPNOTSUPP` na bilo kojoj nl80211 komandi,
- prazan scan,
- NetworkManager `unavailable` / `disconnected`,
- nestanak USB dongle-a (`InterfacePresent=false` → `IsRadioOn=null`).

Iface UP/DOWN ostaje `LinkSnapshot.Status` iz `SystemLinkInspector` / rtnetlink. To je link, ne radio prekidač.

## 7.6 Association — GET_INTERFACE + associated BSS + GET_STATION

Redosled, kao `iw dev … link`, ne kao nagađanje iz skena:

```text
1. GET_INTERFACE (ifindex)
      NL80211_ATTR_IFINDEX
      NL80211_ATTR_WIPHY
      NL80211_ATTR_IFTYPE          (mora station/p2p-client za klijentsku asocijaciju)
      NL80211_ATTR_MAC             = lokalni MAC, NIJE BSSID
      NL80211_ATTR_SSID            = prisutan kad driver predaje SSID veze
      NL80211_ATTR_WIPHY_FREQ      = kanal asocijacije ako je predat
      NL80211_ATTR_SSID odsustvo   ≠ disconnect; idi na korak 2

2. GET_SCAN dump, traži BSS gde je
      NL80211_BSS_STATUS ∈ { ASSOCIATED, IBSS_JOINED }
      (AUTHENTICATED sam nije Associated)

      taj BSS daje:
        NL80211_BSS_BSSID
        NL80211_BSS_INFORMATION_ELEMENTS → SSID
        NL80211_BSS_SIGNAL_MBM / SIGNAL_UNSPEC
        NL80211_BSS_FREQUENCY
        NL80211_BSS_SEEN_MS_AGO

3. GET_STATION, NL80211_ATTR_MAC = BSSID iz koraka 2
      NL80211_STA_INFO_SIGNAL / SIGNAL_AVG     dBm
      NL80211_STA_INFO_TX_BITRATE / RX_BITRATE
      NL80211_STA_INFO_INACTIVE_TIME
      ENOENT / not found → Associated ostaje iz koraka 2, station info unknown
```

`Associated = true` samo uz korak 2 (BSS_STATUS associated) ili uz `NL80211_ATTR_SSID` na interfejsu **i** uspešan `GET_STATION` za poznati BSSID.

`Associated = false` kad je `GET_INTERFACE` uspeo, IFTYPE je klijentski, nema SSID na iface, i dump skena nema BSS_STATUS associated.

`Associated = unknown` na `EPERM` / `EOPNOTSUPP` / `ENODEV` / sakaćen dump.

Mapiranje u `WirelessAssociation`:

| Polje | Izvor | Ako nema |
|---|---|---|
| `Ssid` | iface `NL80211_ATTR_SSID`, inače IE iz associated BSS | `null` — onda reader koristi remembered SSID |
| `Bssid` | `NL80211_BSS_BSSID` | `null` |
| `SignalQuality` | 0–100 iz signala ako se može izvesti; inače `null` | `null`; ne izmišljati 0 |

`ReadAccessPoint`: samo BSS čiji BSSID poklapa traženi, sa `Rssi` u dBm (`SIGNAL_MBM / 100`) i kanal iz frequency. Nikad „najglasniji SSID“.

Roaming: promena BSSID-a je `context.BssidChanged` (već postoji). Adapter javlja novi BSSID; Core sužava `_noRoaming`. Linux ne klasifikuje roam kao outage.

## 7.7 SsidVisibleInScan — GET_SCAN + freshness, ne „nije nađeno“

`GET_SCAN` nije `TRIGGER_SCAN`. Odustvo SSID-a u praznom ili starom kešu nije `false`.

### 7.7.1 Snapshot

```text
WifiScanSnapshot
    ObservedAtMonotonic
    Source            KernelBssCache | TriggeredScan
    Completeness      Complete | Partial | Unknown
    Bss[]             { Ssid, Bssid, Frequency, RssiDbm, SeenMsAgo, Status }
```

`ScanObservedAt`: vreme `NEW_SCAN_RESULTS` ako ga imamo; inače `now - min(SEEN_MS_AGO)` ako ima BSS-ova; inače unknown.

`ScanAge`: monotonic now − `ScanObservedAt`. Ako `ObservedAt` unknown → age unknown → vidljivost `null`.

`MaximumAge` = **3 min**, isto kao `WlanScanCache`. Starije od toga: `IsSsidVisible = null`, čak i ako keš još drži ime.

### 7.7.2 Trostanje

| Vrednost | Uslov |
|---|---|
| `true` | postoji snapshot sa `ScanAge ≤ MaximumAge` i bar jedan BSS čiji SSID poklapa (OrdinalIgnoreCase, kao Windows keš) |
| `false` | snapshot je **validan, svež i dovoljno potpun**, i nijedan BSS ne nosi taj SSID |
| `null` | nema dokazivog scan observation-a |

`false` zahteva `Completeness=Complete` ili eksplicitno završen dump posle `NEW_SCAN_RESULTS`.

`Partial` (opportunistic keš, nema scan-done): sme `true` ako se SSID vidi; **ne sme** `false` (keš može biti nepotpun).

Prazan dump bez scan-done: `null`, ne `false`.

Prazan dump posle `NEW_SCAN_RESULTS` i `ScanAge ≤ MaximumAge`: `false`.

Nema skena od starta: `null` (`WlanScanCache._everScanned == false`).

### 7.7.3 TRIGGER_SCAN nije 3.1 default

Windows 3.0 aktivno skenira na 45 s / 8 s. To je Windows adapter, ne kanonsko pravilo.

Linux 3.1 baseline:

```text
existing kernel BSS cache   →  observation
NL80211_CMD_TRIGGER_SCAN    →  opcioni capability, nije uslov za evidence
```

Razlog: aktivni sken ima side-effect (kratki prekid, power, driver quirk, ponekad privilege). Kernel namerno razdvaja get-scan od trigger-scan.

`RequestUrgentScan()`:

- `WifiTriggerScan` preflight `Success` → sme da pošalje `TRIGGER_SCAN` u pozadini, ne u probe hot path,
- inače no-op + provenance `ScanTrigger=Unsupported|PermissionDenied|Unavailable`.

Linux **ne** pokreće 45 s trigger loop da bi se izjednačio sa Windowsom. Posledica je namerna i dozvoljena: `SsidVisibleInScan` će češće biti `null` nego na Windowsu. Classifier tada **ne** izriče `WifiRadioDown`. To je slabija atribucija, ne druga semantika.

Preflight ćelija: `WifiTriggerScan` (jedna, nije deo ICMP/TCP matrice). Njen failure ne dira connectivity probe.

Periodični `GET_SCAN` dump (samo čitanje keša) sme na 45 s. To nije trigger.

## 7.8 Signal nije connectivity verdict

```text
RSSI = -82 dBm ,  AnyExternalReachability = true   →  nije kontradikcija
RSSI = -40 dBm ,  InternetDown                     →  nije dokaz Wi-Fi ni ISP uzroka
```

`IsSignalWeak` (`< -70 dBm`) već postoji. Koristi se za `NoteTrouble()` i confidence (`_signalHealthy`), **ne** za `NetworkState`. Linux sme samo da popuni `MeasuredRssiDbm`. Ne sme da iz RSSI izvede outage, `RadioOn`, ni `SsidVisibleInScan`.

Bitrate iz `STA_INFO_*_BITRATE` je provenance. Nije verdict.

## 7.9 NetworkManager ostaje enrichment

```text
nl80211 associated BSSID-A
NM connected profile B
        ↓
canonical: BSSID-A
supplementary: NmProfile=B, NmState=…
conflict observation: AssociationConflict (provenance, ne novi NetworkState)
```

NM ne sme da popuni niti pregazi: `RadioOn`, `SsidVisibleInScan`, BSSID, SSID asocijacije, `ProbePath`, rutu.

Sistem bez NM ostaje potpuno merljiv (invarijanta 219 / 233).

## 7.10 Failure mapping — adapter failure ostaje adapter failure

| errno / stanje | RadioOn | Associated | SsidVisible | Probe |
|---|---|---|---|---|
| `EOPNOTSUPP` | null | unknown → `ReadAssociation=null` | null | ne dira se |
| `ENODEV` (iface nestao) | null (`InterfacePresent` false/unknown) | null | null | `SystemLinkInspector` vidi Missing/Down |
| `EPERM` | null | unknown | null | ne dira se |
| malformed IE / vendor attr | ignoriši atribut | ne ruši ostalo | ne ruši ostalo | ne dira se |
| prazan GET_SCAN, nema scan-done | ne dira | po GET_INTERFACE | **null** | ne dira se |
| rfkill read fail | **null** | ne dira | ne dira | ne dira se |

Nijedan red nije `RadioOn=false`, „SSID not visible“, ni „Wi-Fi failed“ kao network failure.

Wi-Fi adapter down ne zaustavlja Ethernet merenje na drugom interfejsu. Nedostupan Wi-Fi metadata ne invalidira ICMP/TCP/DNS/HTTP (invarijanta 220).

## 7.11 Attribution truth table — iz postojećeg classifiera

Kolone su Linux observations + postojeći `LinkSnapshot` / probe. Poslednja kolona je **postojeći** `NetworkState`, ne novi enum.

`link` = `LinkSnapshot.Status` sa `SystemLinkInspector` (rtnetlink / .NET), ne iz nl80211.

| RadioOn | Associated | SsidVisible | Link | External reachability | `NetworkState` | Zašto |
|---|---|---|---|---|---|---|
| false | false | null | Down | n/a (lokalni link prvi) | `AdapterDown` | radio ugašen; nije `WifiRadioDown` |
| true | false | true | Down | n/a | `AdapterDown` | SSID je u etru; nije mrtav AP radio |
| true | false | false | Down | n/a | `WifiRadioDown` | jedini put do tog stanja |
| true | false | null | Down | n/a | `AdapterDown` | nema svežeg skena; unknown ≠ nestao |
| null | false | null | Down | n/a | `AdapterDown` | unknown radio; `RadioOn==true` nije zadovoljeno |
| true | true | true / null | Up | Down | `GatewayDown` / `CpeUpstreamUnreachable` / `InternetDown` po §5 quorum | **ne** Wi-Fi uzrok |
| true | true | false | Up | Down | isto, probe | asociran; nestanak iz skena nije uzrok |
| * | * | * | Up | Up | `Ok` / DNS / degradation | Wi-Fi se ne pita za outage |
| null | * | * | Up | Down | probe klasifikacija | Wi-Fi metadata absent |

Informalni nazivi iz diskusije mapiraju se ovako — **ne unose se u kod**:

```text
LocalWirelessUnavailable     → AdapterDown   (RadioOn=false, link down)
AssociationFailure candidate → AdapterDown   (radio on, SSID visible, nije associated, link down)
SSID/AP availability         → WifiRadioDown (radio on, svež sken bez SSID, link down)
Indeterminate                → AdapterDown   (RadioOn unknown ili SsidVisible null)
```

`Skipped` ICMP zbog §5 ne menja ovu tabelu. Lokalna Wi-Fi atribucija gleda link + wireless snapshot, ne ICMP capability.

## 7.12 Šta LinuxWifiLinkInspector radi

Isto što `WlanLinkInspector`:

```text
inner = SystemLinkInspector
ako Medium != Wireless → vrati inner, bez Wireless
inače
    snapshot = WirelessDetailReader.Read(interfaceId)
    ako !IsUp || snapshot?.IsSignalWeak → NoteTrouble()
    return inner with { Wireless = snapshot }
```

Ako nl80211 nije dostupan: `LinuxLinkInspection.Create` vraća samo `SystemLinkInspector`. Connectivity monitoring ostaje.

`interfaceId` mora biti isti string kao u §5 ifindex mape. Scan i radio se scoped-uju na taj iface/wiphy. Tuđi USB dongle ne odgovara na pitanje o monitorisanom linku.

## 7.13 Provenance uz svaki Wi-Fi fallback

| Situacija | Provenance |
|---|---|
| nema nl80211 familije | `Nl80211=Unavailable` |
| `GET_INTERFACE` EPERM | `WifiInterface=PermissionDenied` |
| nema rfkill | `RadioOn=Unknown; Rfkill=Absent` |
| `GET_SCAN` prazan, nema scan-done | `ScanCompleteness=Unknown` |
| keš stariji od 3 min | `ScanAgeExceeded` |
| `TRIGGER_SCAN` nije rađen | `ScanTrigger=NotUsed` (3.1 default) |
| `TRIGGER_SCAN` odbijen | `ScanTrigger=<state>` |
| NM se razilazi sa BSSID | `NmAssociationConflict` |
| remembered SSID u upotrebi | već implicitno u readeru; adapter ne laže da je associated |

## 7.14 Invarijante 249–254 — LOCKED

## 249

`RADIO_ON_UNKNOWN_NEVER_BECOMES_FALSE`

Odsustvo rfkill/nl80211 činjenice nije ugašen radio.

## 250

`SSID_ABSENCE_WITHOUT_FRESH_COMPLETE_SCAN_IS_UNKNOWN`

`SsidVisibleInScan=false` zahteva validan, svež, dovoljno potpun scan snapshot.

## 251

`ACTIVE_SCAN_IS_NEVER_REQUIRED_FOR_EVIDENCE_OPERATION`

`TRIGGER_SCAN` nije uslov za monitoring, potpis, ni verifikaciju.

## 252

`WIFI_ADAPTER_FAILURE_NEVER_BECOMES_RADIO_OFF_OR_SSID_GONE`

`EOPNOTSUPP` / `ENODEV` / `EPERM` / malformed nisu `RadioOn=false` ni `SsidVisibleInScan=false`.

## 253

`SIGNAL_STRENGTH_IS_NEVER_A_CONNECTIVITY_VERDICT`

RSSI ne proizvodi `NetworkState` i ne opovrgava probe ishod.

## 254

`NETWORKMANAGER_NEVER_OVERRIDES_NL80211_ASSOCIATION`

Konflikt sa NM se beleži; kanonski BSSID/SSID ostaju kernelovi.

## 7.15 Test gate

```text
Ethernet only, nema nl80211
    → SystemLinkInspector; probe rade; nema WifiRadioDown

rfkill soft-block, link down
    → RadioOn=false; AdapterDown; nije WifiRadioDown

rfkill absent, GET_INTERFACE fail
    → RadioOn=null; AdapterDown ako je link down

GET_SCAN prazan, nikad NEW_SCAN_RESULTS
    → SsidVisibleInScan=null; nije WifiRadioDown

GET_SCAN complete, svež, SSID odsutan, RadioOn=true, link down
    → WifiRadioDown

GET_SCAN complete, SSID prisutan, RadioOn=true, link down
    → AdapterDown

Associated, RSSI=-85, TCP success
    → Ok; signal weak sme da postoji

Associated, RSSI=-40, svi external fail
    → probe klasifikacija; nije WifiRadioDown

NM kaže profile B, nl80211 BSSID-A
    → snapshot BSSID-A; konflikt u provenance

TRIGGER_SCAN EPERM
    → monitoring živi; RequestUrgentScan no-op

EOPNOTSUPP na GET_STATION
    → Associated po BSS_STATUS ako postoji; station info null; nije radio off
```

---

# 8. Suspend / Resume

Ovo je release-critical zbog postojećih dokaznih invarijanti.

Primarni Linux izvor:

```text
systemd-logind
org.freedesktop.login1
PrepareForSleep
```

Semantika:

```text
PrepareForSleep(true)
        ↓
HostSuspending
        ↓
measurement observability CLOSED

          SLEEP

PrepareForSleep(false)
        ↓
HostResumed
        ↓
measurement observability RESTORED
```

Nikada:

```text
50 min suspend = 50 min outage
```

Ako logind signal nije dostupan:

```text
HostObservabilityGap
Confidence = Reduced
Reason = SuspendSignalUnavailable
```

Ne sme se fabricirati outage.

---

# 9. Reboot / Service Restart / Host Discontinuity

Boot identity živi u `LinuxTimeObservationProvider.CaptureBootObservation` (`/proc/sys/kernel/random/boot_id`). **Ne** uvoditi `IBootIdentityProvider` / `LinuxBootIdentityProvider` kao zaseban ugovor (§4.3.1).

Cilj je razlikovati:

- process restart
- service restart
- system reboot
- suspend/resume
- host crash
- measurement interruption
- actual network interruption

Service restart ili reboot nikada ne sme postati mrežni outage bez nezavisnih mrežnih činjenica.

---

# 10. Linux Time Provenance

Linux implementacija:

```text
LinuxTimeObservationProvider
```

Pratiti najmanje:

- UTC wall clock
- monotonic clock
- boot-relative clock
- boot identity
- clock adjustment events
- time synchronization state kada je pouzdano dostupan

NTP/time-sync problem:

```text
NTP unavailable
```

nije:

```text
Internet unavailable
```

već utiče na timestamp provenance/quality.

---

# 11. Unix IPC — jedan app-owned socket

Zaključano 2026-08-19.

## 11.0 Odluka: nema socket activation u 3.1

`LinuxUnixDomainSocketTransport` **sam** pravi listener, kao što `WindowsNamedPipeTransport` sam pravi named pipe.

```text
3.1 baseline:
    IIpcTransport.RunAsync
        → socket() / bind() / listen() / accept()
        → SO_PEERCRED po konekciji

Nije 3.1:
    internet-evidence-monitor.socket
    ListenStream=
    SocketUser= / SocketGroup= / SocketMode=
    inherited FD (LISTEN_FDS / SD_LISTEN_FDS_START)
```

Socket activation je legitimna kasnija opcija. Sada bi uvela inherited-FD lifecycle koji `IIpcTransport` nema. Ne uvodi se „za svaki slučaj“.

Windows ima **dva** kanala (`StatusPipeServer` + `IIpcTransport`). Linux **ne** kopira taj dualitet. Jedan pathname socket nosi i status/read i command protokol, isti `IpcRequestEnvelope` / dispatcher, različita autorizacija po komandi.

## 11.1 Endpoint

```text
pathname AF_UNIX
/run/internet-evidence-monitor/control.sock
```

Zabranjeno:

- abstract namespace (`\0…`) — nema filesystem admission, nema `0660`,
- drugi socket za status,
- `SO_PASSCRED` kao zamena za `SO_PEERCRED` na connected socketu (koristi se `getsockopt(SO_PEERCRED)` na prihvaćenoj vezi).

`AF_UNIX` pathname poštuje filesystem ACL: treba search na svakom direktorijumu putanje **i** dozvola na samom socketu. To je sloj 1, ne autorizacija.

## 11.2 Parent directory — traversal politika

Željeno stanje:

```text
/run/internet-evidence-monitor/     iem:iem-users    0750
```

`RuntimeDirectory=internet-evidence-monitor` pravi `/run/…` kao `iem:iem` (jer systemd prati `User=`/`Group=` `iem` i nema zasebnu `RuntimeDirectoryGroup=` direktivu). To **nije** dovoljno: članovi grupe `iem-users` ne bi mogli da uđu.

Rešenje bez `CAP_CHOWN`, bez root helper-a i bez `tmpfiles.d`: baseline unit definiše `SupplementaryGroups=iem-users`. Servisni proces koji trči kao `User=iem` sa tom dopunskom grupom ima pravo da promeni GID sopstvenog resursa na grupu čiji je član.

Posle kreiranja, pre bind-a, transport **proverava i doteruje** samo ako je putanja tačno očekivani runtime dir:

```text
realpath(parent) == /run/internet-evidence-monitor
lstat(parent): nije symlink
owner uid == iem
onda: promeni GID na iem-users (chown/fchown samo GID), chmod 0750
```

Ako owner nije `iem`, ili je symlink, ili realpath nije očekivani — **fail closed**, servis ne sluša. Ne „popravlja“ tuđi direktorijum.

`0750` + grupa `iem-users`: članovi smeju traverse (`+x`) i list (`+r` nije potreban za connect, ali `0750` ga daje grupi). Ostali nemaju `x` — ne dolaze do socket čvora.

`iem-admin` mora moći da se poveže. Konfiguracija: `iem-admin ⊆ iem-users` (admin korisnik se dodaje u obe grupe). Član samo `iem-admin` bez `iem-users` ne prolazi sloj 1.

uid 0 na Linuxu sme da zaobiđe mode. Zato sloj 1 **nije** dovoljan za root. Sloj 2–4 i dalje važe; uid 0 dobija `role:admin` kanonski, ne zato što je „prošao ACL“.

## 11.3 Safe create i stale cleanup

Redosled (sve preko `lstat` / `O_NOFOLLOW`, nikad `stat` koji prati symlink):

```text
1. potvrdi parent (§11.2)
2. umask 0177                    // create 0600
3. ako path postoji:
     lstat
     nije S_IFSOCK        → fail closed, NE unlink
     symlink              → fail closed, NE unlink
     owner != iem         → fail closed, NE unlink
     jeste socket, owner iem:
         connect()
           uspeh          → drugi instance živi → fail (ne kradi socket)
           ECONNREFUSED
           / ENOTCONN     → unlink SAMO tog inode-a, pa nastavi
4. bind(pathname)
5. chown GID iem-users           // omogućeno preko SupplementaryGroups=iem-users
6. chmod 0660                    // tek sada grupa sme connect
7. listen
8. accept loop; po vezi getsockopt(SO_PEERCRED) + getsockopt(SO_PEERGROUPS)
```

Nikad `unlink()` naslepo. Nikad unlink van `/run/internet-evidence-monitor/`. Nikad unlink ako realpath izađe iz tog direktorijuma.

Stop servisa: close fd; socket čvor sme da ostane (stale). Sledeći start ide kroz korak 3. `RuntimeDirectory` se briše pri stopu **osim** ako je `RuntimeDirectoryPreserve=yes`. 3.1: **ne** čuvati runtime dir — socket je ephemeral. Canonical state je u `StateDirectory`, ne ovde.

## 11.4 Pet slojeva autorizacije

```text
1. Filesystem admission          mode 0660 + traverse 0750
2. SO_PEERCRED authentication    uid, gid, pid sa kernela + SO_PEERGROUPS
3. Canonical role resolution     role:operator / role:admin / (nema role)
4. Command authorization         matrica komandi
5. Session-state authorization   vlasnik sesije, sealed, invarijanta 91
```

Svaki sloj je fail-closed. Prolazak kroz 1 ili 2 **nije** Allow.

### Sloj 2 — authentication

```text
ucred = SO_PEERCRED (uid, gid, pid sa kernela)
groups = SO_PEERGROUPS (primarni autoritet za stvarne grupe peer procesa na vezi)
fallback = /proc/<pid>/status Groups (kontrolisani fallback ako kernel/UAPI nema SO_PEERGROUPS)
dijagnostika = getgrouplist(uid) (NSS/account baza; NE koristiti kao authorization authority)
payload.uid / payload.role     → IGNORIŠI
neuspeo SO_PEERCRED ili neuspeo group resolution za zavisne role → PlatformPeerIdentity.Unknown → fail-closed
```

Neuspeo `SO_PEERCRED` ili nemogućnost pouzdanog utvrđivanja grupa za role koje zavise od njih → `PlatformPeerIdentity.Unknown` → sloj 4 vraća `AuthorizationOutcome.Unknown` (fail-closed).

### Sloj 3 — kanonske role

Nema substring `"Admin"` / `"root"`. Tačni claimovi, jedan po jedan:

| Uslov | Claim |
|---|---|
| uid == 0 | `role:admin` (system-admin) |
| član `iem-admin` | `role:admin` |
| član `iem-users` | `role:operator` |
| inače | nema role |

uid 0 je system-admin, ne magični string `"root"`. `iem-users` **nije** admin. Oba claima smeju da stoje istovremeno (admin je i operator).

Windows par:

| Windows peer | Claim |
|---|---|
| Administrators / Local System | `role:admin` |
| authenticated pipe user | `role:operator` |

`PrincipalId` ostaje uid odnosno SID. `principalRef` = `unix:{uid}` / `windows:{sid}`.

### Sloj 4 — komande

| Komanda | Ko sme |
|---|---|
| `GetServiceStatus` | `role:operator` ili `role:admin` |
| `GetActiveSession` | isto |
| `GetSessionStatus` | isto |
| `StartSession` | `role:operator` ili `role:admin` |
| `StopSession` | sloj 5 |
| `FinalizeSession` | sloj 5 |
| `RetryTimestamp` | sloj 5 |
| `CreateExport` | sloj 5 |
| sve ostalo | `Denied` |

Povezan `iem-users` bez role (ne bi smelo da se desi ako je grupa izvor role) → `Denied`, ne Allow.

### Sloj 5 — session state

Za Stop / Finalize / RetryTimestamp / CreateExport:

```text
sessionOwnerPrincipalRef missing or empty
        → Denied          // FAIL CLOSED
        ≠  "anyone may control"

principalRef == sessionOwnerPrincipalRef
        → Allowed         // session-owner (ordinal, scheme+id)

role:admin
        → Allowed

inače
        → Denied
```

Današnji kod (`isOwner || isAdmin || string.IsNullOrEmpty(sessionOwnerPrincipalRef)`) je rupa. 3.1-3 je briše na obe platforme.

Vlasnik se piše pri uspešnom `StartSession` kao `unix:{uid}` i ne menja se. Sealed sesija i dalje odbija mutacije (invarijanta 91) čak i za owner/admin.

`CreateExport` je jedini put do user-visible kopije. Ne otvara write na `/var/lib`.

---

# 12. systemd — host i testirana hardening matrica

## 12.1 Host

```text
IEM.Service.Linux
    Generic Host + UseSystemd
    Type=notify
    Restart=on-failure
    RestartSec=5s
    User=iem
    Group=iem
    SupplementaryGroups=iem-users
    After=local-fs.target          // NE network-online.target
```

Nema `.socket` unita. Nema `Requires=network-online.target`.

`WatchdogSec=30s` je namerno **izbačen iz minimalnog baseline unit-a**: Microsoft `.NET` `UseSystemd()` / `SystemdLifetime` automatski šalje startup i stopping notifikacije (`READY=1`, `STOPPING=1`), ali **ne** održava automatski runtime watchdog heartbeat (`WATCHDOG=1`). systemd zahteva periodično osvežavanje `WATCHDOG=1`. Bez eksplicitnog heartbeat loop-a, systemd bi ubio potpuno zdrav servis nakon 30s. Zato je `WatchdogSec=` definisan kao **CANDIDATE** hardening direktiva koja se aktivira tek kada se implementira namenski heartbeat sender i prođe integration test.

## 12.2 StateDirectory / RuntimeDirectory

```ini
StateDirectory=internet-evidence-monitor
StateDirectoryMode=0700

RuntimeDirectory=internet-evidence-monitor
RuntimeDirectoryMode=0750
```

`StateDirectory=` za system service jeste `/var/lib/internet-evidence-monitor`, ownership prati `User=`/`Group=`, **ne briše** se na `stop` — samo na `systemd-tmpfiles --remove` / uninstall koji to eksplicitno traži. To je canonical store.

Default systemd mode za StateDirectory je `0755`. **Ne oslanjati se na to.** `StateDirectoryMode=0700` je obavezan.

`RuntimeDirectory` jeste `/run/internet-evidence-monitor`, nestaje na stopu. Transport zatim doteruje grupu na `iem-users` (§11.2, omogućeno kroz `SupplementaryGroups=iem-users`).

`UMask=0077` na procesu: novi fajlovi u state tree 0600/0700. Socket i dalje ide kroz eksplicitni `chmod 0660` posle bind-a.

## 12.3 Baseline unit (pre hardening kandidata)

```ini
[Unit]
Description=Internet Evidence Monitor
After=local-fs.target
Documentation=file:/usr/share/doc/internet-evidence-monitor/README

[Service]
Type=notify
User=iem
Group=iem
SupplementaryGroups=iem-users
ExecStart=/usr/lib/internet-evidence-monitor/IEM.Service.Linux
Restart=on-failure
RestartSec=5s
StateDirectory=internet-evidence-monitor
StateDirectoryMode=0700
RuntimeDirectory=internet-evidence-monitor
RuntimeDirectoryMode=0750
UMask=0077

[Install]
WantedBy=multi-user.target
```

Ovo je **minimalni** unit koji sme da uđe u sliku pre nego što hardening matrica ozeleni. Hardening direktive ispod su CANDIDATE dok im integration test ne da REQUIRED.

## 12.4 Hardening matrica

Nijedna direktiva nije REQUIRED dok test ne potvrdi: rtnetlink, nl80211 (ako ima wiphy), ICMP/TCP/DNS/HTTP preflight, logind, signing, storage, IPC.

| Direktiva | Svrha | IEM capability koju sme da slomi | Acceptance | Fallback |
|---|---|---|---|---|
| `NoNewPrivileges=yes` | nema podizanja privilegija, ni preko exec | nijedna očekivana | servis start + ICMP datagram + bind source | CANDIDATE → REQUIRED kad prođe |
| `ProtectSystem=strict` | FS read-only osim StateDirectory izuzetka | pisanje van `/var/lib/internet-evidence-monitor`; slučajni write u `/usr` | sesija se kreira, ključ se provisionuje, evidence se piše **samo** u StateDirectory; write u `/tmp` servisa nije potreban. Običan read `/etc/internet-evidence-monitor/appsettings.json` je dozvoljen (strict čini FS read-only, ne blokira čitanje) | fallback na `ProtectSystem=full` ili bind-write izuzetke vezuje se isključivo za legitiman write van StateDirectory koji se pokaže neophodnim (ne za `/etc` read problem koji je pitanje ownership/permissions `root:iem 0640`) |
| `ProtectHome=yes` | nema pristupa `/home` | pisanje u `~/Documents/` iz servisa (i ne sme: u Installed modu servis kreira verifikovani export u service-owned staging-u, klijent ga preuzima preko IPC-a i klijentski proces upisuje finalni fajl; servis nikada ne pristupa proizvoljnom korisničkom home-u) | test: servis ne čita niti piše `$HOME`; export komanda ne zahteva ProtectHome=no | REQUIRED (ProtectHome=yes ostaje čist u baseline i release unit-u) |
| `PrivateTmp=yes` | privatni `/tmp` | temp fajlovi van StateDirectory | atomski `.tmp` u session dir i dalje radi | CANDIDATE |
| `ProtectKernelTunables=yes` | `/proc/sys` read-only | čitanje `boot_id`, `ping_group_range` ostaje read | preflight čita sysctl; ne piše sysctl (to radi paket) | CANDIDATE; drop samo ako read padne |
| `ProtectKernelModules=yes` | nema insmod | nijedna | start + probe | CANDIDATE |
| `ProtectControlGroups=yes` | nema write na cgroup FS | nijedna | start | CANDIDATE |
| `RestrictSUIDSGID=yes` | nema setuid/setgid binar | nijedna | start | CANDIDATE |
| `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK` | samo potrebne familije | D-Bus je AF_UNIX; rtnetlink/nl80211 su AF_NETLINK; ICMP/TCP/UDP su INET/INET6 | IPC + GETROUTE + GET_SCAN + ICMP/TCP/DNS + logind | ako netlink padne, **ne** dodavati `AF_NETLINK` „na slepo“ — prvo potvrditi da je to uzrok; bez NETLINK nema §5/§7 |
| `PrivateDevices=yes` | nema sirovih device čvorova | `/dev/rfkill`, `/dev/tpm0` (tpm nije 3.1) | RadioOn preko sysfs rfkill; ako treba `/dev/rfkill` → `DeviceAllow=` umesto gašenja cele direktive | CANDIDATE; rfkill sysfs prvo |
| `LockPersonality=yes` | nema personality | nijedna | start | CANDIDATE |
| `MemoryDenyWriteExecute=yes` | W^X | .NET JIT / interpreter | **ne uključivati** dok runtime test ne pokaže da self-contained .NET 10 živi sa tim; inače ostaje OFF | default OFF |
| `WatchdogSec=30s` | automatski restart ako servis zablokira | servis koji ne šalje periodični heartbeat biće ubijen | eksplicitni background heartbeat loop (`WATCHDOG=1` na svakih 10-15s) + integration test | CANDIDATE (isključen iz baseline unita dok se ne implementira i testira heartbeat sender) |

`ProtectSystem=strict` je dobar kandidat baš zato što `StateDirectory=` izuzima `/var/lib/internet-evidence-monitor` iz read-only stabla. To se testira: pokušaj write van StateDirectory mora da padne; write unutra mora da prođe.

Ako neka CANDIDATE direktiva slomi legitimnu capability, ona se **ne** ostavlja „da vidimo“. Ili se popravi (npr. `DeviceAllow`), ili ostaje isključena uz zapis u `LINUX-SERVICE-HARDENING` listi. Unit u repou uvek odgovara onome što je testirano.

## 12.5 Šta unit ne sme

```text
After=network-online.target          // meri baš odsustvo mreže
User=root
AmbientCapabilities=CAP_NET_ADMIN
Socket activation .socket            // 3.1
WorkingDirectory=/home/…
RuntimeDirectory pod 0777
StateDirectoryMode izostavljen       // default 0755 je preširok
systemd --user unit                  // 3.1; vidi §15A
Linger=yes kao zamena za system      // 3.1
```

---

# 13. Service Start Semantics

IEM servis **ne sme zavisiti od `network-online.target` kao uslova za rad**.

Pogrešan model:

```text
wait for network online
        ↓
start measuring network availability
```

IEM mora biti sposoban da se pokrene i kada mreža ne radi.

Odsustvo mreže je upravo predmet posmatranja.

---

# 14. Privilege Model

Zaključano (§4.0, §4.5).

Default:

```text
User=iem
Group=iem
SupplementaryGroups=iem-users
CAP_NET_RAW    → NE
CAP_NET_ADMIN  → NE
CAP_SYS_ADMIN  → NE
root           → NE
```

ICMP ide preko unprivileged datagram soketa. Paket tokom instalacije dinamički utvrđuje alocirani GID za `iem` i idempotentno podešava `net.ipv4.ping_group_range = <iem_gid> <iem_gid>`. To nije capability.

Ako ICMP socket vrati `EPERM`, ICMP je `Unavailable`. TCP, DNS i HTTP nastavljaju. To nije internet outage.

`SO_BINDTODEVICE` nije deo default modela. Source-address bind jeste, i to je paritet sa Windows `IcmpSendEcho2Ex`.

Opcioni drop-in sa `CAP_NET_RAW` sme da postoji. Default paket ga ne uključuje. Preflight mora da zabeleži da je uključen. Capability nikad ne podiže evidence trust (invarijanta 232).

---

# 15. Linux Storage — ownership i lifecycle

Zaključano 2026-08-19. Kanonski store je sistemski, servisni, `0700`. Nije user folder.

## 15.1 Mapiranje

| systemd | Putanja | Živi preko stop-a |
|---|---|---|
| `StateDirectory=internet-evidence-monitor` + `StateDirectoryMode=0700` | `/var/lib/internet-evidence-monitor` | da |
| `RuntimeDirectory=…` + `RuntimeDirectoryMode=0750` | `/run/internet-evidence-monitor` | ne |
| paket `/etc` | `/etc/internet-evidence-monitor/appsettings.json` | da |
| `CreateExport` cilj | service-owned staging → IPC preuzimanje → client write u user path | user copy |

`IPlatformStorageLayout` vraća baš ove putanje. `SpecialFolder.CommonApplicationData` se ne koristi.

## 15.2 Mode matrica

```text
/var/lib/internet-evidence-monitor/                 iem:iem        0700
    keys/                                           iem:iem        0700
        evidence-signing-v1.p8                      iem:iem        0600
    sessions/<id>/                                  iem:iem        0700
        Raw/                                        iem:iem        0700
        Evidence/                                   iem:iem        0700
        Derived/                                    iem:iem        0700
        Exports/                                    iem:iem        0700   # staging, nije user ACL
        layout.json                                 iem:iem        0600
    cases/                                          iem:iem        0700
    state/                                          iem:iem        0700
        capability-preflight.json                   iem:iem        0600

/run/internet-evidence-monitor/                     iem:iem-users  0750
    control.sock                                    iem:iem-users  0660

/etc/internet-evidence-monitor/                     root:iem       0750
    appsettings.json                                root:iem       0640
```

`LinuxSessionModeProvisioner` pri startu sesije postavlja zone. Drift (mode/owner/symlink) → `StorageProtectionState.Degraded` ili `NotEstablished`. Sesija se ne startuje ako granica nije `Established` (invarijanta 81).

`Exports/` unutar sesije je samo staging za atomski upis. Korisnik ga **ne** vidi preko ACL-a. U Installed (System) modu servis kreira verifikovani export u service-owned staging-u, autorizovani IPC klijent ga preuzima preko IPC-a (bounded/chunked stream) i klijentski proces upisuje finalni korisnički fajl (servis ne piše u `~/Documents/...` direktno). Direktan upis od strane servisa dozvoljen je eventualno samo u unapred konfigurisani service-accessible export direktorijum, nikada u proizvoljne korisničke home putanje, čime `ProtectHome=yes` ostaje 100% čist. GUI nema write i nema read na `/var/lib/internet-evidence-monitor`.

## 15.3 Šta uninstall / stop sme

| Događaj | StateDirectory | RuntimeDirectory | keys | evidence |
|---|---|---|---|---|
| `systemctl stop` | ostaje | nestaje | ostaje | ostaje |
| `systemctl restart` | ostaje | novi dir + novi socket | isti KeyId | čitljivo |
| package upgrade | ostaje | — | **isti** KeyId; nema tihe rotacije | verifikuje se ponovo |
| package remove (default) | **ostaje** | — | ostaje | ostaje |
| eksplicitan purge / `remove-data` | sme da obriše, uz potvrdu | — | sme | sme |

`LINUX_PACKAGE_REMOVAL_NEVER_SILENTLY_DELETES_CANONICAL_EVIDENCE` (236). Debian `postrm` na `remove` ne dira `/var/lib/…`; samo `purge` sme, i to dokumentovano.

## 15.4 Symlink / mount

Svaki write ide kroz `LinuxSymlinkGuard`: `lstat` svakog segmenta od StateDirectory naniže. Symlink, unexpected mount, unexpected owner → ne piši.

`ProtectSystem=strict` je dodatni sloj, ne zamena za ovo.

## 15.5 Invarijante 261–268 (draft, ovaj sloj)

## 261

`LINUX_CONTROL_SOCKET_IS_APP_OWNED_IN_31`

3.1 listener pravi `IIpcTransport`, ne systemd socket activation.

## 262

`ONE_UNIX_SOCKET_CARRIES_STATUS_AND_COMMANDS`

Linux ne uvodi drugi UDS da kopira Windows dual-pipe.

## 263

`STALE_SOCKET_UNLINK_IS_FAIL_CLOSED`

`unlink` samo nad potvrđenim `S_IFSOCK` čvorom, owner `iem`, u očekivanom runtime dir-u. Symlink / non-socket / tuđi owner → stop, ne unlink.

## 264

`MISSING_SESSION_OWNER_FAILS_CLOSED`

Prazan `sessionOwnerPrincipalRef` nije Allow za Stop/Finalize/Retry/Export.

## 265

`AUTHORIZATION_ROLES_ARE_CANONICAL_NOT_SUBSTRINGS`

Samo tačni claimovi `role:admin` i `role:operator`. `Contains("Admin")` / `Contains("root")` je zabranjen.

## 266

`STATE_DIRECTORY_MODE_IS_0700_SERVICE_OWNED`

Canonical `/var/lib/internet-evidence-monitor` je `iem:iem` `0700`. GUI nema ACL write ni read.

## 267

`EXPORT_NEVER_OPENS_CANONICAL_STORE_TO_THE_CLIENT`

`CreateExport` je jedini izlaz. Servis kreira verifikovani staging export a autorizovani klijent ga preuzima preko IPC-a (bounded/chunked streaming transport). Mode na `Raw/` / `Evidence/` se ne širi zbog GUI-ja i servis nikada ne kompromituje `ProtectHome=yes` direktnim pisanjem u korisnički home.

## 268

`SYSTEMD_HARDENING_IS_REQUIRED_ONLY_AFTER_CAPABILITY_TESTS`

Direktiva postaje REQUIRED tek kad integration test potvrdi §5, §7, IPC, signing i storage. Slomljena CANDIDATE se gasi ili sužava, ne ostavlja se na sreću.

---

# 15A. Execution modes — Installed vs Portable

Zaključano 2026-08-19. Poslednja velika 3.1 arhitektonska odluka.

## 15A.0 Odluka

3.1 ima **dva** Linux execution moda. Nema treći.

```text
1. Installed  = system systemd service          FULL continuity
2. Portable   = in-process, bez servisa         slabiji operational guarantee
3. systemd --user / linger                      OUT OF SCOPE
```

Windows već ima ove dve putanje: `ServiceMonitorHost` ako je servis instaliran, `InProcessMonitorHost` + `PortableOutputRoot` ako nije. Linux ne sme da izgubi portable, i ne sme da doda `--user` lifecycle.

## 15A.1 Zašto ne systemd --user

`$XDG_RUNTIME_DIR` živi uz login i nestaje kad poslednja sesija korisnika završi; mora biti `0700` user-owned.

User manager se gasi sa poslednjim loginom, osim uz linger. Linger bi uneo:

- posebnu instalaciju,
- Polkit/admin slučajeve,
- user-service persistence,
- drugi socket lifecycle,
- drugi key/storage ownership,
- treću acceptance matricu.

To ne daje ništa što system service već ne radi bolje. **3.1: nema `--user`, nema linger.** 3.2+ samo uz konkretan use-case.

## 15A.2 Dva moda

### Installed (system)

```text
Host:                 IEM.Service.Linux + system systemd
Runtime:              IEM.Service.Runtime
Store:                /var/lib/internet-evidence-monitor     iem:iem 0700
IPC:                  /run/internet-evidence-monitor/control.sock
Signing:              SystemInstallationIdentity
ExecutionMode:        SystemService
StorageAuthority:     ServiceOwned
ServiceContinuity:    Available
GUI/logout/reboot:    sesija živi
Release:              3.1 blocking; Tier A lane C obavezna
```

### Portable (in-process)

```text
Host:                 IEM.App.Linux / CLI  →  InProcessMonitorHost
Runtime:              isti IEM.Service.Runtime
Store:                $XDG_STATE_HOME/internet-evidence-monitor
IPC:                  nema control.sock
Signing:              PortableUserIdentity
ExecutionMode:        PortableInProcess
StorageAuthority:     UserOwned
ServiceContinuity:    NotAvailable
Lifetime:             owning process
Release:              3.1 supported fallback; nije zamena za system acceptance
```

Nema `LinuxPortableMonitor`. Portable je drugi **host**, isti engine.

```text
System:     IEM.Service.Linux  →  IEM.Service.Runtime  →  Evidence Engine
Portable:   InProcessMonitorHost → IEM.Service.Runtime  →  isti Evidence Engine
```

## 15A.3 Portable storage — XDG, ne Desktop

Windows `PortableOutputRoot` je Desktop. Linux **ne** kopira to.

```text
${XDG_STATE_HOME:-$HOME/.local/state}/internet-evidence-monitor/
    evidence/
    sessions/
    cases/
    keys/          # PortableUserIdentity, nije system key
    state/

${XDG_CONFIG_HOME:-$HOME/.config}/internet-evidence-monitor/
    appsettings.json

${XDG_CACHE_HOME:-$HOME/.cache}/internet-evidence-monitor/
    …

${XDG_RUNTIME_DIR}/internet-evidence-monitor/
    samo ephemeral (pid, lock); NIKAD evidence
```

**Pravilo validacije XDG okruženja:** Sve XDG environment varijable (`XDG_STATE_HOME`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`, `XDG_RUNTIME_DIR`) moraju biti striktno validirane pre upotrebe: moraju biti neprazne i **isključivo apsolutne putanje** koje počinju sa `/`. Relativne putanje ili nevalidne vrednosti se odbacuju bez kompromisa:
- Za `XDG_STATE_HOME`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME` nevalidna ili relativna vrednost pada na standardni fallback `$HOME/.local/state`, `$HOME/.config`, `$HOME/.cache`.
- `XDG_RUNTIME_DIR` je vezan za korisnički login session; ako nije validan/postavljen, efemeralno stanje ostaje u procesu. Nikada ne sme biti odredište za dokazne fajlove.

`XDG_STATE_HOME` je namena: user-specific persistent state. `XDG_RUNTIME_DIR` se briše na logout — tamo ne sme lanac.

`~/Desktop/InternetEvidence` i `~/Documents/IEM` nisu Linux portable default. Drugo je CreateExport odredište (user copy), ne store.

`LinuxPortableStorageLayout : IPlatformStorageLayout` vraća XDG putanje. `LinuxStorageLayout` vraća `/var/lib` / `/run`. Composition root bira layout prema modu. Shared `ServiceContract.PortableOutputRoot` (Desktop) se ne zove na Linuxu.

Ispod XDG state root-a važi **isti** `SessionLayoutDescriptor` (Raw / Evidence / Derived / Exports) kao u system sesiji. `evidence/` u listi iznad je taj session tree, ne drugi model.

## 15A.4 Semantika: isto značenje, slabije garancije

Portable evidence **nije** invalid. Isti model, lanac, verifier, klasifikacija.

Mora da piše provenance (manifest / acquisition context):

| | Installed | Portable |
|---|---|---|
| `ExecutionMode` | `SystemService` | `PortableInProcess` |
| `StorageAuthority` | `ServiceOwned` | `UserOwned` |
| `ServiceContinuity` | `Available` | `NotAvailable` |

Portable **ne sme** da tvrdi:

- da je monitoring preživeo zatvaranje GUI-ja,
- da preživljava logout,
- da korisnik nema write na store,
- da ga je system service čuvao,
- da ima service restart/reboot continuity.

`KeyProtection` portable: `SoftwareProtected` + evidence da je user-owned FS. Nikad formulacija koja implicira service-only ACL.

## 15A.5 Installation probe — ne boolean, ne silent fallback

`ServiceContract.IsInstalled()` sa `OperatingSystem.IsWindows()` izlazi iz shared koda. `IPlatformInstallationProbe` vraća **dva** ortogonalna rezultata:

```text
InstallationPresence
    InstalledSystemService     unit/fajl instalacije postoji
    PortableOnly               nema system unit-a
    Unknown                    probe nije mogao da utvrdi

ServiceReachability
    Reachable                  control.sock + protokol
    Unreachable                instaliran, IPC ne odgovara
    NotApplicable              PortableOnly
```

`unit exists` ≠ `service healthy`.

```text
PortableOnly
        → InProcessMonitorHost + XDG layout

InstalledSystemService + Reachable
        → ServiceMonitorHost + /var/lib layout

InstalledSystemService + Unreachable
        → ServiceUnavailable
        → retry / recovery UI
        → NE InProcessMonitorHost
        → NE drugi store
        → NE tihi portable

Unknown
        → fail-closed ka ServiceUnavailable
        → NE tihi portable
```

Dve paralelne istorije (servis u `/var/lib` + GUI u XDG) su evidence defekt. Zato silent fallback ne postoji.

Windows 3.1: isti split — registry kaže installed, pipe mrtav → ServiceUnavailable, ne Desktop portable sesija.

## 15A.6 Signing — dva namespace-a

```text
SystemInstallationIdentity     /var/lib/.../keys/evidence-signing-v1.p8
PortableUserIdentity           $XDG_STATE_HOME/.../keys/evidence-signing-v1.p8
```

Isti `IEvidenceKeyProvider`, **nikad** isti fajl. Nema čitanja system ključa iz portable procesa i obrnuto.

Oba: ECDSA P-256, fail-closed, nema tihe rotacije. `KeyId` su različiti. Report/manifest kaže koji je identity namespace.

## 15A.7 GUI / process close

```text
System:    GUI close  →  servis i sesija nastavljaju
Portable:  process close → observability CLOSED
                           session finalize / controlled interruption
                           nije network outage
```

8h zatvoren GUI u portable-u ≠ 8h outage. Isto pravilo kao suspend: nema observera → nema mrežne činjenice.

## 15A.8 Headless ≠ portable

| Scenario | Mod | Status |
|---|---|---|
| Server / Pi / mini PC, paket instaliran, bez GUI | **Installed** + CLI | full supported |
| `iem --portable` / raspakovan `.tar.gz`, bez systemd | Portable CLI | supported fallback, slabiji lifecycle |
| systemd --user na headless | — | nije 3.1 |

Raspberry Pi bez monitora i dalje ide system service, ne user-mode.

## 15A.9 Release

| Mod | 3.1 |
|---|---|
| System | release-blocking; Tier A lane C |
| Portable | supported fallback; characterization + functional + §25 four-way; **nije** zamena za C |
| systemd --user | not supported |

## 15A.10 Golden parity

```text
parity.mode.four-way
    isti semanticInput
    × Windows Service
    × Windows Portable
    × Linux Service
    × Linux Portable
```

`CanonicalParityView` (classification, outage, claims) isti. `allowedDivergences` samo:

```text
ExecutionMode
StorageAuthority
ServiceContinuity
platform metadata
path continuity / scan freshness   // već §25
```

Portable ne sme da proizvede jači continuity claim od system.

## 15A.11 Invarijante 274–282

## 274

`LINUX_HAS_NO_SYSTEMD_USER_SERVICE_IN_31`

Nema `--user`, nema linger.

## 275

`PORTABLE_IS_A_HOST_NOT_A_SECOND_ENGINE`

Samo `InProcessMonitorHost` + isti runtime.

## 276

`INSTALLED_UNREACHABLE_NEVER_FALLS_BACK_TO_PORTABLE`

Servis postoji a IPC ćuti → `ServiceUnavailable`, ne XDG sesija.

## 277

`PORTABLE_STORE_IS_XDG_STATE_NOT_DESKTOP`

Default je `XDG_STATE_HOME` uz striktnu proveru neprazne apsolutne putanje (fallback `~/.local/state`). Ne `~/Desktop`. Ne relativne/nevalidne putanje.

## 278

`PORTABLE_MUST_NOT_CLAIM_SERVICE_CONTINUITY`

`ServiceContinuity=NotAvailable`. GUI/logout/reboot nisu garantovani.

## 279

`PORTABLE_AND_SYSTEM_SIGNING_IDENTITIES_NEVER_SHARE_A_KEY`

Dva namespace-a, dva `KeyId`, nema čitanja preko granice.

## 280

`PORTABLE_PROCESS_EXIT_IS_OBSERVABILITY_END_NOT_OUTAGE`

Kraj procesa seče merenje kao gap, ne kao downtime.

## 281

`HEADLESS_IS_NOT_PORTABLE`

Installed headless je full mode.

## 282

`INSTALLATION_PROBE_IS_NOT_A_BOOLEAN`

`Installed` i `Reachable` su odvojeni. Shared kod ne grana po `IsWindows()`.

---

# 16. Linux Evidence Signing

Postojeći cross-platform contract ostaje:

```text
IEvidenceSigningIdentity
IEvidenceKeyProvider
```

Linux implementacija:

```text
LinuxEvidenceKeyProvider
LinuxEvidenceSigningIdentity
```

## 16.1 3.1 baseline

- ECDSA P-256
- **dva** namespace-a: `SystemInstallationIdentity` (`/var/lib/.../keys`) i `PortableUserIdentity` (`$XDG_STATE_HOME/.../keys`) — §15A.6
- ne dele fajl ni `KeyId`
- system: service-only `0700`/`0600`
- portable: user-owned; `SoftwareProtected` bez service-ACL claim-a
- atomic provisioning
- stable `KeyId` unutar svog namespace-a
- fail-closed access

Protection claim:

```text
SoftwareProtected
```

dok nije dokazano jače.

Nikada ne emitovati `TpmBacked` samo zato što TPM postoji.

---

## 16.2 Future TPM2 provider

Kasnije:

```text
LinuxTpm2EvidenceKeyProvider
```

`TpmBacked` se priznaje samo uz stvarnu hardware-backed proveru / attestation gde je moguće.

---

## 16.3 Identity preservation

Ako postojeći ključ ne može da se otvori:

```text
SigningIdentityUnavailableException
```

ili semantički ekvivalent.

Nikada:

```text
old key failed
   ↓
silently create new key
```

---

# 17. Presentation Architecture

Windows WPF aplikacija ostaje stabilna.

Ne raditi veliki WPF→Avalonia rewrite za Windows.

Cilj:

```text
IEM.App           = Windows WPF
IEM.App.Linux     = Linux Avalonia
```

Zajednički presentation sloj:

```text
IEM.Presentation
```

Sadrži:

- `PresentationSnapshot`
- revision tracking
- ViewModels
- semantic tokens
- common UI semantics

Arhitektura:

```text
                 IEM.Presentation
                 /              \
                /                \
          Windows WPF        Linux Avalonia
```

Jedna presentation semantika.

Dva renderera.

---

# 18. Linux GUI — Avalonia

Linux UI:

```text
IEM.App.Linux
```

Glavne celine ostaju iste:

```text
MONITOR
EVIDENCE
CASE
SPEED
```

UI nikada ne kreira niti reinterpretira evidence semantics.

U **Installed** modu GUI može da se zatvori bez prekida servisa. U **Portable** modu zatvaranje procesa zatvara observability — to nije outage (§15A.7).

---

# 19. X11 / Wayland Strategy

Za 3.1 stable:

```text
X11                SUPPORTED
Wayland/XWayland    SUPPORTED

Native Wayland      PREVIEW
```

Native Wayland ne sme biti uslov za evidence operation.

Display backend nikada ne ulazi u dokaznu semantiku.

Ako GUI padne u Installed modu:

```text
GUI failure
```

ne sme postati:

```text
measurement failure
```

Portable: nestanak procesa je kraj merenja (gap), ne `Failed` probe i ne outage.

---

# 20. Linux Distribution Support Matrix

Zaključano 2026-08-19. Stari nacrt (Ubuntu 25.x / Fedora 43 kao Tier A) je **pogrešan** na ovaj datum.

| Distro | Stanje 19.08.2026. |
|---|---|
| Ubuntu 26.04 LTS | aktuelni LTS, podrška do 2031; `.NET 10` podržan |
| Ubuntu 25.10 | izašao iz standardne podrške jula 2026 — **nije** IEM target |
| Ubuntu 24.04 LTS | LTS do 2029; hosted GitHub runner; Tier B |
| Debian 13 trixie | stable, point 13.6; `.NET 10` podržan |
| Debian 12 | oldstable; Tier B |
| Fedora 44 | aktuelna stable (28.04.2026); `.NET 10` podržan |
| Fedora 43 | još navedena uz `.NET 10`; nije primarni target pored 44 |

## 20.1 Šta obećavamo korisniku

**Support target** je *distro release*, ne jedan kernel build i ne jedan image digest.

```text
Podržavamo Ubuntu 26.04 LTS na x64.
Ne podržavamo samo kernel 6.x.y-zz sa slike od 3. marta.
```

Pin u CI-u služi reprodukciji testa. Point/security update unutar istog release-a sme da promeni kernel; zato postoji CURRENT lane (§31.5).

## 20.2 Tier A — release blocking (x64)

```text
Ubuntu 26.04 LTS    x64
Debian 13           x64
Fedora 44           x64
```

Nijedan 3.1.0-rc1 se ne izdaje ako VM acceptance (§31.C) nije zelen na sva tri.

GitHub-hosted `ubuntu-26.04` je 19.08.2026. još public preview. **Ne sme** biti jedini Tier-A autoritet. Ubuntu 26.04 se prihvata na **pravoj VM slici** (lane C), ne na preview runneru.

## 20.3 Tier B — compatibility (x64)

```text
Ubuntu 24.04 LTS    x64     (i dalje LTS; hosted fast CI živi ovde)
Debian 12           x64
Fedora 43           x64
```

Best-effort: package install + unit/integration gde stane. Nije release blocker. Pad se beleži; ne obara 3.1 osim ako otkrije shared-core regresiju.

## 20.4 Arhitektura

```text
x64     3.1 stable
arm64   preview dok puna acceptance matrica (§31.C) ne bude zelena
                    na bar jednom Tier-A distro-u
```

`linux-arm64` paket sme da se objavi kao preview. Ne piše se „supported“ u README dok lane C ne prođe.

## 20.5 Community / experimental

```text
Arch, NixOS, Gentoo, Alpine, ostalo
```

Nema zvaničnog support-a. `.tar.gz` / source build po želji.

## 20.6 Šta nije target

```text
Ubuntu 25.04 / 25.10
Fedora 42 i starije
Debian 11
bilo šta bez systemd kao PID 1, kao 3.1 system-service baseline
```

---

# 21. Packaging Strategy

Stable Linux distribucija:

```text
.deb
.rpm
```

Arhitekture:

```text
linux-x64      stable  (Tier A)
linux-arm64    preview (§20.4)
```

Opcionalno:

```text
.tar.gz
```

za CLI/verifier/headless scenario.

---

# 22. Flatpak / Snap Policy

Ne koristiti kao primarni deployment za 3.1.

IEM zahteva:

- system daemon
- kernel networking observability
- D-Bus/systemd integraciju
- `/var/lib`
- `/run`
- trusted local IPC

Sandboxed desktop package nije prirodna glavna distribucija.

Kasnije može postojati GUI-only Flatpak/Snap koji razgovara sa host servisom.

---

# 23. Linux Release & Supply Chain Integrity

Linux release mora zadržati 3.0 filozofiju:

```text
ReleaseIdentity
ReleaseManifest
SBOM
SHA-256
artifact inventory
build provenance
```

Za `.deb` / APT:

- signed repository metadata
- `InRelease`
- repository-specific key
- `Signed-By`

Za `.rpm`:

- GPG package signing
- package signature verification

Dodatni sloj:

```text
Sigstore / Cosign
```

Potpisivati:

```text
.deb
.rpm
SBOM
release-manifest.json
```

Cosign nije zamena za distro signing, već dodatni provenance sloj.

---

# 24. Network Namespace Test Harness

Linux daje veliku prednost za determinističko testiranje kroz network namespaces.

Topologija:

```text
IEM namespace
     │
    veth
     │
router namespace
     │
fake internet targets
```

Automatski fault injection:

```text
link down
gateway loss
route loss
DNS failure
TCP failure
HTTP failure
single-probe failure
all-probe failure
IPv4 failure / IPv6 success
IPv6 failure / IPv4 success
route switch
VPN route
gateway reachable / internet unreachable
```

Ovo treba postati jedan od glavnih Linux integration test mehanizama.

---

# 25. Cross-Platform Golden Parity — fixture format

Zaključano 2026-08-19. Verification sloj preko §5 (path/probes) i §7 (Wi-Fi).

## 25.1 Šta parity dokazuje, a šta ne

Golden parity **ne** dokazuje da Windows i Linux vide iste native činjenice.

Dokazuje:

> Kad njihove native činjenice predstavljaju **isti stvarni događaj**, Core daje **isto kanonsko značenje**.

Platform-specific observability se modeluje eksplicitno. Nikad se ne sakriva iza generičkog „ignore platform metadata“.

```text
stvarni događaj          SemanticInput.world
        │
        ├── Windows adapter  →  PlatformFacts.windows  →  ProbeCycle
        └── Linux adapter    →  PlatformFacts.linux    →  ProbeCycle
                    │
                    ▼
                 IEM.Core
        classifier / incidents / quality / claims / reports
                    │
                    ▼
          CanonicalParityView     (RFC 8785)
                    │
                    ▼
          diff po JSON path-u
          allowed | forbidden
```

Windows aktivni sken, Linux `GET_SCAN` keš, Live vs polling observer — to su različite činjenice. Fixture to piše. Core se poredi posle projekcije, ne sirovi nl80211 dump sa Win32 dump-om.

## 25.2 Tri odvojena dela

Svaki fixture je jedan JSON objekat, `parityFixtureVersion` + tri imenovana dela. Nisu opciona i ne smeju se spojiti.

| Deo | Sme da se razlikuje W/L | Ulazi u kanonski hash |
|---|---|---|
| `semanticInput` | ne — jedna priča | ne (ulaz) |
| `platformFacts.windows` / `.linux` | **da** | ne |
| `expectedCanonicalOutput` | samo kroz `allowedDivergences` | da, posle normalizacije |

```text
tests/IEM.Core.Tests/Fixtures/Parity/v1/<fixtureId>.json
```

`v1` prati `parityFixtureVersion`. Nova verzija formata = novi direktorijum, stari fixture-i ostaju čitljivi.

## 25.3 Zaglavlje

```json
{
  "parityFixtureVersion": 1,
  "fixtureId": "parity.wifi.ssid-gone.asymmetric-scan",
  "scenario": "Adapter down, radio on, SSID nestao sa etra; Linux nema complete scan",
  "intent": "Windows sme WifiRadioDown; Linux sa SsidVisible=null ostaje AdapterDown. SessionVerdict.Kind oba LocalFault.",
  "kind": "asymmetric-observability",
  "tags": ["wifi", "scan", "attribution"],
  "semanticInput": {},
  "platformFacts": { "windows": {}, "linux": {} },
  "expectedCanonicalOutput": {},
  "allowedDivergences": [],
  "pathWhitelist": []
}
```

| Polje | Pravilo |
|---|---|
| `parityFixtureVersion` | ceo broj. Tiha promena formata je zabranjena. Test odbija fixture čija verzija nije podržana |
| `fixtureId` | stabilan, `parity.<domain>.<name>[.<variant>]`, Ordinal, jedinstven |
| `scenario` | ljudski naslov |
| `intent` | šta mora ostati isto i koja asimetrija je namerna |
| `kind` | `symmetric` \| `asymmetric-observability` \| `adversarial` |

`kind=symmetric`: `allowedDivergences` prazan. Obe projekcije → identičan `CanonicalParityView`.

`kind=asymmetric-observability`: bar jedna stavka u `allowedDivergences`, sa razlogom.

`kind=adversarial`: namerno loš / granični ulaz (TOCTOU, skip, suspend). Može biti symmetric ili sa navedenom asimetrijom.

## 25.4 Tagged values — null, Unavailable, Skipped, Unknown, odsutno

JSON `null`, odsutno polje, `ProbeOutcome.Skipped` i capability `Unavailable` **nisu isto**. Fixture to taguje. Goli JSON `null` je zabranjen u ovom formatu.

```json
{ "t": "v", "v": true }
{ "t": "v", "v": "WifiRadioDown" }
{ "t": "v", "v": 42 }

{ "t": "null" }          // Core bool? / reference null — „provereno, nije utvrđeno“
{ "t": "unknown" }       // tri-state unknown (RadioOn unknown, PathContinuity.Unknown)
{ "t": "unavailable" }   // capability/preflight ćelija; kod u "code"
{ "t": "skipped" }       // ProbeOutcome.Skipped; nije attempt
{ "t": "absent" }        // polje ne postoji u ovom ciklusu (nema ICMP mete, nema Wireless)
```

| Tag | Ulazi u `WasAttempted` | sme da proizvede `WifiRadioDown` | sme u loss imenilac |
|---|---|---|---|
| `v:false` (`SsidVisible`) | n/a | da, uz `RadioOn==true` i link down | n/a |
| `null` | n/a | **ne** | n/a |
| `unknown` | n/a | **ne** | n/a |
| `unavailable` | ne | ne | **ne** |
| `skipped` | **ne** | ne | **ne** |
| `absent` | nema probe reda | ne | ne |

Harness odbija fixture koji napiše JSON `null` ili izostavi tag tamo gde je tagged value obavezan (`RadioOn`, `SsidVisibleInScan`, `Path.Resolved`, `Outcome`).

## 25.5 SemanticInput — jedna priča

Nije klasifikacija. Nije Windows ni Linux. To je stvarni događaj koji oba adaptera posmatraju.

```json
"semanticInput": {
  "session": {
    "plannedDuration": "PT2M",
    "medium": "Wireless"
  },
  "ticks": [
    {
      "seq": 1,
      "monotonicMs": 0,
      "wallUtc": "2026-08-19T10:00:00Z",
      "link": { "status": "Up", "hasGateway": true, "medium": "Wireless" },
      "world": {
        "internetReachable": { "t": "v", "v": true },
        "gatewayReachable": { "t": "v", "v": true },
        "radioSwitch": { "t": "v", "v": "on" },
        "associated": { "t": "v", "v": true },
        "ssidOnAir": { "t": "v", "v": true },
        "hostObservability": { "t": "v", "v": "awake" }
      },
      "probes": {
        "intent": "all-families-eligible"
      }
    }
  ]
}
```

`world.*` opisuje stvarnost, ne merenje. `ssidOnAir=true` ne znači da će Linux imati `SsidVisibleInScan=true`.

Pravilo konzistentnosti (linter fixture-a):

```text
PlatformFacts ne smeju biti JAČE od world.
  world.ssidOnAir=unknown  →  nijedan adapter ne sme SsidVisible=false niti true kao sigurnu činjenicu
  world.ssidOnAir=true     →  sme true, sme null (slabije); ne sme false
  world.ssidOnAir=false    →  sme false, sme null; ne sme true

Slabija observability je dozvoljena.
Jača tvrdnja od world-a je malformed fixture, ne parity fail.
```

`probes.intent` govori koje familije svet sme da izvrši (`all-families-eligible`, `icmp-v4-denied`, `no-icmp`). To nije ishod.

## 25.6 PlatformFacts — ovde žive razlike

Svaka strana je ono što bi adapter **stvarno** predao Core-u posle §5 / §7 pravila, za isti `semanticInput`.

```json
"platformFacts": {
  "windows": {
    "ticks": [
      {
        "seq": 1,
        "radioOn": { "t": "v", "v": true },
        "associated": { "t": "v", "v": false },
        "ssidVisibleInScan": { "t": "v", "v": false },
        "scan": {
          "source": "TriggeredScan",
          "completeness": "Complete",
          "ageMs": 2000
        },
        "path": {
          "resolved": { "t": "v", "v": true },
          "bound": { "t": "v", "v": true },
          "continuity": { "t": "unknown" },
          "interfaceId": { "t": "v", "v": "{guid}" },
          "sourceAddress": { "t": "v", "v": "192.168.1.50" }
        },
        "icmpV4": { "t": "v", "v": "TimedOut" },
        "tcp": { "t": "v", "v": "Failed" },
        "provenance": {
          "routeLookup": "GetBestRoute2",
          "scan": "NativeWifi.ScanNetworksAsync"
        }
      }
    ]
  },
  "linux": {
    "ticks": [
      {
        "seq": 1,
        "radioOn": { "t": "v", "v": true },
        "associated": { "t": "v", "v": false },
        "ssidVisibleInScan": { "t": "null" },
        "scan": {
          "source": "KernelBssCache",
          "completeness": "Partial",
          "ageMs": { "t": "unknown" }
        },
        "path": {
          "resolved": { "t": "v", "v": true },
          "bound": { "t": "v", "v": true },
          "continuity": { "t": "v", "v": "Held" },
          "ifindex": { "t": "v", "v": 3 },
          "interfaceId": { "t": "v", "v": "wlx..." },
          "sourceAddress": { "t": "v", "v": "192.168.1.50" },
          "routeGeneration": { "t": "v", "v": 12 }
        },
        "icmpV4": { "t": "v", "v": "TimedOut" },
        "tcp": { "t": "v", "v": "Failed" },
        "provenance": {
          "routeLookup": "RTM_GETROUTE",
          "scan": "NL80211_CMD_GET_SCAN"
        }
      }
    ]
  }
}
```

Projekcija u Core je **deterministička** i deo ugovora:

| PlatformFacts | Core |
|---|---|
| `radioOn` | `WirelessSnapshot.RadioOn` |
| `ssidVisibleInScan` | `WirelessSnapshot.SsidVisibleInScan` |
| `associated` + ssid/bssid | `IWirelessRadio.ReadAssociation` → reader |
| `scan.ageMs` > 180000 ili completeness≠Complete i value=false | forsira `SsidVisibleInScan` na `null` (linter; Linux adapter već to radi) |
| `icmpV4: skipped/unavailable` | `ProbeResult.Outcome=Skipped` |
| `path.resolved/bound` | `ProbePath` |
| `path.continuity` | sidecar; **ne** menja `ProbePath.Resolved` |
| `provenance.*` | ne ulazi u `CanonicalParityView` |

`interfaceId` string sme da se razlikuje (`{guid}` vs `wlx`). Kanonski view koristi **stabilan alias** iz fixture-a (`"if:monitored"`), ne sirovi OS id. Sirovi id ostaje u PlatformFacts.

## 25.7 ExpectedCanonicalOutput — šta mora biti isto

Ovo je `CanonicalParityView`: podskup Core izlaza koji nosi značenje. Nema ifindex, nema nl80211 attr, nema Win32 status teksta, nema `TechnicalDetail` slobodnog stringa ako nije stabilan kod.

```json
"expectedCanonicalOutput": {
  "samples": [
    {
      "seq": 1,
      "networkState": "AdapterDown",
      "isOutage": true,
      "anyExternalReachability": false,
      "tallies": {
        "gateway": { "attempted": 0, "succeeded": 0 },
        "externalIcmp": { "attempted": 3, "succeeded": 0 },
        "externalTcp": { "attempted": 2, "succeeded": 0 }
      },
      "pathProvesLink": true
    }
  ],
  "incidents": [
    {
      "index": 1,
      "worstState": "AdapterDown",
      "monotonic": { "firstBadMs": 0, "lastBadMs": 5000, "firstGoodMs": { "t": "null" } },
      "endedByGap": false,
      "routeChanged": false
    }
  ],
  "sessionVerdictKind": "LocalFault",
  "quality": {
    "pathAttribution": "Reduced",
    "outageDuration": "Moderate"
  },
  "claims": {
    "supportsComplaint": false,
    "namesOperatorAsFault": false,
    "wifiRadioBlamed": false
  }
}
```

Obavezna identična polja (osim `allowedDivergences`):

| Putanja | Tip | Zašto |
|---|---|---|
| `samples[*].networkState` | `NetworkState` | klasifikacija |
| `samples[*].isOutage` | bool | outage da/ne |
| `samples[*].anyExternalReachability` | bool | quorum §5.12 |
| `samples[*].tallies.*` | attempted/succeeded | Skip nije attempt |
| `incidents[*].worstState` | `NetworkState` | segment |
| `incidents[*].monotonic.firstBadMs` / `lastBadMs` / `firstGoodMs` | ms | interval; monotonic, ne wall |
| `incidents[*].endedByGap` | bool | suspend seče segment, ne produžava outage |
| `sessionVerdictKind` | `VerdictKind` | TooShort / Stable / LocalFault / UpstreamFault |
| `quality.*` | band po `QualityPurpose` | sem kada fixture dozvoli PathQuality asimetriju |
| `claims.supportsComplaint` | bool | `SessionVerdict.SupportsComplaint` |
| `claims.namesOperatorAsFault` | bool | uvek `false` (3.0 filozofija) |
| `claims.wifiRadioBlamed` | bool | `true` samo ako je neki sample `WifiRadioDown` |

`TechnicalDetail` (engleska rečenica classifiera) **nije** u view-u. Menja se bez promene značenja. `NetworkState` je ključ.

`ConfidenceScore` brojevi (support/coverage) ulaze samo ako ih fixture eksplicitno traži. Default poredi `ConfidenceBand` i `EvidenceItem.Key`+`Outcome`, ne slobodan tekst.

## 25.8 Whitelist je lista JSON path-ova, ne glob „platform“

Dve liste, obe eksplicitne.

### Globalni registar (`parityFixtureVersion = 1`)

Samo ovi path-ovi smeju da se razlikuju **u svakom** fixture-u, bez navođenja. To nisu semantika.

```text
$.platformFacts
$.samples[*].pathAlias              // već normalizovan; obično isti
$.canonicalView._meta.platform      // "Windows" | "Linux" ako view uopšte nosi meta
```

Sirovi PlatformFacts se **ne kopiraju** u `CanonicalParityView`. Zato globalni registar ostaje kratak. Nema `$.**` i nema `ignore all *Provenance*`.

### Per-fixture `pathWhitelist`

Retko. Koristi se za dijagnostička polja koja su slučajno procurila u view tokom razvoja, uz razlog. Svaki unos:

```json
{
  "path": "$.samples[0].debugNativeStatus",
  "reason": "privremeni debug; ukloniti pre 3.1.0-rc1"
}
```

Wildcard `[*]` sme samo za indeks niza sample/incident. `.*` na kraju (bilo koje dete) je **zabranjen**.

### Per-fixture `allowedDivergences`

Tačna očekivana asimetrija:

```json
"allowedDivergences": [
  {
    "path": "$.samples[0].networkState",
    "windows": "WifiRadioDown",
    "linux": "AdapterDown",
    "reason": "Windows ima complete triggered scan (SsidVisible=false). Linux ima Partial GET_SCAN keš (null). WifiRadioDown zahteva RadioOn==true && SsidVisible==false. Connectivity: oba isOutage=true, sessionVerdictKind=LocalFault."
  },
  {
    "path": "$.claims.wifiRadioBlamed",
    "windows": true,
    "linux": false,
    "reason": "Ista asimetrija atribucije; complaint claim ostaje false na obe strane."
  }
]
```

Ako se Windows ili Linux vrednost razlikuje od navedene — **forbidden**, čak i ako je path „sličan“.

Sve što nije u globalnom registru, `pathWhitelist`, ili `allowedDivergences` = **release failure**.

## 25.9 Diff koji test štampa

Failure nije `Assert.Equal` na celom JSON-u. Harness ispisuje red po path:

```text
FORBIDDEN  $.samples[0].networkState
           windows: WifiRadioDown
           linux:   AdapterDown
           reason:  not in pathWhitelist or allowedDivergences

ALLOWED    $.samples[0].networkState
           windows: WifiRadioDown
           linux:   AdapterDown
           reason:  allowedDivergences[0]: Linux scan null cannot satisfy WifiRadioDown

FORBIDDEN  $.samples[1].tallies.externalIcmp.attempted
           windows: 3
           linux:   0
           reason:  ICMP skip must be modeled as Skipped in both PlatformFacts for this symmetric fixture
```

Svaki red: path → Windows value → Linux value → allowed/forbidden + reason.

CI log mora moći da se čita bez otvaranja fixture fajla. Exit code ≠ 0 ako postoji bar jedan `FORBIDDEN`.

## 25.10 Kanonska serijalizacija

Poredi se `CanonicalParityView` propušten kroz `JsonCanonicalizer` (RFC 8785 JCS), ne sirovi paket, ne PlatformFacts, ne HTML izveštaj.

```text
view (objekt)
  → ukloni pathWhitelist polja
  → zameni allowedDivergences vrednosti placeholder-om
       { "t": "diverged", "id": "allowedDivergences[0]" }
  → RFC 8785 bytes
  → SHA-256
```

Windows hash i Linux hash moraju biti jednaki posle te normalizacije.

Sirovi `manifest.json` sesije **nije** ovaj test. Manifest i dalje sme da nosi `platform: Linux` (provenance). Cross-package verifikacija je §26. Ovaj sloj poredi značenje, ne potpis.

Zabranjeno:

- poređenje `TechnicalDetail` stringova,
- poređenje ifindex / GUID / `nlmsg_seq`,
- `ToString()` na native exception,
- pretty-print JSON sa razmacima.

## 25.11 Slabija Linux observability

Pravilo (invarijanta 258):

```text
Ako Linux vidi MANJE od Windowsa o istom događaju,
canonical verdict sme biti jednak ili SLABIJI (manje precizna atribucija).
Ne sme biti JAČI.
```

| Windows ulaz | Linux ulaz | Windows izlaz | Linux izlaz | Parity |
|---|---|---|---|---|
| `SsidVisible=false`, `RadioOn=true`, link down | isto | `WifiRadioDown` | `WifiRadioDown` | symmetric, mora biti isto |
| `SsidVisible=false`, … | `SsidVisible=null` | `WifiRadioDown` | `AdapterDown` | asymmetric, **mora** biti u `allowedDivergences` |
| `SsidVisible=null` | `SsidVisible=null` | `AdapterDown` | `AdapterDown` | symmetric |
| `SsidVisible=null` | `SsidVisible=false` | `AdapterDown` | `WifiRadioDown` | **FORBIDDEN** uvek — Linux ne sme da izmisli jači claim iz slabijeg ili jednakog world-a bez činjenice |
| ICMP 3/3 timeout, TCP 2/2 ok | ICMP skipped, TCP 2/2 ok | not outage (`Ok` / filter) | not outage | symmetric na `isOutage=false`; tally.icmp sme da divergira **samo** ako je u `allowedDivergences` (attempted 3 vs 0) |
| `PathContinuity=Unknown` | `Held` | ista `networkState` | ista | `Held` ne sme da promeni verdict; sme jači provenance, ne jači claim |
| `Unknown` | `ChangedDuringExecution` | ista `networkState` | ista `networkState`; PathQuality Reduced | `networkState` isto; quality path sme u `allowedDivergences` |

`WifiRadioDown` na Linuxu bez `SsidVisible=false` je uvek harness failure, i pre diff-a (projekcija krši §7).

## 25.12 Obavezni katalog (v1)

Svaki red je poseban `fixtureId`. Skup je release-blocking za 3.1-12.

### Symmetric — isti verdict

| `fixtureId` | Priča | Očekivano |
|---|---|---|
| `parity.healthy.dual-stack` | sve familije success | `Ok`, nema incidenta, `Stable` |
| `parity.outage.gateway-down` | adapter up, gateway ICMP fail, nema external | `GatewayDown`, `LocalFault` |
| `parity.outage.cpe-upstream` | gateway ICMP ok, svi external fail | `CpeUpstreamUnreachable`, `UpstreamFault` |
| `parity.filter.icmp-timeout-tcp-ok` | ICMP 3/3 timeout, TCP success | nije outage |
| `parity.dns.isp-fail-same-family` | assigned IPv4 fail, public IPv4 ok | `DnsIspFailure` |
| `parity.wifi.radio-on-ssid-gone.symmetric` | oba complete scan, SSID absent, radio on, link down | oba `WifiRadioDown` |
| `parity.wifi.radio-null.link-down` | `RadioOn=null`, link down | oba `AdapterDown` |
| `parity.wifi.stale-scan` | scan age > 3 min, SSID „odsutan“ u starom kešu | `SsidVisible=null`, `AdapterDown` |
| `parity.suspend.not-outage` | 50 min suspend, hostObservability=asleep | segment `endedByGap`, nije 50 min outage |
| `parity.reboot.not-outage` | boot identity change, nema mrežnih fail | nije network outage |
| `parity.unresolved-route.tcp-runs` | route unresolved, TCP fail/success po world | `pathProvesLink=false`; TCP u tally |
| `parity.bind-fail.tcp-skipped` | TCP bind EADDRNOTAVAIL | TCP `Skipped`, nije loss |

### Asymmetric observability

| `fixtureId` | Windows | Linux | Dozvoljena razlika |
|---|---|---|---|
| `parity.wifi.ssid-gone.asymmetric-scan` | `SsidVisible=false` (triggered, complete) | `SsidVisible=null` (partial keš) | `networkState` WifiRadioDown vs AdapterDown; `sessionVerdictKind` oba `LocalFault`; `wifiRadioBlamed` true vs false |
| `parity.wifi.partial-scan-vs-complete` | complete, SSID true | partial, SSID true | **nema** razlike u `networkState` (oba smeju true iz partial); symmetric claim |
| `parity.path.tocou-route-change` | `continuity=Unknown` | `ChangedDuringExecution` | ista `networkState` i interval; `quality.pathAttribution` sme Reduced samo na Linuxu |
| `parity.path.observer-polling` | Unknown | Unknown (polling) | identičan view; nijedan `Held` |
| `parity.icmp.v4-denied.tcp-ok` | ICMP IPv4 executed+ok ili timeout po world | ICMP IPv4 `skipped` | `tallies.externalIcmp.attempted` 3 vs 0; `isOutage` isto (TCP drži) |
| `parity.icmp.v4-denied.v6-ok` | obe familije ICMP | IPv4 skipped, IPv6 executed | ICMP tally samo po izvršenom; nije outage ako v6 ili TCP drži |
| `parity.vpn.default-route-flip` | route change Wi-Fi→wg | isto + `ChangedDuringExecution` na inflight | nije outage zbog VPN-a; `routeChanged` na incidentu ako ga ima |
| `parity.dual-stack.v4-down-v6-up` | IPv4 fail, IPv6 success | isto | nije „internet down“; familije se ne prosečavaju |
| `parity.nm.association-conflict` | Native Wi-Fi BSSID-A | nl80211 BSSID-A, NM profile B | isti `networkState`; NM nije u view |
| `parity.mode.four-way` | Win Service + Win Portable + Linux Service + Linux Portable, isti world | isti `networkState` / interval / `sessionVerdictKind`; diverge samo `ExecutionMode` / `StorageAuthority` / `ServiceContinuity` | portable ne sme jači continuity claim |

### Adversarial

| `fixtureId` | Šta lomi ako je pogrešno |
|---|---|
| `parity.adversarial.skip-as-loss` | ICMP skipped upisan kao Failed → lažni outage |
| `parity.adversarial.null-as-ssid-gone` | `SsidVisible=null` tretirano kao false → lažni `WifiRadioDown` |
| `parity.adversarial.unknown-radio-as-off` | `RadioOn=null` → lažna krivica na korisnika ili lažni AP fault |
| `parity.adversarial.suspend-as-outage` | rupa u posmatranju sabrana u downtime |
| `parity.adversarial.stronger-linux-claim` | Linux `WifiRadioDown` uz `SsidVisible=null` — harness fail pre diff-a |

## 25.13 Gde test živi

```text
tests/IEM.Core.Tests/Parity/
    ParityFixture.cs              model + tagged-value parser
    ParityProjection.cs           PlatformFacts → ProbeCycle[] (bez IEM.Windows / IEM.Linux)
    CanonicalParityView.cs        Core izlaz → view
    ParityDiffer.cs               path diff + reason
    ParityCatalogTests.cs         jedan theory test po fixtureId
```

Projekcija je u `IEM.Core.Tests`, ne u platform assembly. Time se parity ne veže za P/Invoke. Adapter characterization (pravi nl80211) je poseban Linux integration test; on **puni** PlatformFacts, ne zamenjuje ovaj katalog.

Ako pravi adapter na Linux VM proizvede drugačiji `SsidVisible` od fixture pretpostavke, to je adapter test, ne tiha izmena golden-a.

## 25.14 Invarijante 255–260 (draft, ovaj sloj)

## 255

`PARITY_COMPARES_CANONICAL_MEANING_NOT_NATIVE_FACTS`

Parity test poredi Core značenje, ne Win32/nl80211 bajtove.

## 256

`NULL_UNAVAILABLE_SKIPPED_UNKNOWN_AND_ABSENT_ARE_DISTINCT`

Pet stanja imaju pet tagova. JSON `null` u fixture-u je greška formata.

## 257

`UNLISTED_PARITY_DIVERGENCE_FAILS_THE_RELEASE`

Razlika van registra, `pathWhitelist` i `allowedDivergences` obara 3.1.

## 258

`WEAKER_OBSERVABILITY_MUST_NOT_INVENT_STRONGER_CLAIMS`

Manje činjenica ne sme da proizvede precizniju atribuciju.

## 259

`ASYMMETRIC_ATTRIBUTION_REQUIRES_EXPLICIT_FIXTURE_PERMISSION`

`WifiRadioDown` vs `AdapterDown` (i slično) samo uz `allowedDivergences` i razlog.

## 260

`CANONICAL_PARITY_SERIALIZATION_EXCLUDES_PLATFORM_FACTS`

JCS hash se računa nad normalizovanim `CanonicalParityView`, ne nad PlatformFacts niti nad sirovim manifestom.

---

# 26. Cross-Platform Verification

Obavezni acceptance scenariji:

## Linux → Windows

Linux napravi:

```text
Evidence Package
```

Windows verifier mora potpuno da ga verifikuje.

## Windows → Linux

Windows napravi Evidence Package.

Linux verifier mora potpuno da ga verifikuje.

Isto važi za:

- redacted derivative
- report verification metadata
- signatures
- manifest chain
- canonical hashes

---

# 27. Linux End-to-End Acceptance Scenario

Ovo je **Installed / lane C** scenario (§31.4). Autoritet za `LINUX RELEASE ACCEPTED`. GUI korak sme biti CLI ako nema displeja (headless ≠ portable).

Svaki korak emituje `TestRunEnvironment`. Suspend/netns bez required capability → `GATE INCOMPLETE`, ne PASS.

```text
Fresh VM (Ubuntu 26.04 / Debian 13 / Fedora 44 x64)
  ↓
install .deb/.rpm
  ↓
verify package/release
  ↓
systemd (system, ne --user) starts service
  ↓
CLI ili GUI na control.sock
  ↓
SO_PEERCRED authenticated
  ↓
start shortened 48h-style test
  ↓
network namespace fault injection
  ↓
suspend/resume gde je suspendAvailable
  ↓
service restart
  ↓
system reboot
  ↓
resume session
  ↓
complete evidence
  ↓
sign (SystemInstallationIdentity)
  ↓
verify
  ↓
generate report
  ↓
generate RedactedEvidencePackage
  ↓
verify derivative
  ↓
upgrade package
  ↓
verify old evidence again
  ↓
uninstall
  ↓
canonical evidence preserved
```

Portable four-way parity (§15A.10, §25) je obavezan functional gate, **nije** zamena za ovaj scenario.

Final state:

```text
LINUX RELEASE ACCEPTED
```

---

# 28. Invarijante 211–240

Linux platform invarijante. 241–248 §5.15 · 249–254 §7.14 · 255–260 §25.14 · 261–268 §15.5 · 269–273 §31.11 · 274–282 §15A.11.

## 211

`PLATFORM_NATIVE_FACTS_NEVER_REDEFINE_CANONICAL_EVIDENCE_SEMANTICS`

Linux-native činjenice nikada ne menjaju značenje kanonskih IEM evidence objekata.

## 212

`LINUX_AND_WINDOWS_SHARE_ONE_CANONICAL_EVIDENCE_MODEL`

Ne postoji zaseban Linux evidence model.

## 213

`LINUX_PLATFORM_ADAPTER_FAILURE_NEVER_BECOMES_NETWORK_FAILURE`

Adapter/runtime kvar ne sme biti predstavljen kao mrežni outage.

## 214

`SYSTEMD_LIFECYCLE_STATE_NEVER_BECOMES_MEASUREMENT_STATE`

Service manager stanje nije measurement stanje.

## 215

`SERVICE_START_NEVER_REQUIRES_NETWORK_SUCCESS`

Monitoring servis mora moći da se pokrene dok mreža ne radi.

## 216

`LINUX_HOST_SUSPEND_NEVER_BECOMES_NETWORK_OUTAGE`

Suspend je host non-observability interval, ne internet outage.

## 217

`HOST_REBOOT_OR_SERVICE_RESTART_NEVER_BECOMES_NETWORK_OUTAGE`

Restart hosta/procesa nije mrežni dokaz.

## 218

`LINUX_ROUTE_FACTS_ARE_DERIVED_FROM_KERNEL_ROUTING_STATE`

Route evidence/provenance se dobija iz kernel routing stanja.

## 219

`NETWORKMANAGER_IS_OPTIONAL_ENRICHMENT_NOT_CANONICAL_ROUTE_AUTHORITY`

NetworkManager nije obavezan niti kanonski izvor route istine.

## 220

`WIFI_METADATA_ABSENCE_NEVER_INVALIDATES_GENERIC_CONNECTIVITY_MEASUREMENT`

Wi-Fi enrichment može biti nedostupan bez invalidacije generičkog merenja.

## 221

`UNIX_CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_CREDENTIALS_NOT_CLIENT_PAYLOAD`

Caller UID/GID/PID mora doći iz transporta/kernel-a.

## 222

`UNIX_UID_AUTHENTICATION_NEVER_IMPLIES_COMMAND_AUTHORIZATION`

Autentifikacija identiteta nije autorizacija komande.

## 223

`IPC_FILESYSTEM_PERMISSIONS_NEVER_REPLACE_COMMAND_AUTHORIZATION`

Socket ACL/permissions nisu jedini authorization mehanizam.

## 224

`LINUX_KEY_PROTECTION_IS_NEVER_OVERCLAIMED`

Software key nikad se ne predstavlja kao TPM-backed.

## 225

`LINUX_SIGNING_KEY_FAILURE_NEVER_CAUSES_SILENT_IDENTITY_ROTATION`

Key access failure mora biti fail-closed.

## 226

`CANONICAL_EVIDENCE_STORAGE_IS_NEVER_WRITABLE_BY_PRESENTATION_CLIENT`

Presentation (ViewModel / XAML / Avalonia) ne piše store. U Installed modu GUI proces nema ACL write na `/var/lib`. U Portable modu piše **samo** `IEM.Service.Runtime` u istom procesu, u XDG; to nije „GUI ACL nad system store-om“.

## 227

`DISTRIBUTION_PACKAGING_NEVER_CHANGES_EVIDENCE_SEMANTICS`

`.deb`, `.rpm` i drugi paketi ne utiču na evidence meaning.

## 228

`DISPLAY_BACKEND_NEVER_CHANGES_EVIDENCE_SEMANTICS`

X11/Wayland je prezentacija, ne evidence.

## 229

`DESKTOP_INTEGRATION_FAILURE_NEVER_CHANGES_MONITORING_EXECUTION`

GUI/desktop kvar ne zaustavlja **servisni** monitoring. Portable namerno staje sa procesom (§15A.7, 280).

## 230

`TIME_SYNCHRONIZATION_STATE_NEVER_BECOMES_NETWORK_CONNECTIVITY_STATE`

Time sync je time provenance, ne network connectivity verdict.

## 231

`BOOT_IDENTITY_CHANGE_IS_HOST_DISCONTINUITY_NOT_NETWORK_OUTAGE`

Promena boot identiteta označava host discontinuity.

## 232

`PLATFORM_PRIVILEGES_ARE_NEVER_INTERPRETED_AS_EVIDENCE_TRUST`

Root/capability status ne daje viši evidence trust.

## 233

`NON_NETWORKMANAGER_SYSTEMS_REMAIN_FULLY_MEASUREMENT_CAPABLE`

IEM mora raditi bez NetworkManager-a.

## 234

`EVIDENCE_FROM_EITHER_PLATFORM_IS_VERIFIABLE_ON_THE_OTHER`

Windows/Linux evidence verifikacija mora biti cross-platform.

## 235

`PARITY_GATE_REJECTS_UNEXPLAINED_PLATFORM_SEMANTIC_DIVERGENCE`

Neobjašnjena semantic divergence blokira release.

## 236

`LINUX_PACKAGE_REMOVAL_NEVER_SILENTLY_DELETES_CANONICAL_EVIDENCE`

Uninstall ne briše evidence bez eksplicitne korisničke odluke.

## 237

`LINUX_UPGRADE_NEVER_SILENTLY_ROTATES_EVIDENCE_IDENTITY`

Package upgrade ne sme rotirati signing identity.

## 238

`UNSUPPORTED_PLATFORM_CAPABILITY_IS_NEVER_RENDERED_AS_ZERO_OR_FAILURE`

Unsupported/nedostupna capability se ne prikazuje kao `0`, success ili network failure.

## 239

`PLATFORM_FALLBACK_PROVENANCE_IS_ALWAYS_EXPLICIT`

Ako se koristi fallback provider, provenance mora biti vidljiv.

## 240

`EXPERIMENTAL_UI_BACKEND_IS_NEVER_REQUIRED_FOR_EVIDENCE_OPERATION`

Eksperimentalni Wayland/native UI backend nikada nije uslov za dokazni rad.

---

# 29. ROADMAP 3.1

## 3.1-0 · Linux Portability Baseline

Cilj: mapirati sadašnji sistem pre funkcionalnih promena.

Adapter inventar, privilege preflight i authorization matrica su zaključani u §4. Ova faza ih pretvara u testove i characterization fixture-e, bez nove semantike.

Uraditi:

- kodni inventar iz §4.4 (svaki Windows tip → Linux par ili „nije adapter“)
- uklanjanje plana za curenja iz §4.4.7 (još bez ponašajne promene na Windowsu)
- platform dependency manifest (`IEM.Windows` TFM, native libs, P/Invoke)
- characterization / parity test plan nad postojećim 3.0 fixture-ima
- Linux threat model draft (osnova u §34, proširiti socket/symlink/key)
- draft architecture tests (zabrana Core→Windows/Linux reference)
- lista platform-specific output fields (dozvoljena razlika vs parity blocker)
- define unsupported/unknown semantics prema §33 i §4.5
- baseline package compatibility (šta 3.0 evidence već sme da kaže o `platform`)

Gate:

```text
Existing 3.0 test suite remains green
No Windows behavior change
§4 inventory covers every type in src/IEM.Windows
Every §4.4.7 leak has a named target contract
```

---

## 3.1-1 · Platform Composition Boundary

Uraditi:

- izdvojiti `IEM.Service.Runtime` (isti za system i portable)
- izdvojiti `IEM.Presentation`
- `IPlatformInstallationProbe` → `Presence` + `Reachability` (§15A.5)
- composition bira `ServiceMonitorHost` vs `InProcessMonitorHost` **i** storage layout
- installed+unreachable → `ServiceUnavailable`, ne portable
- zabraniti `OperatingSystem.IsWindows()` u `ServiceContract.IsInstalled` / path roots
- architecture tests

Gate:

```text
Windows semantic behavior unchanged
No platform assembly referenced from canonical layers
No silent portable fallback when service is installed
Same runtime assembly for both hosts
```

---

## 3.1-2 · Linux Host + systemd

Specifikacija je §12, §15 i §15A. Uraditi:

- `IEM.Service.Linux` + `UseSystemd`, `Type=notify` (watchdog je CANDIDATE za fazu sa eksplicitnim heartbeat senderom)
- **nema** `.socket` unita
- **nema** `systemd --user` unita
- `User=iem` / `Group=iem` + `SupplementaryGroups=iem-users`
- `StateDirectory=` + **`StateDirectoryMode=0700`**
- `RuntimeDirectory=` + `RuntimeDirectoryMode=0750`, grupa doterana na `iem-users` (preko `SupplementaryGroups=iem-users`)
- stop ne briše StateDirectory
- hardening matrica §12.4: svaka CANDIDATE tek posle testa
- start bez `network-online.target`

Gate:

```text
Service survives restart with same KeyId and evidence
Service starts without network
StateDirectory is 0700 iem:iem after start
Runtime dir is 0750 iem:iem-users before listen
ProtectSystem=strict does not block session write inside StateDirectory
No hardening directive is REQUIRED without a green capability test
Failure returns correct non-zero state
```

---

## 3.1-3 · Unix IPC & Identity

Specifikacija je §11. Uraditi:

- pathname `control.sock`, app-owned listener, bez socket activation
- jedan socket za status i komande
- safe bind + fail-closed stale cleanup per §11.3
- `SO_PEERCRED` + `SO_PEERGROUPS` (primarni autoritet za supplementary grupe peer-a) / `/proc` fallback
- `IpcAuthorizationPolicy` v2: `role:admin` / `role:operator`, bez `"Admin"`/`"root"` substringa
- missing session owner → Denied
- Windows transport emituje iste role (shared-core)
- `CreateExport` ne širi ACL na StateDirectory; klijent preuzima paket kroz bounded/chunked streaming IPC transport

Gate:

```text
Client payload cannot spoof identity
Filesystem connect ≠ command allow
iem-users can StartSession and read
non-owner cannot Stop/Finalize
missing owner cannot Stop/Finalize
iem-admin or uid 0 can Stop/Finalize
symlink or non-socket at control.sock → service refuses to start
no second Linux status socket
Contains(Admin)|Contains(root) gone from policy
```

---

## 3.1-4 · Linux Routing & Link Truth

Specifikacija je §5. Uraditi tu implementaciju, ništa šire.

- dva netlink soketa (query + observer)
- `RTM_GETROUTE` fib lookup po destinaciji (IPv4 i IPv6 odvojeno)
- `RTM_GETLINK` / `RTM_GETADDR` + mapa ifindex → `LinkSnapshot.InterfaceId`
- multicast `RTNLGRP_LINK` / `IPv4_IFADDR` / `IPv4_ROUTE` / `IPv6_IFADDR` / `IPv6_ROUTE`
- `RouteGeneration` + `RouteResolutionObservation`
- TOCTOU: `Resolved` se ne dira; `ChangedDuringExecution` smanjuje atribuciju
- multicast membership se meri po grupi; neuspeh ili nepouzdan stream → polling, nikad `Held`
- `CAP_NET_ADMIN` se ne pretpostavlja kao zahtev za receive-only observation
- VPN/default-route/multipath po §5.11
- keš 2 s, invalidacija na event

Gate:

```text
Measurement path resolved from kernel facts
Unresolved route still allows probe execution
Route change never fabricates outage
Route change never rewrites probe outcome
No PathContinuity.Held without live netlink membership
Invariants 241–248 have tests
```

---

## 3.1-5 · Linux Probe Execution

Specifikacija je §5.9–§5.13. Uraditi:

- preflight po ćeliji, ne po hostu
- ICMP IPv4 i ICMP IPv6 kao odvojeni datagram soketi
- `IBoundIcmp` capability failure vraća `IcmpEcho` non-timeout failure, **ne** `null` (nema tihog `Ping` fallbacka)
- source-address bind kao jedini parity mehanizam
- Core: TCP bind greška → `Skipped`, ne `Failed`
- `Skipped` ne ulazi u `ProbeTally` / `EligibleCount`
- DNS/TCP/HTTP idu dok je ICMP silent
- namespace harness iz §5.14

Gate:

```text
Default unit runs without CAP_NET_RAW
IPv4 ICMP denied + IPv6/TCP success ≠ outage
Skipped ICMP never enters loss denominator
Gateway ICMP skip + no external success = InternetDown, not GatewayDown
Bound is never true without a successful source bind
Synthetic fault matrix classified deterministically
Windows/Linux semantic outcomes match
```

---

## 3.1-6 · Linux Power / Time / Reboot

Specifikacija je §8–§10 i §5.4.4. Uraditi:

- logind `PrepareForSleep` preko `IPowerEventSource`
- boot identity u `ITimeObservationProvider` (`boot_id`), **ne** novi `LinuxBootIdentityProvider`
- monotonic / `CLOCK_BOOTTIME` / wall-clock
- restart vs reboot vs portable process exit
- clock adjustment tests
- suspend bez logind → `HostObservabilityGap`, lane C `GATE INCOMPLETE` ako je suspend required

Gate:

```text
Suspend never becomes outage
Reboot never becomes outage without network facts
Portable process exit is observability end, not outage
No IBootIdentityProvider type is introduced
```

---

## 3.1-7 · Linux Wi-Fi

Specifikacija je §7. Uraditi:

- `LinuxNl80211Radio` : `IWirelessRadio` — samo puni postojeće metode
- `RadioOn` iz rfkill tog wiphy; unknown nikad false
- association iz `GET_INTERFACE` + associated BSS + `GET_STATION`
- `GET_SCAN` + freshness (`MaximumAge` 3 min); `false` samo uz complete svež snapshot
- `TRIGGER_SCAN` nije default; `RequestUrgentScan` sme da bude no-op
- NM samo enrichment; konflikt se beleži
- `LinuxWifiLinkInspector` dekorator + `WirelessDetailReader` (Core)
- Ethernet / EOPNOTSUPP path ostaje merljiv

Gate:

```text
Missing Wi-Fi metadata never invalidates generic monitoring
RadioOn unknown never becomes false
SsidVisible false requires fresh complete scan
No TRIGGER_SCAN required for evidence
WifiRadioDown only when RadioOn==true && SsidVisible==false && link down
RSSI never becomes NetworkState
NM never overrides nl80211 BSSID
Invariants 249–254 have tests
```

---

## 3.1-8 · Linux Crypto & Canonical Storage

Uraditi:

- `SystemInstallationIdentity` u `/var/lib/.../keys`
- `PortableUserIdentity` u `$XDG_STATE_HOME/.../keys`
- razdvojeni namespace; nema deljenja ključa
- portable ne tvrdi service-only protection
- software-protected baseline
- `/var/lib` 0700 canonical (system)
- XDG state (portable)
- atomic writes, crash recovery, no silent rotation

Gate:

```text
System KeyId survives restart/upgrade
Portable key never opens the system key file
Broken key fails closed
Evidence signs and verifies cross-platform
Portable manifest carries ExecutionMode=PortableInProcess
```

---

## 3.1-9 · Shared Presentation Model

Uraditi:

- move reusable ViewModels
- PresentationSnapshot reuse
- revision monotonicity
- semantic tokens
- platform-neutral presentation contracts
- golden UI semantics tests
- Windows WPF regression suite

Gate:

```text
Same snapshot => same semantic UI state on both platforms
```

---

## 3.1-10 · Avalonia Linux UI

Uraditi:

- `IEM.App.Linux`
- MONITOR
- EVIDENCE
- CASE
- SPEED
- X11
- XWayland
- native Wayland preview
- HiDPI
- accessibility
- keyboard navigation
- multi-monitor
- themes
- crash isolation from service

Gate:

```text
GUI loss does not affect measurement in SystemService mode
Portable process exit ends observability and is not an outage
No UI state becomes evidence state
```

---

## 3.1-11 · Linux Packaging

Uraditi:

- `.deb`
- `.rpm`
- self-contained runtime
- x64 stable
- ARM64 preview (§20.4)
- systemd unit
- desktop entry
- icons
- optional MIME association
- signed repo metadata
- package signing
- SBOM
- release manifest
- Cosign bundle
- checksums

Gate:

```text
Installed artifacts match verified release artifacts
Package removal preserves evidence
```

---

## 3.1-12 · Cross-Platform Parity

Specifikacija je §25. Uraditi:

- parser `parityFixtureVersion` + tagged values
- `ParityProjection` (PlatformFacts → `ProbeCycle`, bez native API)
- `CanonicalParityView` + RFC 8785 poređenje
- `ParityDiffer` (path → W → L → allowed/forbidden)
- ceo katalog §25.12
- linter: PlatformFacts nisu jače od `world`; Linux `WifiRadioDown` zahteva `SsidVisible=false`
- pored toga, nezavisno: Linux paket ↔ Windows verifier i obrnuto (§26)

Gate:

```text
No FORBIDDEN path in catalog
Weaker Linux observability never yields a stronger claim
Skipped never enters tallies
Asymmetric wifi/scan fixture lists its attribution divergence
JCS hashes match after allowedDivergence placeholders
Invariants 255–260 have tests
```

---

## 3.1-13 · Linux Installation Lifecycle

Testirati:

- clean install / upgrade / reinstall (system)
- service crash → GUI vidi `ServiceUnavailable`, ne otvara XDG sesiju
- service restart / machine reboot: system sesija nastavlja; portable ne tvrdi to
- portable: process exit = observability end, nije outage
- key/evidence preservation u svom namespace-u
- uninstall system: evidence u `/var/lib` ostaje; portable XDG se ne dira
- nema `systemd --user` putanje

Gate:

```text
Existing system evidence remains valid after lifecycle operations
Installed+down never creates a portable session
Portable exit is not recorded as network outage
```

---

## 3.1-14 · Linux Release Acceptance

Specifikacija je §20 + §31.C.

```text
Ubuntu 26.04 LTS x64
Debian 13 x64
Fedora 44 x64
```

LOCKED i CURRENT, oba. `TestRunEnvironment` na svakom run-u. Suspend/netns bez capability → `GATE INCOMPLETE`, ne PASS.

Gate:

```text
Lane A green (windows-2025 + ubuntu-24.04)
Lane B LOCKED green on three Tier A
Lane C LOCKED + CURRENT green on three Tier A
No required-capability PASS without the capability
System mode lane C is the LINUX RELEASE ACCEPTED authority
Portable functional + four-way parity green (not a substitute for C)
```

---

## 3.1-15 · IEM 3.1.0-rc1

Finalni milestone:

```text
Internet Evidence Monitor 3.1.0-rc1
Windows + Linux parity candidate
```

Freeze uslovi:

- all 3.0 invariants preserved
- invariants 211–240 locked
- invariants 241–248 locked with 3.1-4 / 3.1-5
- invariants 249–254 locked with 3.1-7
- invariants 255–260 locked with 3.1-12
- invariants 261–268 locked with 3.1-2 / 3.1-3
- invariants 269–273 locked with 3.1-14
- invariants 274–282 locked with 3.1-1 / portable mode
- full Windows regression green
- full Linux suite green
- parity suite green
- cross-platform verification green
- release artifact verification green
- distro acceptance green

---

# 30. Branching Strategy dok traje 3.0.0-rc1

Ne čekati stable da bismo počeli 3.1.

Predlog:

```text
main / 3.0.0-rc1
     │
     ├── Windows RC validation
     │
     └── linux/3.1
             │
             └── 3.1-0
```

Ako 3.0 RC pronađe bug:

```text
fix → 3.0 branch
      ↓
forward merge
      ↓
3.1
```

Nikada:

```text
Linux 3.1 feature
      ↓
backport into frozen 3.0
```

osim ako je stvarni shared-core bug i prolazi 3.0 release discipline.

---

# 31. CI — tri lane-a, pin, provenance

Zaključano 2026-08-19. Nema jedne ogromne matrice koja se pretvara da sve testira. Nema `*-latest` u release-relevantnom workflow-u.

## 31.1 Tri vrste CI-a

| Lane | Gde | Šta dokazuje | Release uloga |
|---|---|---|---|
| **A** Fast deterministic | hosted `windows-2025` + `ubuntu-24.04` | Core značenje, build, parity | svaki PR; blocker |
| **B** Distro integration | container/VM slike Tier A | paket, nalog, unit, socket, preflight | PR na `linux/*` i release; blocker za package/IPC/storage |
| **C** VM/kernel acceptance | prave VM, systemd PID 1 | boot, suspend, reboot, netns, upgrade | samo release; **LINUX RELEASE ACCEPTED** |

Container u B **nije** autoritet za suspend, reboot, pravi netns lifecycle, logind, ni kernel/driver. To je C.

## 31.2 Lane A — fast deterministic (svaki PR)

Runners, **eksplicitni tagovi**:

```yaml
windows-2025      # ne windows-latest
ubuntu-24.04      # ne ubuntu-latest; 24.04 je LTS i hosted GA
```

19.08.2026. GitHub mapira `windows-latest` → Server 2025 i `ubuntu-latest` → 24.04. To se **ne koristi**. Pin je ime runnera.

Ubuntu 26.04 ovde nije runner (preview). 26.04 ide u B/C.

Poslovi:

```text
restore (locked)
build
unit
architecture          (§32)
characterization      (2.7.2 / 3.0 baseline)
golden parity         (§25)
canonical serialization (RFC 8785)
verification          (IEM.Verifier, offline)
```

Bez pravog systemd kao PID 1. Bez `.deb` install na hostu kao acceptance. Parity projekcija je u-process (§25.13).

## 31.3 Lane B — distro integration

Slike iz `ci/linux-platform-lock.json` za:

```text
Ubuntu 26.04    x64
Debian 13       x64
Fedora 44       x64
```

Debian/Fedora: container sme za *deo* (restore, build, pack `.deb`/`.rpm`, unit, filesystem mode). **Ne sme** da se proglasi release-accepted ako je to bio jedini dokaz.

Obavezni B testovi (container ili VM):

```text
package install
service account iem / grupe iem-users iem-admin
systemd unit load (gde ima systemd)
StateDirectory 0700
RuntimeDirectory + control.sock lifecycle
SO_PEERCRED (gde mreža/IPC radi)
rtnetlink GETROUTE
probe preflight ćelije
crypto provision + no silent rotate
storage symlink guard
package upgrade, isti KeyId
uninstall, evidence ostaje
```

Ako container nema systemd PID 1: ti koraci su `NOT TESTED` / `GATE INCOMPLETE` za B-systemd podskup, i **moraju** pasti na C. B ne sme da ih označi PASS.

## 31.4 Lane C — VM/kernel acceptance (release blocker)

Prava VM, normalan boot, systemd je PID 1. Targeti = Tier A.

```text
fresh VM
  → install .deb / .rpm
  → boot
  → start without network          (§13)
  → GUI/CLI IPC na control.sock
  → network namespace fault injection (§5.14, §24)
  → suspend/resume gde host to ume
  → reboot
  → session recovery
  → signing
  → CreateExport
  → verify (isti i drugi OS)
  → upgrade
  → uninstall
  → evidence preserved
```

Sve tri distro slike zelene → `LINUX RELEASE ACCEPTED`.

arm64 preview: isti scenario na jednom Tier-A kad bude spreman; do tada nije deo ovog gate-a.

## 31.5 LOCKED vs CURRENT

Dva moda, oba postoje. Jedan nije zamena za drugi.

| Mod | Ulaz | Gate |
|---|---|---|
| `LOCKED` | tačan `imageDigest` / `baseImageId` iz lock fajla | reproducibility |
| `CURRENT` | najnoviji security/point update **unutar istog** distro release-a | drift / compatibility |

`LOCKED` zelen šest meseci na starom kernelu, a `CURRENT` crven posle Ubuntu update-a koji slomi rtnetlink — to je **signal**, ne smetnja. Release zahteva:

```text
A zelen
B LOCKED zelen na 3× Tier A
C LOCKED zelen na 3× Tier A
C CURRENT zelen na 3× Tier A     // ili dokumentovan, vremenski ograničen waiver
```

Waiver za CURRENT sme samo ako je uzrok uzvodni distro bug, sa linkom i datumom ponovnog pokušaja. Nije „CI je bio zelen prošle nedelje“.

## 31.6 Platform lock

Fajl: `ci/linux-platform-lock.json` (nije samo `ubuntu:26.04` tag).

```json
{
  "lockVersion": 1,
  "dotnetSdkVersion": "10.0.xxx",
  "lastReviewedAt": "2026-08-19",
  "platforms": [
    {
      "id": "ubuntu-26.04-x64",
      "tier": "A",
      "distro": "ubuntu",
      "release": "26.04",
      "architecture": "x64",
      "baseImageId": "ubuntu:26.04",
      "imageDigest": "sha256:…",
      "imageSha256": "sha256:…",
      "kernelVersion": "6.x.y-…",
      "systemdVersion": "257.x",
      "glibcVersion": "2.41",
      "dotnetSdkVersion": "10.0.xxx",
      "packageSnapshotDate": "2026-08-19",
      "lastValidatedAt": "2026-08-19",
      "lanes": ["B", "C"]
    }
  ]
}
```

Svaki Tier A i B red ima digest. Tag bez digest-a nije lock.

`LOCKED` job čita samo ovaj fajl. `CURRENT` job sme da povuče isti `baseImageId` bez digest pina i **upisuje** novi digest u artefakt run-a (ne mutira lock bez PR-a).

Support i dalje stoji na `release: "26.04"`, ne na `kernelVersion` iz lock-a.

## 31.7 .NET pin

Projekat je `net10.0`. U korenu:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

Konfiguracija `"rollForward": "disable"` prema Microsoft .NET specifikaciji zahteva **tačan (exact) SDK match**, dok `latestPatch` predstavlja kretanje unutar feature band-a. Za reproduktibilne LOCKED buildove zahteva se **exact .NET SDK pin**. Vrednost `10.0.xxx` u planovima je isključivo tekstualni placeholder; stvarni `global.json` i `ci/linux-platform-lock.json` moraju sadržati konkretnu, tačnu verziju SDK bez wildcard simbola (npr. `10.0.100`).

Lane A na Windowsu i Linuxu koristi isti tačno pinovani SDK.

Self-contained `.deb`/`.rpm` smanjuje runtime drift kod korisnika. Lane B/C i dalje mere **host** ABI (glibc, netlink, systemd) na svakom Tier-A distro-u. Self-contained nije izgovor da se host ne testira.

## 31.8 TestRunEnvironment — provenance svakog run-a

Svaki test job (A/B/C) emituje JSON uz rezultate:

```json
{
  "distroId": "ubuntu",
  "distroVersion": "26.04",
  "kernelRelease": "6.x.y-…",
  "architecture": "x64",
  "systemdVersion": "257",
  "dotnetVersion": "10.0.xxx",
  "containerOrVm": "vm",
  "imageDigest": "sha256:…",
  "privilegedCapabilities": [],
  "networkNamespaceAvailable": true,
  "nl80211Available": false,
  "logindAvailable": true,
  "suspendAvailable": true
}
```

## 31.9 NOT TESTED / GATE INCOMPLETE ≠ PASS

```text
required capability absent
        → NOT TESTED
        → GATE INCOMPLETE ako lane zahteva tu capability

nikad:
required capability absent
        → PASS
```

| Lane | Required za PASS tog lane-a | Ako nema |
|---|---|---|
| A | nijedan kernel/logind | — |
| B systemd podskup | systemd kao servis menadžer | `GATE INCOMPLETE`; C mora da pokrije |
| C suspend korak | `logindAvailable` ∧ `suspendAvailable` | taj korak `NOT TESTED`; C gate incomplete dok se ne nađe host koji ume, ili eksplicitni waiver po distro-u |
| C netns fault | `networkNamespaceAvailable` | incomplete |
| C Wi-Fi korak | `nl80211Available` | `NOT TESTED`; **nije** blocker (Ethernet-only VM je legitimna). WifiRadioDown scenario ostaje u §25 golden, ne u C |

`PASS: suspend never became outage` sme da stoji samo ako je suspend **stvarno izvršen**. Inače je to lažan PASS.

## 31.10 Release pipeline

```text
PR:     A
linux/: A + B LOCKED
tag:    A + B LOCKED + C LOCKED + C CURRENT
        + package + SBOM + sign + verify
        + cross-platform verifier (§26)
        → LINUX RELEASE ACCEPTED
```

`*-latest` se ne pojavljuje u `.github/workflows/` za ove gate-ove.

## 31.11 Invarijante 269–273 (draft, ovaj sloj)

## 269

`SUPPORT_TARGET_IS_THE_DISTRO_RELEASE_NOT_A_PINNED_KERNEL`

Korisniku se obećava Ubuntu 26.04 / Debian 13 / Fedora 44, ne lock digest.

## 270

`RELEASE_WORKFLOWS_NEVER_USE_FLOATING_RUNNER_TAGS`

Nema `windows-latest` / `ubuntu-latest` / `ubuntu-26.04` preview kao jedinog Tier-A autoriteta.

## 271

`MISSING_REQUIRED_TEST_CAPABILITY_IS_GATE_INCOMPLETE_NOT_PASS`

Nema capability → nije PASS.

## 272

`LOCKED_AND_CURRENT_LANES_ARE_BOTH_REQUIRED_FOR_RELEASE`

Reprodukcija i drift se mere odvojeno. Jedan zeleni lane ne zatvara drugi.

## 273

`DOTNET_SDK_IS_EXACTLY_PINNED_FOR_LOCKED_BUILDS`

`global.json` sa `"rollForward": "disable"` + lock fajl, tačna SDK verzija bez wildcard simbola, ne plutajući feature band niti golo `net10.0`.

---

# 32. Architecture Tests

Dodati testove koji statički proveravaju:

```text
IEM.Core            must not reference IEM.Windows
IEM.Core            must not reference IEM.Linux
IEM.Evidence        must not reference IEM.Windows
IEM.Evidence        must not reference IEM.Linux
IEM.Verification    must not depend on UI
IEM.Presentation    must not mutate evidence
Linux UI            must not directly own canonical storage
Windows UI          must not directly own canonical storage
```

---

# 33. Failure Semantics Matrix

Svaka Linux capability mora razlikovati najmanje:

```text
Success
Unavailable
Unsupported
PermissionDenied
TemporarilyUnavailable
ProviderFailure
HostSuspended
Cancelled
Timeout
Unknown
```

Nikada:

```text
unsupported => 0
unknown => success
service failure => outage
provider failure => internet failure
```

---

# 34. Security Model

Linux threat model treba posebno da pokrije:

- malicious local user
- compromised GUI process
- spoofed Unix socket client
- socket replacement
- symlink attacks
- writable canonical evidence directory
- key theft
- key replacement
- service downgrade
- package tampering
- environment-variable config injection
- malicious NetworkManager metadata
- D-Bus impersonation assumptions
- TOCTOU on evidence files
- privilege escalation through helper operations
- unsafe native interop
- namespace/container edge cases

---

# 35. Native Interop Policy

Za native Linux interop:

- minimalan P/Invoke surface
- wrappers u `IEM.Linux`
- explicit ownership/lifetime
- SafeHandle gde je primenljivo
- zero-copy samo gde donosi dokazivu korist
- bounds validation
- no unsafe parsing without tests
- fuzz parsing of Netlink payloads where practical

Kernel input je i dalje spoljašnji input i mora se parsirati defensively.

---

# 36. Network Probe Semantics

Svaka proba mora čuvati:

- target
- resolved address
- protocol
- start UTC
- end UTC
- duration
- route/interface provenance
- result
- failure category
- raw/platform observation metadata
- semantic normalization

Platform adapter daje fact.

Core odlučuje meaning.

---

# 37. IPv4 / IPv6

Linux 3.1 mora tretirati dual-stack kao first-class.

Testirati:

```text
IPv4 up / IPv6 up
IPv4 down / IPv6 up
IPv4 up / IPv6 down
both down
IPv6 preferred route
IPv4 fallback
route changes
DNS returns A only
DNS returns AAAA only
```

Nikada ne svoditi "internet radi" na samo jedan address family.

---

# 38. VPN / Tunnel Semantics

Testirati najmanje:

- WireGuard
- TUN/TAP generic
- OpenVPN-like tunnel
- default-route VPN
- split tunnel
- VPN disconnect
- route replacement

VPN route change nije automatski outage.

MeasurementPath mora jasno pokazati kojim interface/path-om je proba izvršena.

---

# 39. Headless Linux Scenario

Headless **nije** portable (§15A.8).

| Mašina | Mod |
|---|---|
| server / mini PC / Pi, paket instaliran | Installed system service + CLI = **full** |
| raspakovan tarball, nema systemd | Portable CLI = fallback |
| desktop / laptop sa GUI | Installed ili Portable, po probe-u |

GUI je optional presentation. Monitoring u installed modu ne zavisi od njega.

---

# 40. ARM64 Strategy

ARM64 ima smisla zbog:

- Raspberry Pi
- ARM servers
- low-power monitoring devices

Ali release-blocking ARM64 support uvoditi samo kada:

- runtime dependencies rade
- Avalonia support je potvrđen
- packaging radi
- crypto implementation radi
- systemd integration radi
- probe parity prolazi

Ako nije spremno za 3.1.0:

```text
x64 stable
ARM64 preview
```

je bolja politika nego lažno obećanje.

---

# 41. Documentation Deliverables

Tokom 3.1 napraviti:

```text
docs/ROADMAP-3.1.md
docs/LINUX-ARCHITECTURE.md
docs/LINUX-SUPPORT-MATRIX.md
docs/LINUX-THREAT-MODEL.md
docs/LINUX-PACKAGING.md
docs/CROSS-PLATFORM-PARITY.md
docs/LINUX-TROUBLESHOOTING.md
docs/LINUX-SERVICE-HARDENING.md
```

Ažurirati:

```text
INVARIJANTE.md
PREOSTALO.md
README.md
SECURITY.md
CHANGELOG.md
```

---

# 42. Definition of Done — IEM 3.1

IEM 3.1 nije gotov samo zato što se Linux GUI pokrenuo.

Gotov je tek kada važi sve sledeće:

- Linux **system** servis radi pod systemd (ne `--user`)
- nema root requirement kao default
- Unix IPC: jedan app-owned `control.sock`, `SO_PEERCRED`, kanonske role
- route truth dolazi iz kernel networking state-a
- Wi-Fi koristi nl80211 gde je moguće; `TRIGGER_SCAN` nije uslov
- NetworkManager nije obavezan
- suspend/resume/reboot/portable-exit se ne pretvaraju u outage
- dve signing identity: system i portable, fail-closed, ne dele ključ
- Installed store: `/var/lib` `0700` service-owned; Portable: XDG, user-owned
- installed+unreachable ≠ portable
- Linux UI je presentation; u Installed modu ne prekida servis
- Linux evidence se verifikuje na Windowsu i obrnuto
- semantic parity + four-way service/portable suite green
- package upgrade ne mutira evidence; uninstall ne briše `/var/lib`
- SBOM/release manifest/signatures su validni
- Tier A lane C LOCKED+CURRENT zelena (Ubuntu 26.04 / Debian 13 / Fedora 44 x64)
- nedostajuća required capability ≠ PASS
- invarijante 211–240 su zaključane
- invarijante 241–248 su zaključane
- invarijante 249–254 su zaključane
- invarijante 255–260 su zaključane
- invarijante 261–268 su zaključane
- invarijante 269–273 su zaključane
- invarijante 274–282 su zaključane
- postojeće invarijante 1–210 ostaju nepromenjene

---

# 43. Konačna odluka

IEM Linux ne treba implementirati kao:

```text
"ista ideja, druga aplikacija"
```

nego kao:

```text
        ONE EVIDENCE ENGINE

     ┌──────────┴──────────┐
     │                     │
 WINDOWS PLATFORM      LINUX PLATFORM
     │                     │
     └──────────┬──────────┘
                │
     SAME CANONICAL EVIDENCE
```

Windows:

```text
WPF
Windows Service
Named Pipe
Win32 / Native Wi-Fi
CNG
```

Linux:

```text
Avalonia / CLI
systemd *system* service   |  InProcess portable
Unix Domain Socket         |  (nema socketa u portable)
rtnetlink
nl80211
SystemInstallationIdentity | PortableUserIdentity
```

Nema `systemd --user`.

Ispod oba ostaje:

```text
Measurement semantics
Evidence model
Hash chain
Manifest
Signing contract
Verification
Quality
Claims
Reports
Cases
Redaction
Canonical serialization
```

---

# 44. Preporučeni sledeći korak

Dok `3.0.0-rc1` paralelno ide kroz završnu Windows validaciju:

```text
START:
3.1-0 · Linux Portability Baseline
```

Bez feature developmenta i bez promene kanonske semantike.

Prvi konkretni output 3.1 ciklusa treba da bude:

1. Windows → Linux adapter inventar — **zaključan u §4**,
2. platform contract matrix — **zaključana u §4.2 / §4.3 / §15A**,
3. characterization/parity test plan (prvi konkretan kodni rad unutar 3.1-0),
4. Linux threat model draft (§34, proširiti socket/symlink/key/portable),
5. draft architecture tests,
6. invarijante 211–282 su u masteru; 3.1-0 ih pretvara u testove, ne redefiniše.

---

# 45. External Technical References

## Linux kernel networking

- Linux kernel Netlink / route specification:  
  https://www.kernel.org/doc/html/latest/networking/netlink_spec/rt_route.html

- Linux wireless / nl80211 documentation:  
  https://wireless.docs.kernel.org/en/latest/en/developers/documentation/nl80211.html

## Unix Domain Sockets / credentials

- Linux `unix(7)` / `SO_PEERCRED`:  
  https://man7.org/linux/man-pages/man7/unix.7.html

## systemd

- systemd service documentation:  
  https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html

- systemd login1 D-Bus / power events:  
  https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.login1.html

## .NET

- .NET systemd hosting integration:  
  https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.systemdhostbuilderextensions.usesystemd

## NetworkManager

- NetworkManager D-Bus API:  
  https://networkmanager.dev/docs/api/latest/

## Avalonia

- Avalonia supported platforms:  
  https://docs.avaloniaui.net/docs/supported-platforms

- Avalonia Linux platform notes:  
  https://docs.avaloniaui.net/docs/platform-specific-guides/linux

## Debian / RPM signing

- Debian `apt-secure`:  
  https://manpages.debian.org/apt-secure

- Fedora/RPM package management documentation:  
  https://docs.fedoraproject.org/

## Sigstore

- Cosign blob signing:  
  https://docs.sigstore.dev/cosign/signing/signing_with_blobs/

---

# 46. Milestone

```text
IEM 3.0.0-rc1
        │
        ├── Windows final validation
        │
        └── IEM 3.1 Linux branch
                │
                ├── 3.1-0
                ├── 3.1-1
                ├── 3.1-2
                ├── 3.1-3
                ├── 3.1-4
                ├── 3.1-5
                ├── 3.1-6
                ├── 3.1-7
                ├── 3.1-8
                ├── 3.1-9
                ├── 3.1-10
                ├── 3.1-11
                ├── 3.1-12
                ├── 3.1-13
                ├── 3.1-14
                └── 3.1-15
                       │
                       ▼
              IEM 3.1.0-rc1
          Windows + Linux parity
```

---

**End of document.**
