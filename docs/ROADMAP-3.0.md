# Put do 3.0

## Šta 3.0 treba da bude

2.7 je rešavala jedno pitanje: **da aplikacija ne tvrdi više nego što zna.**

3.0 rešava sledeće: **da može da pokaže kako zna ono što tvrdi.**

Razlika je u tome što posle 3.0 skoro svaka važna tvrdnja ima odgovor na četiri pitanja:

```
ŠTA je izmereno?
KAKO je izmereno?
KOJOM stvarnom mrežnom putanjom?
KAKO se zna da rezultat posle toga nije neprimetno promenjen?
```

Danas je odgovoreno prvo pitanje. Drugo delimično. Treće se svodi na „tabela ruta se slaže" -
što nije isto što i „soket je stvarno išao tim putem". Četvrto ima pošten ali skroman odgovor:
lanac je unutrašnje dosledan, a kontrolni zbirovi stoje u istom folderu u koji se piše.

3.0 nije izdanje sa više mogućnosti. To je izdanje sa **arhitekturom dokaza**.

---

## Faze

Redosled je izabran tako da prvo raste dokazna vrednost, pa kvalitet merenja, pa tek onda
prikaz i distribucija. Svaka faza je upotrebljiva sama za sebe.

### 3.0-0 · Zamrzavanje i osnova (ovo izdanje dokumenata)

Bez ijedne izmene ponašanja.

- `EVIDENCE-MODEL-4.md`, `THREAT-MODEL-3.0.md`, `MIGRATION-2.x-TO-3.0.md`, `INVARIJANTE.md`
- `baseline/v2.7.2/` - prava sesija snimljena pravim rekorderom, bez ijednog privatnog podatka
- characterization testovi koji je čitaju: lanac se verifikuje, indeks se pregrađuje na iste
  vrednosti, nalaz i predmet nose svoje verzije, izveštaj nosi formulacije iz 2.7.2
- CI korak koji odbija da fajl iz `baseline/` postoji samo lokalno

**Kriterijum:** 3.0 kod ne sme da učini nijedan artefakt iz 2.7.2 nečitljivim, i to se vidi kao
pad testa a ne kao primedba u pregledu.

### Beleška uz redosled, 18.08.2026.

Posle 3.0-1a projekat je otvoren za testiranje (serija 2.8.0-beta), i to je promenilo izvor
posla. Tri od četiri ispravke u beta.3 i beta.4 došle su iz prijava, ne iz plana:

- **DNS nalaz iz cele grupe i iz uporedive kontrole** (invarijanta 15) - pitao se samo prvi
  resolver, a odgovor se poredio sa javnim resolverom druge adresne familije
- **beli ekran** - GPU efekti uklonjeni; najverovatniji uzrok, ali nije potvrđen
- **prozor jedne veličine** - slobodan da se razvlači dolazio je u oblik u koji pregled ne staje

Zaključak za redosled: dok testeri rade, njihove prijave imaju prednost nad fazama ispod. Faza
se ne prekida zbog svake sitnice, ali nalaz koji obara neku tvrdnju programa ide odmah - to je
i razlog zašto ovaj projekat postoji.

### 3.0-1 · Stvarna putanja merenja

Danas: tabela ruta se slaže sa izabranim adapterom. Sutra: **ovaj soket je išao ovim
interfejsom.**

Dve podfaze, i namerno razdvojene - da testovi nikada ne zamute razliku između „posmatrao sam
putanju" i „nametnuo sam putanju".

**3.0-1a · `Observed`** - **urađeno.** Jedini cilj je bio pouzdano uhvatiti stvarnu putanju.

- `SocketsHttpHandler.ConnectCallback` beleži `LocalEndpoint`, `RemoteEndpoint` i adresnu
  familiju za svaku konekciju merenja
- lokalna adresa se preslikava u interfejs, pa nastaje `ConnectionAttempt` - **činjenica**
- `PathAgreement { Match, Mismatch, Unknown }` je **zaključak** iz te činjenice i traženog
  adaptera, sa vezom na zapise iz kojih je izveden
- ništa se ne forsira: pita se šta bi sistem uradio, i zapisuje šta jeste
- `ActualPathMismatch` je defekt merenja; `Unknown` nije, jer je već pokriven proverom tabele ruta
- adrese se zapisuju raspakovane: soket dvostrukog steka javlja IPv4 vezu kao `::ffff:a.b.c.d`,
  pa bi svako IPv4 merenje na ovoj mašini bilo zavedeno kao IPv6 - a mešovita familija je
  upravo ono zbog čega se familija i beleži

**3.0-1b · `Forced`** - **urađeno.** Merenje koje se namerno vezuje za izabrani adapter.

- `MeasurementIntent { ObserveSystemPath, MeasureRequestedInterface }` - dva različita pitanja,
  pa i dva različita nalaza.
- **Važno značenje:** `ObserveSystemPath` znači neforsirana putanja samog IEM measurement
  connection-a, a ne dokaz putanje bilo koje druge aplikacije na računaru.
- Nivo modelovanja je `ConnectionAttempt` za svaku vezu, a svaki prenos brzine pokazuje na
  skup konekcija (`ConnectionAttempt` set) koji ga je nosio.
- bindovan soket bez rute → `MeasurementStatus = NotExecuted`, razlog
  `NoRouteFromRequestedInterface`. Nikada `0 Mbps` ni „spora veza".
- `TunnelIndication { Detected, NotDetected, Unknown }` sa signalima i verzijom detektora.
  Tunel je zaključak, ne činjenica, i `PathAgreement` ne zavisi od njega - poređenje interfejsa
  je opažanje, „ovo liči na VPN" nije. Interface prefiks (`wg0`, `tun0`) je samo slab signal;
  primarni signal gde je dostupan je kernel `linkinfo.kind` / `DeviceType`.
- `ActualMeasurementPathConfirmed` je definisan i potvrđen (sve posmatrane veze se poklapaju
  sa traženim interfejsom bez nerazrešenog ostatka).


### 3.0-2 · Kanonski manifest i ugovor o potpisivanju - **urađeno.**

- **3.0-2A (Kanonski model i RFC 8785 JCS)**:
  - `EvidenceManifest` model (`ManifestSchemaVersion = 1`, `Canonicalization = "RFC8785-JCS"`, `Session`, `Evidence`, `Files[]`, `AcquisitionContext`, `CreatedUtc`).
  - `JsonCanonicalizer` po RFC 8785 (deterministički redosled svojstava, UTF-8 bez BOM-a, minimalni whitespace, kanonsko formatiranje brojeva i stringova).
  - 64-bitni brojevi koji prevazilaze IEEE-754 preciznost se serijalizuju kao decimalni stringovi.
- **3.0-2B (Opseg manifesta i inventar fajlova)**:
  - `Files[]` pokriva: `Raw/**`, `Evidence/**` i nepromenljive metapodatke sesije.
  - Isključeni artefakti: `manifest.json`, `manifest.sig`, `timestamp.tsr`, privremeni fajlovi `*.tmp` i `Exports/**` (sprečavanje ciklusa i zavisnosti).
  - Sve putanje su relativne, sa forward-slash `/`, bez `./` i `../`, deterministički sortirane (Ordinal).
- **3.0-2C (manifest.json kao direktni kanonski bajtovi)**:
  - `manifest.json` sadrži tačne kanonske UTF-8 bajtove koji se heširaju (`manifest.json` jeste canonical bytes).
- **3.0-2D (Definicija ugovora za potpis i vremenski žig)**:
  - `SignatureEnvelope` (`EnvelopeVersion`, `ManifestSha256`, `KeyId`, `SignatureSuite`, `SignatureBase64`).
  - `ComputeTimestampMessageImprint()` nad kanonskim bajtovima omotnice (priprema za RFC 3161 u 3.0-4).
- **Invarijante**:
  - Invarijanta 19: `MANIFEST_NEVER_DESCRIBES_MUTABLE_EVIDENCE` (provera nepromenljivosti i veličina tokom finalizacije).
  - Invarijanta 20: `MANIFEST_IS_COMPLETE_OR_DOES_NOT_EXIST` (atomski upis preko `.tmp` i zamena).
- **Golden test fixture**:
  - `Fixtures/Canonicalization/` (`input.json`, `expected-manifest.json`, `expected-sha256.txt`) kao cross-platform golden standard.


### 3.0-3 · Ključevi i potpisivanje - **urađeno.**

- **3.0-3A (Platform-neutralni ugovor o potpisivanju)**:
  - `IEvidenceSigningIdentity` i `IEvidenceKeyProvider` u `IEM.Evidence.Crypto`.
  - `SignatureSuite` (`ECDSA_P256`, `SHA256`, `SubjectPublicKeyInfoDer`, `Rfc3279DerSequence`).
  - `KeyProtectionClaim` (`Protection` = `TpmBacked` / `SoftwareProtected`, `Evidence` = `ProviderReported`).
  - Potpisuje se tačno SHA-256 heš kanonskog manifesta (`SignHashAsync`).
- **3.0-3B (Deterministički KeyId)**:
  - `KeyId = "sha256:" + Hex(SHA256(SubjectPublicKeyInfoDer))` (Invarijanta 21).
  - Verifikator nezavisno izvodi i proverava KeyId bez eksternog mapiranja.
- **3.0-3C (Instalacioni identitet i zabrana tihe rotacije)**:
  - Jedna IEM instalacija poseduje jedan trajni signing identitet koji potpisuje sve sesije.
  - Ako se postojeći ključ ne može otvoriti, proces baca `SigningIdentityUnavailableException` (Invarijanta 22: `SIGNING_IDENTITY_NEVER_ROTATES_SILENTLY`).
- **3.0-3D (Windows CNG implementacija)**:
  - `WindowsCngKeyProvider` u `IEM.Windows.Crypto`:
    - Pokušava TPM-backed (`Microsoft Platform Crypto Provider`) prilikom prvog kreiranja.
    - Softverski fallback na `Microsoft Software Key Storage Provider` samo prilikom prvog kreiranja.
    - Neizvozivi privatni ključ (`CngExportPolicies.None`, Invarijanta 24).
- **3.0-3E (Atomsko potpisivanje i verifikacija)**:
  - `ManifestSigner.SignManifestAtomicallyAsync` kreira `manifest.sig.tmp`, vrši automatsku samoproveru preko `SignatureVerifier` i atomski preimenuje u `manifest.sig`.
  - Invarijanta 23: `SIGNATURE_IS_BOUND_TO_EXACT_MANIFEST`.


### 3.0-4 · Vremenski žig treće strane (RFC 3161) - **urađeno.**

- **3.0-4A (MessageImprint nad tačnim bajtovima manifest.sig)**:
  - Invarijanta 25 (`TIMESTAMP_IS_BOUND_TO_EXACT_SIGNATURE_ENVELOPE`): `MessageImprint` se računa direktno nad sirovim bajtovima `manifest.sig` sa diska.
- **3.0-4B (RFC 3161 zahtev sa nonce-om i certReq)**:
  - `Rfc3161TimestampRequest.CreateFromHash` sa SHA-256, 128-bitnim nasumičnim nonce-om i `requestSignerCertificates: true`.
  - Čuvanje i zahteva i odgovora (`Evidence/timestamp/timestamp.tsq` i `timestamp.tsr`).
- **3.0-4C (Verifikacija odgovora i RFC 5816 podrška)**:
  - `Rfc3161TimestampVerifier` proverava ASN.1/DER strukturu, poklapanje `MessageImprint`-a, poklapanje `nonce`-a, i CMS digitalni potpis TSA autoriteta (uključujući `id-aa-signingCertificateV2`).
- **3.0-4D (Stanja vremenskog žiga i Invarijanta 17)**:
  - `TrustedTimeState` (`NotRequested`, `Pending`, `PresentUnverified`, `ValidTrusted`, `ValidUntrusted`, `Invalid`).
  - Semantika po Invarijanti 17: žig dokazuje postojanje potpisanog paketa pre `GenTimeUtc`, nikada vreme mrežnog događaja.
- **3.0-4E (Odsustvo mreže i bezbedan retry)**:
  - Mrežni prekid/timeout tokom finalizacije ostavlja paket u statusu `TrustedTime = Pending` (paket ostaje zapečaćen i validan).
  - Invarijanta 26: `TIMESTAMP_RESPONSE_IS_NEVER_PUBLISHED_BEFORE_SELF_VERIFICATION` (atomski upis u `timestamp.tsr.tmp` $\to$ `timestamp.tsr`).
  - Invarijanta 27: `PENDING_TIMESTAMP_NEVER_REBUILDS_SIGNED_EVIDENCE` (retry koristi postojeći `manifest.sig` bez regeneracije dokaza).
- **3.0-4F (Očuvanje offline validacionog materijala)**:
  - Sertifikati potpisnika se čuvaju u `Evidence/timestamp/validation/certificates/`.
- **Golden test fixture i test suite**:
  - `Fixtures/Rfc3161/` offline fixture i testovi pokrivaju sve scenarije bez zavisnosti od javnog TSA servisa u CI.


### 3.0-5 · Zaseban verifikator (Independent Verifier) - **urađeno.**

- **3.0-5A (Dvodimenzionalni model rezultata i stabilni izlazni kodovi)**:
  - `VerificationReport` sa ortogonalnim dimenzijama `Integrity` (`Verified`, `Incomplete`, `Invalid`) i `Trust` (`Established`, `NotEstablished`, `NotApplicable`).
  - Četiri korisnička zbirna stanja: `VERIFIED` (0), `VALID — TRUST NOT ESTABLISHED` (10), `INCOMPLETE` (20), `INVALID` (30), plus `UNSUPPORTED` (40) i `INPUT_ERROR` (50).
- **3.0-5B (Potpuno odvojen, platform-neutralan projekat)**:
  - `IEM.Verification` (net10.0 biblioteka) i `IEM.Verifier` (`iem-verifier` CLI alat).
  - Invarijanta 28 (`VERIFIER_HAS_NO_PLATFORM_IMPLEMENTATION_DEPENDENCY`): nula zavisnosti od `IEM.Windows`, `IEM.Service`, `IEM.App`.
- **3.0-5C (Nezavisna provera 4 sloja)**:
  - Provera sirovog lanca evidencije (`ChainVerifier`), manifesta, digitalnog potpisa i RFC 3161 vremenskog žiga.
- **3.0-5D (Zaštita od neprijateljskog inputa i path traversal napada)**:
  - Invarijanta 29 (`VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT`): `PathSafety` odbacuje apsolutne putanje, `..` segmente, oznake diskova i NUL karaktere.
- **3.0-5E/F (Nezavisna verifikacija potpisa i opcioni pinning ključa)**:
  - Invarijanta 30 (`EMBEDDED_PUBLIC_KEY_PROVES_SIGNATURE_MATCH_NOT_EXTERNAL_IDENTITY`).
  - Podrška za `--expected-key-id <sha256:...>` i `--trusted-key <spki.der>`.
- **3.0-5G/H (Pravi offline rad i strogi read-only pristup)**:
  - Invarijanta 31 (`OFFLINE_VERIFICATION_NEVER_SILENTLY_USES_NETWORK`): garantovano 0 DNS/HTTP/OCSP/AIA zahteva u `--offline` režimu.
  - Invarijanta 32 (`VERIFICATION_IS_STRICTLY_READ_ONLY`): verifikacija ne menja nijedan bajt paketa.
- **3.0-5I (Human i JSON izlaz, Golden Forensic Fixtures)**:
  - Podrška za `iem-verifier <folder>` (formatiran izveštaj na srpskom) i `iem-verifier <folder> --json`.
  - Golden fixtures u `tests/IEM.Core.Tests/Fixtures/Verifier/`.


### 3.0-6 · Višestruke probe i gubitak odgovora po meti (Target Probe Loss & Delay Variation) - **urađeno.**

- **3.0-6A (Razdvajanje činjenica i statistike, IPPM standard)**:
  - `TargetProbeAttempt` (posmatranje/činjenica u sirovom lancu) vs `TargetProbeStatistics` (statistika nad uzorkom).
  - Ishodi: `ReplyReceived`, `NoReplyBeforeTimeout`, `DestinationUnreachable`, `LocalExecutionFailure`, `Cancelled`.
- **3.0-6B (Lokalni kvar probe nikada nije mrežni gubitak)**:
  - Invarijanta 33 (`LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS`): `LocalExecutionFailure` se isključuje iz imenilaca mrežnog gubitka (`EligibleCount = ExecutedCount - LocalFailureCount`).
- **3.0-6C (Statistika isključivo po jednoj meti)**:
  - Invarijanta 34 (`LOSS_RATIO_IS_NEVER_AVERAGED_ACROSS_TARGETS`): nema prosečivanja procenata gubitka preko različitih meta.
- **3.0-6D (Eksplicitne mrežne greške)**:
  - `DestinationUnreachable` se beleži odvojeno od tihog tajmauta (`NoReplyBeforeTimeout`).
- **3.0-6E (Model statistike i terminologija)**:
  - `TargetProbeStatistics` sa `NoReplyRatio`, brojem zakazanih, izvršenih, kvalifikovanih i odgovorenih proba.
- **3.0-6F (RTT statistika samo iz primljenih odgovora)**:
  - Invarijanta 35 (`TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT`): tajmaut se ne sintetiše kao 2000 ms; distribucija RTT-a računa se isključivo nad uspešnim odgovorima.
- **3.0-6G (Deterministički algoritam percentila)**:
  - `ProbePercentileCalculator`: standardna Nearest-Rank metoda ($k = \lceil \frac{P}{100} \times N \rceil$), uz obavezno čuvanje veličine uzorka ($N$).
- **3.0-6H (Varijacija kašnjenja sa eksplicitnim metodom)**:
  - Invarijanta 36 (`DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD`): `RoundTripDelayVariationCalculator` sa metodom `ConsecutiveReplyAbsoluteDifference`.
- **3.0-6I (Perzistirana metodologija uzorkovanja)**:
  - `ProbeMethodology` (`ProbeCount`, `IntervalMs`, `TimeoutMs`, `PayloadBytes`, `SamplingMethod`).
- **3.0-6J (Striktno razdvajanje IPv4 i IPv6 putanja)**:
  - Invarijanta 37 (`PROBE_RESULT_PRESERVES_TARGET_AND_ADDRESS_FAMILY`).
- **3.0-6K (Usklađenost sa činjenicama i dokazna vrednost)**:
  - Invarijanta 38 (`ICMP_NO_REPLY_DOES_NOT_PROVE_PACKET_DROP_LOCATION`).
- **Golden test fixtures**: `tests/IEM.Core.Tests/Fixtures/PacketLoss/`.


### 3.0-7 · Zdravlje meta (Target Health Assessment & Explicit Exclusion) - **urađeno.**

- **3.0-7A (Zdravlje kao nepromenljiva izvedena procena)**:
  - `TargetHealthSnapshot` sa stanjima (`Unknown`, `Healthy`, `Degraded`, `Unresponsive`, `Recovering`), mogućnostima (`Unknown`, `ResponseObserved`, `ResponseNotYetObserved`), doprinosom dokazu (`Full`, `Reduced`, `Suspended`) i težinom (1.0, 0.5, 0.0).
- **3.0-7B (Odsustvo odgovora nikada ne dokazuje nesposobnost za ICMP)**:
  - Invarijanta 39 (`ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY`): ako meta nikada nije odgovorila, status je `ResponseNotYetObserved`, nikada neutemeljeno `IcmpSupported = false`.
- **3.0-7C/F (Nepromenljiva, vremenski segmentirana istorija)**:
  - Invarijanta 40 (`TARGET_HEALTH_NEVER_REWRITES_PRIOR_EVIDENCE`).
  - Invarijanta 41 (`TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY`): promena stanja mete u trenutku $T_1$ ne menja težinu niti briše prethodne opservacije.
- **3.0-7D/E (Prozori posmatranja, histerezis i verzirana politika)**:
  - `TargetHealthPolicy` (`PolicyVersion`, `MinEligibleSamplesPerWindow`, `HealthyLossThreshold`, `DegradedLossThreshold`, `FailureWindowsToDegrade`, `FailureWindowsToUnresponsive`, `RecoveryWindowsRequired`, `PolicyHash`).
  - Za oporavak iz degradiranog stanja potreban je definisani niz uzastopnih čistih prozora (histerezis).
- **3.0-7G (Vidljivost i obrazloženost isključenja)**:
  - Invarijanta 42 (`TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED`): suspendovana meta ostaje u izveštaju uz eksplicitni kod razloga (`ReasonCodes`).
- **3.0-7H/I (Kontekst vršnjaka i zaštita od lažne izolacije)**:
  - Invarijanta 43 (`SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT`): istovremeni pad većine ili svih meta (`PeerContext.IsSharedNetworkFailure`) ne degradira pojedinačnu metu već se prepoznaje kao mrežni/lokalni incident.
- **3.0-7J/K/L (Obnovljivost i izolacija opsega)**:
  - Invarijanta 44 (`TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE`): deterministička rekonstrukcija istorije zdravlja iz sirovih proba.
  - Invarijanta 45 (`TARGET_HEALTH_IS_SCOPED_TO_ENDPOINT_AND_ADDRESS_FAMILY`): potpuno odvojeni tokovi za IPv4 i IPv6.


### 3.0-8 · Profilisanje mogućnosti mrežnog prolaza (Gateway Capability Learning) - **urađeno.**

- **3.0-8A (Stabilan identitet prolaza i mrežni kontekst)**:
  - `GatewayIdentity` (`GatewayId`, `GatewayAddress`, `AddressFamily`, `InterfaceId`, `InterfaceAddress`, `RouteContextRef`).
  - Invarijanta 53 (`GATEWAY_CAPABILITY_IS_SCOPED_TO_GATEWAY_IDENTITY_AND_NETWORK_CONTEXT`): promena mreže ili interfejsa započinje nezavisan profil.
- **3.0-8B (Sirova opažanja kao činjenice - FACT)**:
  - `GatewayCapabilityObservation` (`ObservationId`, `GatewayId`, `GatewayCapabilityKind`, `ObservationMethod`, `Outcome`, `InterfaceId`, `AddressFamily`).
  - Sposobnosti: `IcmpEcho`, `NeighborResolution`, `RoutePresence`, `ManagementResponse`.
- **3.0-8C/D (Učenje profila isključivo pozitivnim dokazima - INFERENCE)**:
  - Invarijanta 46 (`ABSENCE_OF_GATEWAY_RESPONSE_NEVER_PROVES_UNSUPPORTED_CAPABILITY`): nedostatak odgovora je `ResponseNotYetObserved`, nikada `Unsupported`.
  - Invarijanta 47 (`OBSERVED_GATEWAY_CAPABILITY_IS_ESTABLISHED_ONLY_BY_POSITIVE_EVIDENCE`): status `ObservedSupported` uspostavlja se isključivo potvrđenim uspehom.
- **3.0-8E (Nepromenljiva, append-only istorija)**:
  - Invarijanta 48 (`GATEWAY_CAPABILITY_HISTORY_IS_APPEND_ONLY`).
  - Invarijanta 49 (`CURRENT_GATEWAY_BEHAVIOR_NEVER_REWRITES_PRIOR_CAPABILITY_EVIDENCE`): trenutni pad ne poništava istorijski dokazanu sposobnost (prelazi u `PreviouslyObserved`).
- **3.0-8F/G (Ograničenja semantike lokalnih signala)**:
  - Invarijanta 50 (`NEIGHBOR_RESOLUTION_NEVER_PROVES_GATEWAY_FORWARDING`): uspeh ARP/NDP ne dokazuje rutiranje ka internetu.
  - Invarijanta 51 (`ROUTE_PRESENCE_NEVER_PROVES_GATEWAY_REACHABILITY`): postojanje rute u OS-u ne garantuje fizičku dostupnost prolaza.
- **3.0-8H/I (Bihevioralna procena bez lažnih uzroka - ASSESSMENT)**:
  - `GatewayBehaviorState`: `Unknown`, `NormallyResponding`, `ResponseDegraded`, `PreviouslyObservedCapabilityMissing`, `Recovering`.
  - Izostanak ranije dokazanog odgovora generiše `PreviouslyObservedCapabilityMissing`, a ne preuranjeni `GatewayDown`.
- **3.0-8J (Kontinuirano učenje)**:
  - Invarijanta 52 (`INITIAL_LEARNING_WINDOW_NEVER_FREEZES_UNKNOWN_AS_UNSUPPORTED`): istek početnog učenja ne zamrzava status; kasniji odgovor normalno uspostavlja sposobnost.
- **3.0-8K/L (Deterministička obnovljivost)**:
  - Invarijanta 54 (`GATEWAY_CAPABILITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE`).


### 3.0-9 · Zdravlje sopstvenih proba (Probe Execution Health) - **urađeno.**

- **3.0-9A/B (Faktički zapis pokušaja izvršenja - FACT)**:
  - `ProbeExecutionAttempt` (`AttemptId`, `ProbeIdentity`, `TargetRef`, `AddressFamily`, `Stage`, `RawOutcome`, `NativeErrorDomain`, `NativeErrorCode`, `NativeErrorName`, `TimeoutConfiguredMs`).
  - Faze: `Preparation`, `NameResolution`, `SocketCreation`, `Bind`, `RouteResolution`, `Connect`, `Send`, `Receive`, `ProtocolValidation`, `Completion`.
- **3.0-9C/D (Klasifikacija domena otkaza i semantika tajmauta - INFERENCE)**:
  - `ProbeFailureClassification` sa domenima: `None`, `FailedNetwork`, `FailedRemote`, `FailedLocalSystem`, `InternalError`, `Timeout`, `Unknown`.
  - Invarijanta 55 (`LOCAL_EXECUTION_FAILURE_IS_NEVER_REPORTED_AS_NETWORK_FAILURE`): lokalne sistemske greške se nikada ne prijavljuju kao mrežni pad.
  - Invarijanta 56 (`AMBIGUOUS_PROBE_FAILURE_REMAINS_UNKNOWN`): nejasni ishodi ostaju `Unknown`.
  - Invarijanta 57 (`TIMEOUT_DESCRIBES_OBSERVED_NON_COMPLETION_NOT_FAILURE_CAUSE`): `Timeout` opisuje izostanak odgovora pre isteka roka, a ne uzrok pada.
- **3.0-9E/F/G/H (Strogo razdvajanje uzroka grešaka)**:
  - Invarijanta 58 (`NATIVE_ERROR_CODE_IS_EVIDENCE_INPUT_NOT_FINAL_SEMANTIC_CLASSIFICATION`): OS kodovi su ulazni dokaz, a semantika se izvodi kroz verziranu politiku (`ProbeFailurePolicy`).
  - Invarijanta 59 (`INTERNAL_PROBE_ERROR_NEVER_CONTRIBUTES_NETWORK_FAILURE_EVIDENCE`): interne greške koda nikada ne svedoče o padu mreže.
  - Invarijanta 60 (`REMOTE_FAILURE_REQUIRES_POSITIVE_REMOTE_OR_PROTOCOL_FAILURE_EVIDENCE`): `FailedRemote` zahteva pozitivan protokolski odgovor (npr. HTTP 503, SERVFAIL).
  - Invarijanta 61 (`NETWORK_FAILURE_CLASSIFICATION_NEVER_IDENTIFIES_UNPROVEN_ROOT_CAUSE`): `FailedNetwork` opisuje nemogućnost rute, ne nagađajući koji je mrežni čvor kriv.
- **3.0-9I (Eksplicitna podobnost dokaza)**:
  - Invarijanta 62 (`PROBE_EXECUTION_ELIGIBILITY_IS_EXPLICIT_NOT_IMPLICIT`): svaki pokušaj ima status `Eligible`, `Limited`, ili `Ineligible`.
- **3.0-9J/K/L (Zdravlje mehanizma probe, histerezis i obnovljivost - ASSESSMENT)**:
  - Invarijanta 63 (`SINGLE_PROBE_EXECUTION_FAILURE_NEVER_ESTABLISHES_PROBE_UNHEALTHINESS`).
  - Invarijanta 64 (`PROBE_HEALTH_IS_SCOPED_TO_PROBE_IMPLEMENTATION_AND_RELEVANT_CONTEXT`): DNS greške ne kvare ICMP mehanizam, IPv4 ne kvari IPv6.
  - Invarijanta 65 (`PROBE_HEALTH_NEVER_REWRITES_EXECUTION_EVIDENCE`).
  - Invarijanta 66 (`PROBE_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE`).


### 3.0-10 · Raspored foldera i prava pristupa (Session Storage Layout & Access Boundaries) - **urađeno.**

- **3.0-10A (Eksplicitni i verzirani deskriptor rasporeda)**:
  - `SessionLayoutDescriptor` (`layout.json`, `LayoutVersion = 2`, `Raw`, `Derived`, `Evidence`, `Exports`, `StoragePolicyHash`).
  - Invarijanta 67 (`SESSION_STORAGE_LAYOUT_IS_VERSIONED_AND_EXPLICIT`).
- **3.0-10B/C (Klasifikacija artefakata i obuhvat manifesta)**:
  - `ArtifactRole`: `RawEvidence`, `DerivedEvidence`, `IntegrityEnvelope`, `Export`.
  - `ArtifactMutationPolicy`: `AppendOnlyUntilSeal`, `CreateOnce`, `EnvelopePostSealWrite`, `UserMutableExcluded`.
  - Invarijanta 68 (`MANIFEST_SCOPE_IS_DEFINED_BY_ARTIFACT_ROLE`): manifest štiti `layout.json`, `Raw/**`, `Derived/**`.
  - Invarijanta 69 (`USER_WRITABLE_CONTENT_NEVER_BECOMES_PROTECTED_EVIDENCE`): `Exports/**` nikada ne ulazi u manifest.
- **3.0-10D/E (Životni ciklus sesije i nepromenljivost)**:
  - Životni ciklus: `Provisioning` $\to$ `Active` $\to$ `Sealing` $\to$ `Sealed`.
  - Invarijanta 70 (`POST_SIGNATURE_WRITES_ARE_LIMITED_TO_EXPLICIT_ENVELOPE_ARTIFACTS`): post-seal upisi su dozvoljeni isključivo za retry vremenskog žiga (`Evidence/timestamp/`).
  - Invarijanta 71 (`AUTHORITATIVE_RAW_AND_DERIVED_ARTIFACTS_ARE_APPEND_ONLY_UNTIL_SEAL`).
  - Invarijanta 72 (`SEALED_PROTECTED_ARTIFACTS_ARE_NEVER_MUTATED_IN_PLACE`).
  - Invarijanta 73 (`LEGACY_SESSION_LAYOUT_IS_NEVER_MIGRATED_IN_PLACE`): stare v2.x sesije se ne migriraju u mestu.
- **3.0-10F (Slobodna korisnička zona izvoza)**:
  - Invarijanta 74 (`EXPORTS_NEVER_AFFECT_EVIDENCE_INTEGRITY`): brisanje/menjanje izveštaja u `Exports/` ne utiče na verifikaciju paketa.
  - Invarijanta 75 (`USER_WRITABLE_EXPORTS_ARE_NEVER_TRUSTED_AS_EVIDENCE_INPUT`).
- **3.0-10G/H (Zaštita od traversal i reparse napada)**:
  - `SessionPathResolver`:
    - Invarijanta 78 (`PROTECTED_ARTIFACT_PATH_NEVER_ESCAPES_SESSION_ROOT`): odbacivanje `..`, apsolutnih putanja i NUL bajtova.
    - Invarijanta 79 (`PUBLISHED_PROTECTED_ARTIFACT_IS_COMPLETE_OR_ABSENT`): atomski `.tmp` $\to$ final preimenovanje.
  - `WindowsReparsePointGuard`:
    - Invarijanta 77 (`PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS`): provera junction/symlink reparse tačaka.
- **3.0-10I/J/K/L (Prava pristupa, posmatranje zaštite i preflight)**:
  - `WindowsSessionAclProvisioner` (`IStorageProtectionProvider`): primena DACL-a (System/Admin Full, Users Read zaštićene zone, Modify za `Exports`).
  - Invarijanta 76 (`FILESYSTEM_ACL_IS_PROTECTION_PROVENANCE_NOT_CRYPTOGRAPHIC_INTEGRITY`).
  - Invarijanta 80 (`STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR`).
  - Invarijanta 81 (`EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY`).
  - Invarijanta 82 (`FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS`).


### 3.0-11 · Komande preko platformskog IPC-a (Authenticated Platform IPC Command Boundary) - **urađeno.**

- **3.0-11A (Platform-neutralni IPC ugovor)**:
  - `IIpcTransport` i `IpcConnectionContext` sa ulaznim/izlaznim tokovima.
  - Invarijanta 83 (`IPC_TRANSPORT_NEVER_DEFINES_COMMAND_SEMANTICS`): transport prenosi bajtove, Core odlučuje o semantici.
- **3.0-11B/C (Identitet pozivaoca kao FACT i autorizacija)**:
  - `PlatformPeerIdentity` (`WindowsSid`, `UnixUid`, `Generic`).
  - Invarijanta 84 (`PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION`): prepoznavanje SID-a/UID-a je činjenica, ne automatska dozvola.
  - Invarijanta 85 (`TRANSPORT_ACCESS_NEVER_IMPLIES_COMMAND_AUTHORIZATION`): konekcija na pipe/socket ne garantuje izvršenje komande.
- **3.0-11D/E/F (Uokvireni protokoli, enkapsulacija i stroge granice)**:
  - `IpcRequestEnvelope` i `IpcResponseEnvelope` sa verzijom protokola `ProtocolVersion = 1`.
  - `IpcMessageFraming`: 4-bajtovni dužinski prefiks sa gornjim limitom od 1 MB.
  - Invarijanta 86 (`UNKNOWN_IPC_PROTOCOL_VERSION_IS_NEVER_SILENTLY_DOWNGRADED`).
  - Invarijanta 87 (`UNKNOWN_COMMAND_IS_REJECTED_NOT_GUESSED`).
  - Invarijanta 88 (`IPC_MESSAGE_BOUNDARY_IS_EXPLICIT_AND_BOUNDED`).
- **3.0-11G/H (Bela lista komandi, fail-closed autorizacija i invarijante stanja)**:
  - Invarijanta 89 (`IPC_EXPOSES_EXPLICIT_COMMANDS_NEVER_ARBITRARY_SERVICE_EXECUTION`): dozvoljene samo `GetServiceStatus`, `GetActiveSession`, `GetSessionStatus`, `StartSession`, `StopSession`, `FinalizeSession`, `RetryTimestamp`, `CreateExport`.
  - Invarijanta 90 (`UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED`): nepoznat korisnik se odbija statusom `Unauthorized`.
  - Invarijanta 91 (`AUTHORIZED_COMMAND_NEVER_BYPASSES_SESSION_STATE_INVARIANTS`): nijedna komanda ne može mutirati zapečaćene sirove dokaze.
- **3.0-11I/J (Idempotencija i operativni audit)**:
  - Invarijanta 92 (`RETRIED_STATE_CHANGING_REQUEST_NEVER_CAUSES_DUPLICATE_EFFECT`): isti `RequestId` vraća prethodni semantički rezultat bez duplog efekta.
  - Invarijanta 93 (`EVIDENCE_AFFECTING_CONTROL_ACTIONS_ARE_AUDITABLE`): komande koje utiču na sesiju kreiraju `ControlCommandObserved` audit zapis.
- **3.0-11K/L/M (Platformska implementacija i bezbednost prekida veze)**:
  - `WindowsNamedPipeTransport` sa Windows DACL-om i preuzimanjem SID-a pozivaoca.
  - Invarijanta 94 (`CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD`): identitet dolazi isključivo iz OS transporta.
  - Invarijanta 95 (`PLATFORM_CREDENTIAL_FORMAT_NEVER_CHANGES_COMMAND_AUTHORIZATION_SEMANTICS`): isti ugovor za Windows i Linux.
  - Invarijanta 96 (`CLIENT_DISCONNECT_NEVER_INTERRUPTS_A_COMMITTED_EVIDENCE_TRANSITION`).


### 3.0-12 · Identitet pokretanja sistema i neprekidnost vremena (Boot Identity & Time Continuity) - **urađeno.**

- **3.0-12A/B (Faktički vremenski zapisi i dvostruko praćenje proteklog vremena - FACT)**:
  - `BootObservation` i `ClockSample` sa razdvojenim `BootElapsedIncludingSuspend` i `ActiveElapsedExcludingSuspend`.
  - Invarijanta 97 (`SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME`): periodi spavanja (suspend/sleep) se detektuju i isključuju iz mrežnog prekida.
  - Invarijanta 98 (`WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION`): sistemski sat ne meri trajanje; za trajanje se koristi monotoni brojač (QPC).
  - Invarijanta 99 (`MONOTONIC_TIME_IS_NEVER_PRESENTED_AS_ABSOLUTE_UTC`).
- **3.0-12C/D/E/F (Identitet pokretanja sistema i append-only istorija)**:
  - Invarijanta 100 (`BOOT_CONTINUITY_IS_NEVER_ASSUMED_WHEN_IDENTITY_EVIDENCE_IS_AMBIGUOUS`): sumnjiv boot ostaje `Ambiguous`.
  - Invarijanta 101 (`BOOT_IDENTITY_CHANGE_SPLITS_TIME_CONTINUITY`): restart OS-a deli vremensku osu i kreira `BootBoundaryObserved`.
  - Invarijanta 102 (`BOOT_OBSERVATION_HISTORY_IS_APPEND_ONLY`).
- **3.0-12G/H/I (Detekcija skokova i pomeranja sata bez nagađanja uzroka - INFERENCE)**:
  - `ClockContinuityAssessment`: `Continuous`, `ForwardAdjustmentObserved`, `BackwardAdjustmentObserved`, `SuspendIntervalObserved`, `BootBoundaryObserved`.
  - Invarijanta 103 (`CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE`).
  - Invarijanta 104 (`CLOCK_DISCONTINUITY_NEVER_IDENTIFIES_AN_UNPROVEN_ADJUSTMENT_CAUSE`): skok sata se konstatuje deskriptivno bez nagađanja o NTP-u ili korisniku.
- **3.0-12J/K/L/M (Redosled događaja, nepromenljivost i izolacija procesa)**:
  - `EvidenceTime` kompozitna vremenska struktura.
  - Invarijanta 105 (`EVENT_ORDER_WITHIN_A_BOOT_IS_NEVER_DERIVED_FROM_WALL_CLOCK_ALONE`): monotoni brojač i heš-lanac čuvaju tačan redosled čak i ako sat skoči unazad.
  - Invarijanta 106 (`CLOCK_ADJUSTMENT_NEVER_REWRITES_PREVIOUS_EVENT_TIMESTAMPS`).
  - Invarijanta 107 (`MONOTONIC_DURATION_IS_NEVER_COMPUTED_ACROSS_BOOT_INSTANCES`).
  - Invarijanta 108 (`HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION`).
  - Invarijanta 109 (`SUSPEND_RESUME_NEVER_CREATES_A_NEW_BOOT_INSTANCE_BY_DEFAULT`).
  - Invarijanta 110 (`SERVICE_RESTART_NEVER_IMPLIES_HOST_REBOOT`).
- **3.0-12N/O (Pouzdanost, obnovljivost i platformski provenance)**:
  - Invarijanta 111 (`UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY`).
  - Invarijanta 112 (`TIME_CONTINUITY_IS_REBUILDABLE_FROM_PERSISTED_TEMPORAL_EVIDENCE`).
  - `WindowsTimeObservationProvider` (`GetSystemTimePreciseAsFileTime`, `QueryUnbiasedInterruptTimePrecise`, QPC).
  - Invarijanta 113 (`PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS`).


### 3.0-13 · Ocena kvaliteta dokaza (Evidence Quality Engine) - **urađeno.**

- **3.0-13A/B (Opseg tvrdnje i segmentacija intervala - ASSESSMENT)**:
  - `EvidenceQualitySubject` sa namenom tvrdnje (`QualityPurpose`: `TargetReachability`, `LossMeasurement`, `OutageDuration`, `GatewayBehavior` itd.).
  - `EvidenceQualityInterval`: segmentacija vremenske ose na homogene intervale.
  - Invarijanta 114 (`EVIDENCE_QUALITY_IS_ASSESSMENT_NOT_FACT`).
  - Invarijanta 115 (`EVIDENCE_QUALITY_IS_SCOPED_TO_THE_CLAIM_OR_ASSESSMENT_PURPOSE`).
  - Invarijanta 116 (`CURRENT_HEALTH_STATE_NEVER_REWEIGHTS_PRIOR_QUALITY_INTERVALS`).
- **3.0-13C/D/E (Podobnost dokaza, vidljivost i hard gates)**:
  - `QualityEligibility`: `Full`, `Reduced`, `Ineligible`, `Unknown`, `NotObservable`, `NotApplicable`.
  - Invarijanta 117 (`INELIGIBLE_EVIDENCE_NEVER_REENTERS_QUALITY_AGGREGATION`).
  - Invarijanta 118 (`UNKNOWN_QUALITY_INPUT_NEVER_COUNTS_AS_POSITIVE_SUPPORT`).
  - Invarijanta 119 (`NON_OBSERVABLE_TIME_IS_NEVER_TREATED_AS_NEGATIVE_NETWORK_EVIDENCE`): vreme spavanja računara (suspend) se isključuje iz imenioca aktivnog posmatranja.
  - `EvidenceContributionDecision`: Invarijanta 120 (`REDUCED_OR_EXCLUDED_EVIDENCE_IS_ALWAYS_VISIBLE_AND_REASONED`).
  - Invarijanta 121 (`CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY`): diskontinuitet vremena hard-gate-om limitira tvrdnju o trajanju na `Limited`.
- **3.0-13F (Eksplicitni imenilac pokrivenosti)**:
  - `EvidenceCoverage`: Invarijanta 122 (`QUALITY_COVERAGE_DENOMINATOR_IS_ALWAYS_EXPLICIT`).
- **3.0-13G/H (Kripto-integritet i zrelost procene)**:
  - Invarijanta 123 (`PACKAGE_INTEGRITY_NEVER_PROVES_MEASUREMENT_TRUTH`): digitalni potpis ne dokazuje istinitost lošeg merenja.
  - Invarijanta 124 (`INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS`): nevažeći paket daje `OverallEvidenceBand = Insufficient`.
  - Invarijanta 125 (`TRUST_NOT_ESTABLISHED_IS_NEVER_PRESENTED_AS_INVALID_MEASUREMENT_EVIDENCE`).
  - Invarijanta 126 (`PROVISIONAL_QUALITY_IS_NEVER_PRESENTED_AS_FINAL`): `Provisional` za živu sesiju, `Finalized` nakon pečaćenja.
- **3.0-13I/J/K/L (Verzirana politika, nepromenljivost i obnovljivost)**:
  - `EvidenceQualityPolicy` sa hešom politike: Invarijanta 127 (`EVIDENCE_QUALITY_POLICY_IS_VERSIONED_AND_HASHED`).
  - Invarijanta 128 (`QUALITY_REANALYSIS_CREATES_A_NEW_ASSESSMENT_AND_NEVER_MUTATES_THE_OLD_ONE`).
  - Invarijanta 129 (`EVIDENCE_QUALITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE`).
  - Invarijanta 130 (`EVIDENCE_QUALITY_NEVER_REWRITES_OR_DELETES_SOURCE_EVIDENCE`).
  - Invarijanta 131 (`PLATFORM_PROVENANCE_NEVER_CHANGES_EVIDENCE_QUALITY_SEMANTICS`).


### 3.0-14 · Jedan model dokumenta (Unified Report Document Model) - **urađeno.**

- **3.0-14A/B/C (Jednosmerni pipeline, ulazni snapshot i kanonski model - PRESENTATION)**:
  - `EvidenceAnalysisSnapshot` kao jedini i nepromenljivi ulaz za generisanje izveštaja.
  - `ReportDocumentModel` i `ReportCompositionProfile` (`Technical`, `Complaint`, `Ratel`).
  - Invarijanta 132 (`REPORT_RENDERERS_NEVER_CONTAIN_EVIDENCE_BUSINESS_LOGIC`).
  - Invarijanta 133 (`REPORT_MODEL_CONSUMES_ESTABLISHED_ANALYSIS_AND_NEVER_REINTERPRETS_RAW_EVIDENCE`).
  - Invarijanta 134 (`DOCUMENT_PURPOSE_MAY_CHANGE_COMPOSITION_BUT_NEVER_EVIDENCE_SEMANTICS`).
- **3.0-14D/E/F (Tipizirano AST stablo, ReportClaim i struktuirane vrednosti)**:
  - Tipizirani blokovi: `HeadingBlock`, `ParagraphBlock`, `ClaimBlock`, `MetricBlock`, `TableBlock`, `TimelineBlock`, `NoticeBlock`, `QualityBadgeBlock`, `IntegrityNoticeBlock`.
  - `ReportValue` sa eksplicitnim tipovima (`Numeric`, `Integer`, `Duration`, `Timestamp`, `Unknown`) i lokalizacijom bez promene numeričkih činjenica.
  - Invarijanta 135 (`CANONICAL_REPORT_MODEL_CONTAINS_SEMANTIC_BLOCKS_NOT_RENDERER_MARKUP`).
  - Invarijanta 136 (`EVERY_EVIDENTIARY_REPORT_CLAIM_PRESERVES_ITS_EPISTEMIC_CLASS_AND_PROVENANCE`).
  - Invarijanta 137 (`LOCALIZATION_AND_FORMATTING_NEVER_CHANGE_REPORT_VALUE_SEMANTICS`).
  - Invarijanta 138 (`UNKNOWN_REPORT_VALUE_IS_NEVER_REPLACED_BY_ZERO_EMPTY_OR_INFERRED_TEXT`).
- **3.0-14G/H/I/J (Kvalitet tvrdnji, integritet, poverenje i vremenski redosled)**:
  - Invarijanta 139 (`OVERALL_REPORT_QUALITY_NEVER_ERASES_CLAIM_SPECIFIC_QUALITY`).
  - Invarijanta 140 (`REPORT_PRESENTATION_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY`).
  - Invarijanta 141 (`REPORT_TIMELINE_NEVER_VISUALIZES_NON_OBSERVATION_AS_NETWORK_FAILURE`): period spavanja računara (suspend) se prikazuje kao period bez osmatranja a ne mrežni pad.
  - Invarijanta 142 (`REPORT_EVENT_ORDER_PRESERVES_EVIDENCE_TIME_ORDER_NOT_WALL_CLOCK_SORTING_ALONE`).
- **3.0-14K/L/M/N (Rendereri, projekcije, narativi i determinizam)**:
  - `HtmlReportRenderer`, `CsvReportProjection`, `ComplaintNarrativeComposer`, `RatelRegulatoryComposer`.
  - Invarijanta 143 (`RENDERER_LIMITATION_NEVER_CHANGES_OR_INVENTS_EVIDENCE_MEANING`).
  - Invarijanta 144 (`NARRATIVE_TEMPLATE_NEVER_STRENGTHENS_THE_UNDERLYING_CLAIM`): nepoznat uzrok (`Unknown`) ostaje nepoznat i nikada ne optužuje operatora bez dokaza.
  - Invarijanta 145 (`GENERATED_REPORT_IS_TRACEABLE_TO_THE_ANALYSIS_AND_POLICY_VERSIONS_THAT_PRODUCED_IT`).
  - Invarijanta 146 (`REPORT_DOCUMENT_MODEL_IS_DETERMINISTIC_FOR_IDENTICAL_SEMANTIC_INPUT`).
  - Invarijanta 147 (`RENDERING_IS_STRICTLY_READ_ONLY_WITH_RESPECT_TO_REPORT_AND_EVIDENCE_MODELS`).
  - Invarijanta 148 (`EXPORT_RENDERING_FAILURE_NEVER_INVALIDATES_SOURCE_EVIDENCE`).
  - Invarijanta 149 (`SENSITIVITY_METADATA_NEVER_SILENTLY_REDACTS_OR_ALTERS_AUTHORITATIVE_REPORT_CONTENT`).
  - Invarijanta 150 (`REPORT_GENERATION_CAPTURES_AN_EXPLICIT_ANALYSIS_SNAPSHOT_AND_NEVER_IMPLICITLY_TRACKS_FUTURE_STATE`).


### 3.0-15 · Preuređen prozor (MONITOR / EVIDENCE / CASE / SPEED) - **urađeno.**

- **3.0-15A/B (Prezentacioni snimak i separacija stanja - PRESENTATION)**:
  - `PresentationSnapshot` kao nepromenljivi, atomski snimak stanja sesije i analize.
  - `PresentationRevisionTracker`: zaštita od zastarelih snimaka (Invariant 168).
  - `ShellTab` navigacija (`Monitor`, `Evidence`, `Case`, `Speed`).
  - Invarijanta 151 (`UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS`).
  - Invarijanta 152 (`LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS`).
  - Invarijanta 153 (`UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS`).
  - Invarijanta 154 (`UI_STATE_IS_NEVER_EVIDENCE_STATE`): selekcija taba ili scroll ne ulaze u paket dokaza.
  - Invarijanta 155 (`UI_PREFERENCES_NEVER_ENTER_THE_EVIDENCE_PACKAGE`).
  - Invarijanta 156 (`UI_NAVIGATION_NEVER_CHANGES_MEASUREMENT_EXECUTION`).
- **3.0-15C/D/E (MONITOR, EVIDENCE i CASE celine)**:
  - `MonitorViewModel`: osmatranja, zdravlje sondi i meta, vremenska linija sa razdvojenim `HostSuspended` (Invariant 161).
  - Invarijanta 159 (`UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED`).
  - Invarijanta 160 (`SERVICE_CONNECTIVITY_FAILURE_IS_NEVER_PRESENTED_AS_NETWORK_CONNECTIVITY_FAILURE`).
  - `EvidenceViewModel`: jasno razdvojeni Integritet (`Verified`), Poverenje (`NotEstablished`) i Kvalitet (`Strong`) (Invariant 162).
  - Invarijanta 163 (`OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY`).
  - `CaseViewModel`: radni prostor slučaja sa `UserStatement` izjavama koje nikada ne postaju tehnički dokazi (Invarijante 164 i 165) i read-only pregledom (Invariant 166).
- **3.0-15F/G/H (SPEED i vizuelna semantika)**:
  - `SpeedViewModel`: namera merenja, interfejs, i zaštita od lažnog `0 Mbps` kada merenje nije pokrenuto (Invariant 167).
  - `SemanticVisualTokens`: Invarijanta 170 (`VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE`).
  - Invarijanta 171 (`UI_TOOLKIT_IS_PRESENTATION_IMPLEMENTATION_NOT_EVIDENCE_SEMANTICS`).
  - Invarijanta 172 (`PRESENTATION_ABSENCE_OR_LOAD_FAILURE_IS_NEVER_PRESENTED_AS_MEASUREMENT_FAILURE`).
  - Invarijanta 173 (`UI_EXPORT_OR_PREVIEW_FAILURE_NEVER_CHANGES_EVIDENCE_STATUS`).


### 3.0-16 · Redigovani paket za deljenje (Redacted Evidence Package) - **urađeno.**

- **3.0-16A/B (Model domena i deterministički Redaction Engine - REDACTION)**:
  - `RedactedEvidencePackage`, `RedactionManifest`, `RedactionPolicy` (`StandardPrivacy`, `StrictAnonymization`), `RedactionRule`, `RedactionEntry`.
  - Invarijanta 174 (`REDACTION_NEVER_MUTATES_SOURCE_EVIDENCE`): originalni dokazi ostaju 100% netaknuti.
  - Invarijanta 175 (`REDACTED_PACKAGE_IS_ALWAYS_EXPLICITLY_DERIVED`).
  - Invarijanta 178 (`REDACTION_POLICY_IS_VERSIONED_AND_HASH_BOUND`).
  - Invarijanta 179 (`SAME_SOURCE_AND_POLICY_PRODUCE_THE_SAME_REDACTED_SEMANTICS`).
  - Invarijanta 180 (`REDACTION_NEVER_FABRICATES_REPLACEMENT_EVIDENCE`).
  - Invarijanta 181 (`REMOVED_INFORMATION_NEVER_BECOMES_UNKNOWN_MEASUREMENT_DATA`).
- **3.0-16C/D/E (Poreklo, lanac derivacije i verifikacija)**:
  - `RedactionManifest` čuva `OriginalManifestSha256`, `RedactionPolicyHash`, listu `RedactionEntry` (sa `FieldHashBefore`, nikada plaintext - Invarijanta 182).
  - `RedactedPackageVerifier`: verifikacija porekla do originala i integriteta izvedenog paketa (`ValidRedactedDerivative`, `OriginalManifestMismatch`, `RedactionPolicyMismatch`, `RedactedContentTampered`, `SignatureInvalid`).
  - Invarijanta 176 (`REDACTED_PACKAGE_ALWAYS_BINDS_TO_THE_ORIGINAL_MANIFEST_HASH`).
  - Invarijanta 177 (`ORIGINAL_SIGNATURE_IS_NEVER_REPRESENTED_AS_SIGNING_REDACTED_CONTENT`).
  - Invarijanta 183 (`REDACTED_PACKAGE_TAMPERING_NEVER_INVALIDATES_SOURCE_EVIDENCE`).
  - Invarijanta 184 (`REDACTION_FAILURE_NEVER_MODIFIES_ORIGINAL_EVIDENCE_STATE`).
  - Invarijanta 185 (`USER_SHARE_POLICY_NEVER_CHANGES_CANONICAL_EVIDENCE_SEMANTICS`).
  - Invarijanta 186 (`REDACTION_SCOPE_IS_EXPLICIT_AND_AUDITABLE`).
  - Invarijanta 187 (`UNRECOGNIZED_SENSITIVE_FIELDS_FAIL_CLOSED_WHEN_POLICY_REQUIRES_COMPLETE_REDACTION`).
  - Invarijanta 188 (`REDACTED_DERIVATIVE_HAS_ITS_OWN_INTEGRITY_IDENTITY_AND_SIGNATURE`).
  - Invarijanta 189 (`REDACTION_CHAIN_NEVER_LOSES_PROVENANCE_TO_THE_CANONICAL_SOURCE`).
  - Invarijanta 190 (`REDACTED_EXPORT_IS_NEVER_PRESENTED_AS_THE_CANONICAL_EVIDENCE_PACKAGE`).


### 3.0-17 · Instalacija i izdanje (Release Identity, Authenticode Gate, SBOM & Lifecycle) - **urađeno.**

- **3.0-17A/B/C (Release Identity, Authenticode Signing & Gate - RELEASE)**:
  - `ReleaseIdentity` kao jedinstveni kanonski identitet celog izdanja (Invarijante 191, 192, 193, 207).
  - `AuthenticodeSignatureState`: provera potpisa, lanca sertifikata, SHA256 algoritma i RFC 3161 timestamp-a.
  - `ReleaseGateEvaluator`: fail-closed verifikacija pre objave (Invarijante 194, 195, 196, 197, 209).
  - Invarijanta 198 (`SIGNED_ARTIFACT_IS_NEVER_MUTATED_AFTER_SIGNING`).
  - Invarijanta 210 (`DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET`).
- **3.0-17D/E (SBOM i Release Manifest)**:
  - `SbomGenerator`: automatsko kreiranje SPDX-2.3/CycloneDX SBOM-a i hešovanje (Invarijante 200, 201).
  - `ReleaseManifest`: razdvajanje domena poverenja izdanja i dokaza (Invarijante 199, 202, 203).
- **3.0-17F/G (Installer Acceptance & Evidence Safety)**:
  - `InstallerAcceptanceSimulator`: simulacija celog životnog ciklusa.
  - Invarijanta 204 (`INSTALL_OR_UPGRADE_NEVER_MUTATES_EXISTING_CANONICAL_EVIDENCE`).
  - Invarijanta 205 (`UNINSTALL_NEVER_SILENTLY_DELETES_USER_EVIDENCE`).
  - Invarijanta 206 (`INSTALLER_FAILURE_NEVER_LEAVES_A_FALSE_RUNNING_SERVICE_STATE`).
  - Invarijanta 208 (`RELEASE_ACCEPTANCE_REQUIRES_FRESH_INSTALL_RUNTIME_VERIFICATION`).
  - Ultimativni end-to-end acceptance scenario (Clean Install $\to$ Service Run $\to$ Record Session $\to$ Verify Evidence $\to$ Redact Derivative $\to$ Verify Redacted $\to$ Upgrade $\to$ Re-verify Evidence $\to$ Uninstall $\to$ Data Preservation Verified).


---

## Cross-Platform Arhitektura & Linux Izdanje

Linux podrška se razvija kao **druga platformska implementacija istog IEM evidence engine-a**,
poštujući Invarijantu 18 (`PLATFORM_IMPLEMENTATION_NEVER_LEAKS_INTO_EVIDENCE_SEMANTICS`) i 18a
(`PLATFORM_SOURCE_IS_PROVENANCE_NOT_SEMANTICS`).

### Arhitektura Projekata

```
src/
 ├─ IEM.Core                 [net10.0] - IRouteResolver, ILinkInspector, IBootIdentityProvider
 ├─ IEM.Storage              [net10.0] - SQLite, Raw hash-chain, Derived ledger, SessionPaths
 ├─ IEM.Evidence             [net10.0] - IEvidenceKeyProvider, IEvidenceSigningIdentity, Manifest, RFC3161
 ├─ IEM.Legal                [net10.0] - Zamrznuti pravni registar, rokovi, propisi
 │
 ├─ IEM.Windows              [net10.0-windows] - Windows implementation (CNG, NativeWifi, IP Helper)
 ├─ IEM.Linux                [net10.0]         - Linux implementation (Netlink, D-Bus/nl80211, boot_id)
 │
 ├─ IEM.Service.Core         [net10.0] - IIpcTransport, ICallerIdentityResolver, engine host
 ├─ IEM.Service.Windows      [net10.0-windows] - Windows Service (SCM, NamedPipe)
 ├─ IEM.Service.Linux        [net10.0]         - systemd daemon (sd_notify, Unix Domain Socket)
 │
 ├─ IEM.Cli                  [net10.0] - Cross-platform CLI (iem, verifikator)
 │
 ├─ IEM.App                  [net10.0-windows] - Windows WPF Desktop UI
 └─ IEM.App.Linux            [net10.0]         - Linux Avalonia Desktop UI
```

Composition root (DI) bira implementacije na startupu:
- Windows: `services.AddIemWindowsPlatform()`
- Linux: `services.AddIemLinuxPlatform()`

Nema god-object locatora; klase primaju samo uske interfejse koje koriste.

### Parity Gates (Kriterijumi Pariteta)

1. **CANONICALIZATION PARITY**: Isti platform-neutralni FACT input $\to$ identični kanonski bajtovi $\to$ identičan SHA-256 heš na Windows-u i Linux-u.
2. **VERIFIER INTEROPERABILITY**: Paket snimljen na Linux-u uspešno proverava Windows verifikator; paket snimljen na Windows-u uspešno proverava Linux verifikator.
3. **SEMANTIC PARITY**: Isti kontrolisani mrežni scenario (npr. prekid veze, gašenje rutera) $\to$ isti tipovi tvrdnji, stanja i poštovanje svih invarijanti (bez zahteva za identičnim heš-lancem između fizički različitih sesija).

### Faze Linux Implementacije (L0 – L6)

- **Faza L0 · CI & Multi-Platform Matrix**
  - `ubuntu-latest` CI build za `IEM.Core`, `IEM.Storage`, `IEM.Evidence`, `IEM.Legal`
  - `ubuntu-latest` pokretanje `IEM.Core.Tests`
  - `linux-arm64` compile/publish gate u CI-ju
- **Faza L1 · IEM.Linux Platform Providers**
  - `LinuxRouteResolver`: `rtnetlink` (`NETLINK_ROUTE`) kao primarni izvor; `procfs` isključivo kao dijagnostički fallback (nikada ne proizvodi `Confirmed` rutu). Prepoznavanje `linkinfo.kind`.
  - `LinuxWifiInspector`: NetworkManager D-Bus $\to$ `nl80211` fallback $\to$ `Unknown` state.
  - `LinuxBootIdentityProvider`: `/proc/sys/kernel/random/boot_id` + `CLOCK_BOOTTIME`.
  - `LinuxPowerEvents`: `systemd-logind` D-Bus signali za suspend/resume.
  - `LinuxKeyProvider`: TPM2 / PKCS#11 / secure software keyfile.
- **Faza L2 · iem CLI & iem-verifier**
  - Cross-platform CLI: `iem --proveri`, `iem --izvestaj`, `iem --predmet`.
  - Samostalni `iem-verifier` bez zavisnosti.
- **Faza L3 · systemd Daemon (iem-service)**
  - `Microsoft.Extensions.Hosting.Systemd`, `Type=notify`, `Restart=on-failure`.
  - Unix Domain Socket (`/run/iem/iem.sock`) sa `SO_PEERCRED` native interop slojem i POSIX dozvolama (`0660`, grupa `iem`).
  - `iem.service` unit definicija.
- **Faza L4 · Interoperability & Parity Verifikacija (Cross-Platform Parity Gate)**
  - Izvršavanje Canonicalization Parity, Verifier Interoperability i Semantic Parity testova.
  - **Cross-Platform Parity Gate**:
    - Windows dokazni paket $\to$ Linux `iem-verifier` $\to$ offline provera uspešna.
    - Linux dokazni paket $\to$ Windows `iem-verifier` $\to$ offline provera uspešna.
  - Testiranje na nativnom x64 i ARM64 Linux okruženju.
- **Faza L5 · Avalonia Desktop GUI (IEM.App.Linux)**
  - Pinovana podržana Avalonia verzija u trenutku izrade GUI faze.
  - Baseline X11, Wayland experimental opt-in.
  - Iste 4 celine: MONITOR, EVIDENCE, CASE, SPEED.
- **Faza L6 · Pakovanje i Distribucija**
  - `.deb` (Ubuntu/Debian), `.rpm` (Fedora/RHEL), self-contained tarball arhive.

---

## Prihvatni scenario za 3.0.0 (Core Release Gate)

Ne pušta se dok ovo ne prođe od početka do kraja:

```
čista VM → instalacija → 12 h nadzora → kratak prekid → VPN gore/dole → spavanje/buđenje
→ restart sistema → nastavak → merenja brzine → zatvaranje sesije → potpis manifesta
→ vremenski žig → PDF → paket za prigovor → redigovana kopija
→ provera ORIGINALA → provera REDIGOVANE KOPIJE
→ prenos na drugu mašinu → provera tamo, bez instalirane aplikacije i bez mreže
→ deinstalacija → provera PONOVO
```

Poslednji red je poenta: dokazni paket mora biti proverljiv bez originalne instalacije i bez
originalne mašine. Kada Linux port uđe u formalno izdanje (kroz Fazu L4), prihvatni scenario se
proširuje na unakrsnu proveru na drugom operativnom sistemu.

---

## Šta 3.0 svesno ne dobija

Nalozi, backend, telemetrija, mobilna aplikacija, dashboard preko interneta, AI dijagnoza,
poređenje operatera, automatsko slanje RATEL-u, stotine novih meta. Sve to je moguće kasnije i
ništa od toga ne povećava dokaznu vrednost onoga što se već snima.

