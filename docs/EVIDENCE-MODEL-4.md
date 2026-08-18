# Model dokaza 4

Šta 3.0 zapisuje, i po čemu se tri vrste zapisa razlikuju.

Format zapisa 3 (verzija koju nosi 2.7.x) ne pravi razliku između onoga što je **opaženo** i
onoga što je iz toga **zaključeno**. Sve stoji u istom redu lanca, u istom nalazu, u istom
dnevniku. Dva puta se pokazalo da je to skupa razlika:

- Nalaz merenja iz 2.6 nosio je i broj `61.4` i ocenu „ispunjava uslove". Broj nije zastareo.
  Ocena jeste — doneta je po pravilu koje je neproverenu putanju računalo kao proverenu.
- Predmet je nosio rokove bez zapisa o tome pod kojim su pravilima izračunati, pa je svaka
  izmena registra menjala značenje starih predmeta.

Model 4 tu razliku uvodi u sam format.

---

## Tri kategorije

### FACT — direktno opaženo

Ono što je izmereno ili pročitano. Nema tumačenja u sebi.

```
DownloadMbps          = 61.4
ContractedMbps        = 100          (uneo korisnik)
LocalEndpoint         = 192.168.1.25:52718
RemoteEndpoint        = 104.16.0.1:443
InterfaceId           = {AAAA…}
IcmpReply             = timeout
SubmittedDate         = 2026-07-25
```

Činjenica ne zastareva. Ako je zapisana ispravno, ostaje ispravna zauvek. Sme da se popravi
samo kada je pogrešno **zabeležena**, i to nikada tiho.

Svaka činjenica nosi **poreklo** — to 2.7 već radi kroz `FactOrigin` i `AnchoredDate`:
uneo korisnik / izvedeno iz sesije (uz `EvidenceRef`) / preuzeto iz starijeg zapisa / nepoznato.

### INFERENCE — zaključak iz činjenica

Mehanički izveden nalaz. Deterministički: iste činjenice i ista pravila daju isti zaključak.

```
Band                  = Below70Percent
NetworkState          = CpeUpstreamUnreachable
FaultDomain           = UpstreamPath
PathAgreement         = Match
TunnelIndication      = Detected
UnreachableShare      = 33.3 %
```

Zaključak **zastareva** kada se promeni pravilo po kom je donet. Zato mora nositi identitet tog
pravila.

### ASSESSMENT — vrednosna, pravna ili dokazna ocena

Sud o tome šta zaključak znači za nekoga.

```
SpeedAssessment       = Undetermined
EvidenceQuality       = Limited
LegalDeadline         = 2026-10-04
ValidForComplaint     = …
```

Ocena zastareva i kada se promeni pravilo i kada se promeni pravni kontekst. Nosi identitet
oba.

---

## Gde stoji tumačenje: dva zapisa, ne jedan

Sirovi lanac govori **šta je opaženo**. Ne govori šta je verzija koja ga je pisala mislila da to
znači. To dvoje danas stoji u istom redu, i posledica se već videla: nalaz merenja iz 2.6 nosi i
broj i ocenu, pa je ocena preživela pravilo po kom je doneta.

```
Raw/
  evidence-chain.jsonl      samo FACT - šta je posmatrano
Evidence/
  derived-ledger.jsonl      INFERENCE i ASSESSMENT - šta je iz toga zaključeno
  manifest.json             hešira oba
Derived/
  izveštaji, izvozi
```

**Sirovi lanac** nosi opažanja i operativne događaje:

```
ProbeStarted / ProbeCompleted
TargetResponded / TargetTimedOut
ConnectionAttempt (LocalEndpoint, RemoteEndpoint, AddressFamily)
InterfaceSnapshot, RouteSnapshot
SpeedBytesTransferred
SystemSleep / SystemResume / BootChanged
ProbeInternalError
```

**Izvedeni dnevnik** je takođe append-only, ali odvojen:

```
DerivedClaim
    ClaimId
    Kind                  Inference | Assessment
    SourceEvidenceRefs[]  na koje zapise iz lanca se oslanja
    InterpretationRefId
    Value
    CreatedAtUtc
```

Tu žive `IncidentClassification`, `FaultAttribution`, `TunnelIndication`, `PathAgreement`,
`SpeedBand`, `EvidenceQuality`, `LegalResolution`.

Manifest hešira i jedno i drugo, pa ih potpis štiti zajedno — ali se epistemološki više ne
mešaju. Zaključak se uvek može napraviti iznova iz lanca; opažanje ne može ni iz čega.

### Stari lanac se ne popravlja

Lanac iz 2.x sadrži i opažanje i tadašnje tumačenje u istom redu. Ne prepisuje se, ne
preračunava i ne pretvara retroaktivno u čist model 4. Čita se i verifikuje **po svojoj
originalnoj verziji**, a sloj za migraciju samo zna da taj zapis sadrži oboje.

---

## InterpretationRef

Svaki INFERENCE i svaki ASSESSMENT koji se **zapisuje izvan procesa koji ga je izračunao** nosi:

```
InterpretationRef
    Model         "classifier" | "attribution" | "speed-band" | "legal" | "evidence-quality"
    Version       "2.3.0"
    ContentHash   sha256 kanonskog oblika pravila
```

Broj verzije sam nije dovoljan. Dva builda mogu nositi isti broj a različit sadržaj — greškom u
procesu izdavanja, spajanjem grana, ručnom izmenom. `ContentHash` vezuje zaključak za sadržaj
algoritma, ne za obećanje o njemu.

Ovo nije nov mehanizam: `LegalRulesetRef {Id, Version, ContentHash}` u 2.7 radi tačno tako, i
test odbija izmenu objavljene verzije upravo poređenjem heša. Model 4 to proširuje na sve
izvedene tvrdnje umesto da klasifikacija i pripisivanje nose samo broj.

### Katalog, pa referenca

Tačnost po tvrdnji ne mora da znači ponavljanje celog objekta u svakom redu:

```
InterpretationCatalog
    classifier-2.3.0-a91f…      { IncidentClassifier, 2.3.0, sha256:… }
    attribution-2.1-18bc…       { FaultAttribution,   2.1,   sha256:… }
    legal-RS-ZEK-2025-6435…     { LegalRuleset,       …,     sha256:… }

DerivedClaim
    InterpretationRefId: classifier-2.3.0-a91f…
```

Katalog stoji jednom po sesiji, referenca u svakoj tvrdnji. Fizički deduplikovano, semantički
vezano za tvrdnju.

**A tvrdnja, ne sesija, određuje koji unos važi.** Duga sesija može preživeti nadogradnju
servisa ili promenu engine-a, pa pretpostavka „jedna sesija = jedna verzija tumačenja" ne stoji.
Kada se tumačenje promeni usred sesije, to je događaj kao svaki drugi:

```
InterpretationContextChanged
    PreviousRef
    NewRef
    ChangedAtUtc
    Reason
```

i naredne izvedene tvrdnje nose novu referencu.

`FACT` nema `InterpretationRef`. Nema šta da tumači.

### Šta to menja u praksi

Nalaz merenja iz 2.6 danas se prepoznaje po **odsustvu** verzije. To radi, ali radi jednom.
Sa `InterpretationRef` odgovor na pitanje „da li ovaj zaključak još važi" prestaje da bude
„proveri koja ga je verzija napisala" i postaje poređenje heša — mehaničko, i tačno i kada se
verzije poklope a sadržaj ne.

---

## Šta se ne menja

Format zapisa 3 ostaje čitljiv. Model 4 je **nadgradnja**, ne zamena:

- lanac otisaka i njegov način računanja ostaju isti;
- postojeći zapisi bez `InterpretationRef` čitaju se kao što se danas čitaju nalazi bez
  `FindingSchemaVersion` — brojevi se uzimaju, zaključci se izvode iznova;
- `baseline/v2.7.2/` je merilo: sesija snimljena 2.7.2 mora da se verifikuje i pregradi na iste
  vrednosti pod 3.0, i to je test koji pada ako se to pokvari.

---

## Gubitak paketa: staro ime se ne preuzima

Vrednost `PacketLoss` u lancu iz 2.x **zauvek znači ono što je istorijski značila** — udeo meta
koje nisu odgovorile, tri destinacije po jedna proba. Ne preimenuje se, ne preračunava i nikada
se ne prikazuje kao pravi gubitak paketa.

Sloj za čitanje sme da je izloži preciznije, ali kao izvedenu tvrdnju, ne kao ispravku zapisa:

```
LegacyReachabilityMetric
    OriginalFieldName    "PacketLoss"
    Meaning              UnreachableTargetShare
    OriginalValue        33.3
    SourceSchemaVersion  3
```

Kada 3.0-6 uvede pravo merenje, ono je **nov tip**, a ne novo značenje starog:

```
TargetPacketLossMeasurement
    Target, Sent, Received, Lost, LossRatio
    MinRtt, MedianRtt, P95Rtt, MaxRtt, Jitter
```

Između ta dva nema automatskog preslikavanja, ni u jednom smeru. Odatle i invarijanta
`LEGACY_PACKETLOSS_IS_NEVER_INTERPRETED_AS_PACKET_LOSS` — postoji zato da za dve godine niko ne
vidi slična imena i „pojednostavi" mapper.

---

## Kako to izgleda na 3.0-1

Prvi slučaj koji se modeluje po novom su stvarna putanja merenja.

**FACT** — šta je soket uradio:

```
ConnectionAttempt
    Intent            ObserveSystemPath | MeasureRequestedInterface
    RequestedInterface
    LocalAddress, LocalPort
    RemoteAddress, RemotePort
    AddressFamily
    ConnectedAtUtc

ObservedNetworkInterface
    InterfaceId, InterfaceName, LocalAddress
```

**INFERENCE** — šta iz toga sledi:

```
PathAgreement
    State                 Match | Mismatch | Unknown
    SourceEvidenceRefs[]
    InterpretationRefId

TunnelIndication
    State                 Detected | NotDetected | Unknown
    Signals[], Reason
    SourceEvidenceRefs[]
    InterpretationRefId
```

Ovako `IsVpn = true` ne može slučajno da postane činjenica: tunel je uvek zaključak, sa
signalima i verzijom detektora, i `PathAgreement` ne sme da zavisi od njega — poređenje
interfejsa je činjenica, „ovo liči na VPN" nije.

### Namera merenja, ne osobina soketa

`Observed` i `Forced` nisu zastavica na soketu nego **dva različita pitanja**:

| Namera | Pita |
|---|---|
| `ObserveSystemPath` | Kojim putem Windows stvarno vodi ovu konekciju? |
| `MeasureRequestedInterface` | Mogu li, i kojom brzinom, da merim preko baš ovog adaptera? |

Merenje sa nametnutom putanjom dokazuje kvalitet tog adaptera. Ne dokazuje kojim putem ide
korisnikov obični saobraćaj. Zato su dve namere, a ne jedan rezultat sa napomenom — da za godinu
dana izveštaj ne pomeša dva nalaza koja izgledaju isto.
