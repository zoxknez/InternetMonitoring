# 3.1-7B · Linux Bare-Metal Wi-Fi Simulated Acceptance Report

- **Overall Verdict**: **PASS** (Exit Code `0`)
- **Timestamp UTC**: `2026-08-20T14:29:45.5166838+00:00`
- **Adapter**: `wlan0` (phy0) - Simulating Intel AX200 Wi-Fi 6 (802.11ax / nl80211)
- **Zero Capabilities**: `CapEff=0000000000000000`, `CapAmb=0000000000000000` (PASS)

## Gate Verdicts Matrix

| Gate | Category | Verdict | Note |
|---|---|---|---|
| `ZeroCapabilities` | Mandatory | **PASS** | Strict CapEff=0, CapAmb=0 verified |
| `InterfaceIdentity` | Mandatory | **PASS** | IFINDEX=3, WDEV=0x100000001, WIPHY=0 (phy0) |
| `AssociationTruth` | Mandatory | **PASS** | Associated to 'HomeMesh_5G' (BSSID 00:11:22:33:44:55, 5180 MHz / Ch 36) |
| `ContinuityTruth` | Mandatory | **PASS** | Temporal continuity verified across multi-part queries |
| `ProductionProjectionTruth` | Mandatory | **PASS** | Production LinkSnapshot matches direct nl80211 observation |
| `StationPeerTruth` | Mandatory | **PASS** | Station peer matches associated BSSID |
| `CachedBssTruth` | Mandatory | **PASS** | Cached passive GET_SCAN dump resolves BSSID without active scan |
| `AccessPointEvidence` | Mandatory | **PASS** | Channel 36 and RSSI -58 dBm verified |
| `NumericFidelity` | Mandatory | **PASS** | RX/TX bytes & packets strictly non-decreasing over traffic interval |
| `MloHardwareQualification` | Optional | **NOT_APPLICABLE** | Non-MLO single-link hardware (Wi-Fi 6) |
