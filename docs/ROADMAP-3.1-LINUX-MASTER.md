# Internet Evidence Monitor 3.1 — Linux Master Architecture & Implementation Plan

> **Status:** Planned / ready to start  
> **Base release:** Internet Evidence Monitor `3.0.0-rc1`  
> **Scope:** Linux enablement + Windows/Linux semantic parity  
> **Principle:** **One Evidence Engine — Multiple Platform Adapters**  
> **Language:** Serbian (technical identifiers remain English)  
> **Target milestone:** `Internet Evidence Monitor 3.1.0-rc1`

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
                  WPF App                  Avalonia App
```

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
├── IEM.Service.Linux/           # tanak systemd host
│
├── IEM.App/                     # postojeći Windows WPF
└── IEM.App.Linux/               # Avalonia
```

Napomena:

Postojeći `IEM.Service` ne treba agresivno preimenovati na početku 3.1. Prvo izdvojiti platform-neutral runtime iza characterization testova, pa zatim razdvojiti hostove.

---

# 4. Platform Capability Contracts

Linux/Windows razlike moraju biti zatvorene iza jasnih ugovora.

Predloženi ugovori:

```text
IRouteResolver
ILinkInspector
INetworkChangeObserver
IBoundProbeExecutor
IWirelessDiagnostics
IPowerEventSource
ITimeObservationProvider
IEvidenceKeyProvider
IIpcTransport
IPlatformStorageLayout
IPlatformIdentityProvider
IBootIdentityProvider
```

Pravilo:

> OS-specifičan kod ne sme biti razbacan po Core/Evidence/Analysis slojevima kroz `OperatingSystem.IsWindows()` / `IsLinux()` grananja.

Platform selection se radi u composition root-u.

---

# 5. Linux Networking Architecture

## 5.1 Kernel je primarni autoritet

Kanonski network facts treba da dolaze iz Linux kernel API-ja, ne iz parsiranja CLI izlaza.

Ne koristiti kao kanonski izvor:

```bash
ip route
ip addr
nmcli
iw
ping
```

Ovi alati mogu biti korisni za dijagnostiku i development, ali ne kao evidence authority.

---

## 5.2 LinuxRouteResolver

Implementacija preko:

```text
NETLINK_ROUTE
RTM_GETROUTE
```

Prikuplja:

- destination
- source
- preferred source
- output `ifindex`
- gateway
- routing table
- route metric / priority
- route type
- multipath podatke
- IPv4/IPv6 route facts

Cilj:

Za svaku probu poznajemo stvarni measurement path.

---

## 5.3 LinuxNetworkChangeObserver

Pretplata na rtnetlink događaje:

```text
LINK
ADDRESS
ROUTE
NEIGHBOUR
```

Kernel event subscription ima prednost nad periodičnim polling-om.

Polling može biti fallback / reconciliation mehanizam, ne primarni izvor.

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

# 7. Linux Wi-Fi Architecture

Windows ima svoj Native Wi-Fi sloj.

Linux ekvivalent koristi:

```text
nl80211 / cfg80211
```

Predložene komponente:

```text
LinuxNl80211Radio
LinuxWifiLinkInspector
LinuxWifiScanCache
LinuxWirelessDiagnostics
```

Gde je dostupno prikupljati:

- SSID
- BSSID
- ifindex
- frequency
- channel
- RSSI/signal
- bitrate
- station state
- interface type
- radio capabilities
- roaming/change events

Važna semantika:

> Nedostupan Wi-Fi metadata nikada ne znači neuspešno internet merenje.

Ethernet, nepoznat driver ili ograničen Wi-Fi API mora i dalje omogućiti osnovni connectivity monitoring.

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

Dodati:

```text
LinuxBootIdentityProvider
```

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

# 11. Unix IPC

## 11.1 Transport

Linux koristi:

```text
AF_UNIX / Unix Domain Socket
```

Predloženi endpoint:

```text
/run/internet-evidence-monitor/control.sock
```

Implementacija:

```text
LinuxUnixDomainSocketTransport : IIpcTransport
```

---

## 11.2 Peer identity

Identitet klijenta dobija se preko kernel-provided credentials:

```text
SO_PEERCRED
```

Koristiti:

- UID
- GID
- PID

Ne verovati klijentskom payload-u za identitet.

Primer:

```text
UI tvrdi:
uid=1000
      ↓
IGNORISATI

kernel potvrđuje:
uid=1000
gid=1000
pid=4382
      ↓
AUTHENTICATION PROVENANCE
```

Važno:

```text
authentication != authorization
```

UID identitet ne daje automatski pravo da izvrši određenu command operaciju.

---

# 12. systemd Service Architecture

Linux servis:

```text
IEM.Service.Linux
```

Koristi:

- .NET Generic Host
- systemd lifetime integration
- journald
- watchdog
- restart recovery
- dedicated service account

Predložena systemd unit konfiguracija:

```ini
[Unit]
Description=Internet Evidence Monitor
After=local-fs.target

[Service]
Type=notify

User=iem
Group=iem

ExecStart=/usr/lib/internet-evidence-monitor/IEM.Service.Linux

Restart=on-failure
RestartSec=5s

WatchdogSec=30s

StateDirectory=internet-evidence-monitor
RuntimeDirectory=internet-evidence-monitor

UMask=0077

NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes

RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK

[Install]
WantedBy=multi-user.target
```

Hardening opcije se uključuju tek kada testovi potvrde da ne blokiraju legitimne probe funkcije.

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

Default:

```text
User=iem
Group=iem
```

Ne koristiti trajni root servis.

Cilj:

```text
CAP_NET_ADMIN → NE
CAP_SYS_ADMIN → NE
root          → NE
```

Ako neka sonda realno zahteva dodatne privilegije:

- dokazati potrebu testovima
- dodati minimalni capability
- dokumentovati provenance
- ne proširivati ceo servis nepotrebno

Mogući capability:

```text
CAP_NET_RAW
```

samo ako je zaista neophodan za izabrani probe model.

---

# 15. Linux Storage Layout

Predlog:

```text
/etc/internet-evidence-monitor/
    appsettings.json

/var/lib/internet-evidence-monitor/
    evidence/
    sessions/
    cases/
    keys/
    state/

/run/internet-evidence-monitor/
    control.sock
    runtime/
```

Kanonski dokaz ne treba stavljati direktno u user-owned lokacije kao:

```text
~/Documents
~/.local/share
```

Preporučen model:

```text
system service
       ↓
/var/lib/.../evidence/
       ↓
CANONICAL

          │ explicit export
          ▼

~/Documents/IEM/
       ↓
USER COPY
```

GUI korisnik ne sme imati mogućnost da kroz prezentacioni sloj direktno mutira kanonski evidence store.

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
- persistent installation key
- service-only filesystem permissions
- atomic provisioning
- stable `KeyId`
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

GUI može da prestane da radi bez prekida monitoring servisa.

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

Ako GUI padne:

```text
GUI failure
```

ne sme postati:

```text
measurement failure
```

---

# 20. Linux Distribution Support Matrix

## Tier A — release blocking

Planirani glavni CI/acceptance target-i:

```text
Ubuntu 25.x
Debian 13
Fedora 43
```

Arhitekture:

```text
x64
arm64 gde je podržano
```

## Tier B — best effort

```text
Ubuntu 24.04
Debian 12
Fedora 42
```

## Community / experimental

```text
Arch
NixOS
Gentoo
Alpine
ostalo
```

Ne obećavati zvaničan support dok acceptance matrix to ne potvrdi.

---

# 21. Packaging Strategy

Stable Linux distribucija:

```text
.deb
.rpm
```

Arhitekture:

```text
linux-x64
linux-arm64
```

po support matrici.

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

# 25. Cross-Platform Golden Parity

Ovo je release blocker.

Isti synthetic semantic input:

```text
same observations
       │
       ├── Windows platform path
       └── Linux platform path
```

mora proizvesti:

```text
same classification
same outage interval
same quality band
same claims
same report semantics
same canonical serialization semantics
```

Dozvoljene razlike:

- platform provenance
- platform-specific adapter metadata
- OS identifiers

Nedozvoljene razlike:

- meaning
- evidence classification
- trust level
- quality semantics
- report claim semantics

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

```text
Fresh VM
  ↓
install .deb/.rpm
  ↓
verify package/release
  ↓
systemd starts service
  ↓
GUI connects through UDS
  ↓
SO_PEERCRED authenticated
  ↓
start shortened 48h-style test
  ↓
network namespace fault injection
  ↓
suspend/resume
  ↓
service restart
  ↓
system reboot
  ↓
resume session
  ↓
complete evidence
  ↓
sign
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

Final state:

```text
LINUX RELEASE ACCEPTED
```

---

# 28. Invarijante 211–240

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

GUI ne dobija direktno write vlasništvo nad kanonskim evidence store-om.

## 227

`DISTRIBUTION_PACKAGING_NEVER_CHANGES_EVIDENCE_SEMANTICS`

`.deb`, `.rpm` i drugi paketi ne utiču na evidence meaning.

## 228

`DISPLAY_BACKEND_NEVER_CHANGES_EVIDENCE_SEMANTICS`

X11/Wayland je prezentacija, ne evidence.

## 229

`DESKTOP_INTEGRATION_FAILURE_NEVER_CHANGES_MONITORING_EXECUTION`

GUI/desktop kvar ne zaustavlja servisni monitoring.

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

Uraditi:

- inventar Windows-only API-ja
- inventar native dependencies
- platform dependency manifest
- characterization test matrix
- Linux threat model
- parity contract
- draft invariants 211–240
- lista platform-specific output fields
- define unsupported/unknown semantics
- baseline package compatibility

Gate:

```text
Existing 3.0 test suite remains green
No Windows behavior change
```

---

## 3.1-1 · Platform Composition Boundary

Uraditi:

- izdvojiti `IEM.Service.Runtime`
- izdvojiti `IEM.Presentation`
- DI composition roots
- platform factory/registration
- zabraniti direct platform dependency iz Core/Evidence
- architecture tests

Gate:

```text
Windows semantic behavior unchanged
No platform assembly referenced from canonical layers
```

---

## 3.1-2 · Linux Host + systemd

Uraditi:

- `IEM.Linux`
- `IEM.Service.Linux`
- Generic Host
- systemd lifetime
- journald
- watchdog
- restart recovery
- service account
- storage/runtime directories
- hardened unit baseline

Gate:

```text
Service survives restart
Service starts without network
Failure returns correct non-zero state
```

---

## 3.1-3 · Unix IPC & Identity

Uraditi:

- Unix Domain Socket
- `SO_PEERCRED`
- UID/GID/PID identity
- authorization policy
- protocol framing parity
- Linux CLI→service
- socket lifecycle
- stale socket cleanup
- permissions tests

Gate:

```text
Client payload cannot spoof identity
Unauthorized command rejected
Authorized command semantics match Windows
```

---

## 3.1-4 · Linux Routing & Link Truth

Uraditi:

- rtnetlink
- interfaces
- addresses
- route resolution
- route events
- gateways
- preferred source
- metrics
- IPv4
- IPv6
- VPN/tunnel semantics
- route changes during session

Gate:

```text
Measurement path resolved from kernel facts
Route change never fabricates outage
```

---

## 3.1-5 · Linux Probe Execution

Uraditi:

- ICMP probe
- DNS probe
- TCP probe
- HTTP/HTTPS probe
- interface/route-bound probes
- target quorum
- retry semantics
- timeout parity
- minimal privilege model
- network namespace harness

Gate:

```text
Synthetic fault matrix classified deterministically
Windows/Linux semantic outcomes match
```

---

## 3.1-6 · Linux Power / Time / Reboot

Uraditi:

- logind `PrepareForSleep`
- suspend
- resume
- observability gaps
- boot identity
- monotonic clock
- wall-clock provenance
- restart vs reboot distinction
- clock adjustment tests

Gate:

```text
Suspend never becomes outage
Reboot never becomes outage without network facts
```

---

## 3.1-7 · Linux Wi-Fi

Uraditi:

- nl80211 adapter
- Wi-Fi link state
- RSSI/signal
- SSID/BSSID
- channel/frequency
- station metadata
- scan cache
- NetworkManager enrichment
- no-NetworkManager path
- unsupported driver behavior

Gate:

```text
Missing Wi-Fi metadata never invalidates generic monitoring
```

---

## 3.1-8 · Linux Crypto & Canonical Storage

Uraditi:

- Linux signing identity
- key provisioning
- stable KeyId
- software-protected baseline
- permissions
- `/var/lib` canonical store
- atomic writes
- crash recovery
- no silent key rotation
- redaction/signing compatibility

Gate:

```text
Key identity survives restart/upgrade
Broken key fails closed
Evidence signs and verifies cross-platform
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
GUI loss does not affect measurement
No UI state becomes evidence state
```

---

## 3.1-11 · Linux Packaging

Uraditi:

- `.deb`
- `.rpm`
- self-contained runtime
- x64
- ARM64 where supported
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

Uraditi:

```text
Linux evidence → Windows verifier
Windows evidence → Linux verifier
```

Plus:

- golden semantic vectors
- canonical serialization comparisons
- claims parity
- report parity
- redaction parity
- signature verification parity
- failure/adversarial matrix

Gate:

```text
No unexplained semantic divergence
```

---

## 3.1-13 · Linux Installation Lifecycle

Testirati:

- clean install
- upgrade
- reinstall
- repair-equivalent scenario
- service crash
- service restart
- machine reboot
- config preservation
- key preservation
- evidence preservation
- uninstall
- reinstall after uninstall
- downgrade rejection/handling

Gate:

```text
Existing evidence remains valid after lifecycle operations
```

---

## 3.1-14 · Linux Release Acceptance

Realne VM / machine acceptance:

```text
Ubuntu
Debian
Fedora
```

Testirati:

- service startup
- GUI IPC
- measurement
- fault injection
- Wi-Fi/Ethernet where available
- suspend/resume
- reboot
- signing
- report
- redaction
- cross-platform verification
- upgrade
- uninstall
- evidence preservation

Gate:

```text
LINUX RELEASE ACCEPTED
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

# 31. CI Matrix

Predložena GitHub Actions matrica:

```text
Windows:
  windows-latest
  x64

Linux:
  ubuntu
  debian container/VM
  fedora container/VM

Architecture:
  x64
  arm64 where practical
```

Test klase:

```text
unit
architecture
characterization
integration
network namespace
IPC
crypto
storage
power/time simulation
package
cross-platform parity
E2E
release acceptance
```

Posebni release gate:

```text
SOURCE
  ↓
RESTORE
  ↓
BUILD
  ↓
UNIT TESTS
  ↓
ARCHITECTURE TESTS
  ↓
LINUX INTEGRATION
  ↓
NAMESPACE FAULT TESTS
  ↓
PARITY TESTS
  ↓
PACKAGE
  ↓
SBOM
  ↓
SIGN
  ↓
VERIFY
  ↓
FRESH INSTALL
  ↓
E2E
  ↓
CROSS-PLATFORM VERIFY
  ↓
RELEASE ACCEPTED
```

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

IEM mora raditi i bez GUI-ja.

Podržati:

```text
server
mini PC
Raspberry Pi-class ARM64 device
desktop
laptop
```

Headless mode koristi:

- service
- CLI
- verifier
- evidence export

GUI je optional presentation layer.

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

- Linux servis radi pod systemd
- nema root requirement kao default
- Unix IPC koristi kernel peer credentials
- route truth dolazi iz kernel networking state-a
- Wi-Fi koristi nl80211 gde je moguće
- NetworkManager nije obavezan
- suspend/resume se pravilno klasifikuje
- reboot/service restart ne postaje outage
- signing identity je persistent i fail-closed
- canonical storage je service-owned
- Linux UI je samo presentation layer
- Linux evidence se verifikuje na Windowsu
- Windows evidence se verifikuje na Linuxu
- semantic parity suite je 100% green
- package upgrade ne mutira evidence
- uninstall ne briše evidence
- SBOM/release manifest/signatures su validni
- distro acceptance matrix je zelena
- invarijante 211–240 su zaključane
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
Avalonia
systemd
Unix Domain Socket
rtnetlink
nl80211
Linux crypto provider
```

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

1. kompletan Windows-native dependency inventory,
2. platform contract matrix,
3. characterization/parity test plan,
4. Linux threat model,
5. draft architecture tests,
6. finalizovan set Invarijanti 211–240.

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
