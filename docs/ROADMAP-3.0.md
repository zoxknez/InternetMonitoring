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

**3.0-1b · `Forced`** - merenje koje se namerno vezuje za izabrani adapter.

- `MeasurementIntent { ObserveSystemPath, MeasureRequestedInterface }` - dva različita pitanja,
  pa i dva različita nalaza; rezultat sa nametnutom putanjom ne dokazuje kojim putem ide
  korisnikov obični saobraćaj
- bindovan soket bez rute → `MeasurementStatus = NotExecuted`, razlog
  `NoRouteFromRequestedInterface`. Nikada `0 Mbps` ni „spora veza"

**Kasnije, uz njih:** `TunnelIndication { Detected, NotDetected, Unknown }` sa signalima i
verzijom detektora. Tunel je zaključak, ne činjenica, i `PathAgreement` ne sme da zavisi od
njega - poređenje interfejsa je opažanje, „ovo liči na VPN" nije.

Tek posle svega ovoga sme da postoji `ActualMeasurementPathConfirmed`.

### 3.0-2 · Potpisan manifest

- kanonska serijalizacija (stabilan redosled polja, UTF-8, normalizacija, format brojeva i
  vremena) - bez toga potpis nije ponovljiv
- `manifest.json`: verzija, sesija, vreme, verzije modela, završni otisak lanca, spisak fajlova
  sa veličinom i SHA-256, otisak pravnog konteksta
- `manifest.sig`

### 3.0-3 · Ključevi

- Windows CNG, ključ koji se ne izvozi; TPM gde postoji
- **potpisuje servis**; prozor i konzola nemaju pristup privatnom ključu
- u manifestu stoji samo identifikator javnog ključa

### 3.0-4 · Vremenski žig treće strane

- RFC 3161 nad otiskom `manifest + potpis`
- bez mreže u trenutku završetka: `TrustedTime = Pending`, nikad `Invalid`; kasnije se
  timestampuje **isti postojeći otisak**, nikada rekonstruisan paket
- sertifikati i podaci o opozivu putuju u paketu, jer provera mora da radi bez interneta

### 3.0-5 · Zaseban verifikator

Prenosiv, samo za čitanje, radi bez instalirane aplikacije. Ishod ima tri stanja, ne dva:
proveren; matematički ispravan ali koren poverenja nepoznat; neispravan.

Verifikator potvrđuje **integritet i poreklo paketa**, ne mrežni zaključak.

### 3.0-6 · Pravi gubitak paketa

Tek ovde se termin vraća. Više proba ka **istoj** meti, po meti: poslato, primljeno, izgubljeno,
min/median/p95/max RTT, džiter. Bez prosečivanja procenata preko meta - dve zdrave i jedna koja
filtrira ICMP nisu „23 % gubitka" nego jedna meta sa sopstvenim problemom.

### 3.0-7 · Zdravlje meta

Istorija po meti (uspešnost, uzastopni neuspesi, sposobnost za ICMP, trenutno stanje). Meta koja
se pokvari gubi težinu u klasifikaciji - ali se **ne izbacuje tiho**: evidencija kaže da je
isključena i zašto.

### 3.0-8 · Šta ruter ume

Prvih nekoliko minuta uči se profil: odgovara li na ICMP, kako se ponaša ARP, ima li upravljački
odgovor. Ako je „ICMP dokazano podržan" pa prestane - to je jak signal. Ako je „nepouzdan",
izostanak odgovora ne sme sam po sebi da znači `GatewayDown`.

### 3.0-9 · Zdravlje sopstvenih proba

Razdvojiti „mreža nije odgovorila" od „naš kod nije uspeo da izvrši probu". Kategorije:
`FailedNetwork`, `FailedRemote`, `FailedLocalSystem`, `InternalError`, `Timeout`. U evidenciji
piše „DNS proba nije izvršena: resolver API vratio grešku X", ne „DNS pao".

### 3.0-10 · Raspored foldera i prava

```
Sesija/
  Raw/        servis piše, korisnik čita
  Evidence/   servis piše, korisnik čita
  Derived/    servis piše, korisnik čita
  Exports/    korisnik piše
```

Ponovna izrada izveštaja čita `Raw/` + `Evidence/` i piše u `Derived/` ili `Exports/` - nikada
u izvor.

### 3.0-11 · Komande preko imenovane cevi

Sve što je danas fajl-zahtev u folderu u koji korisnik piše, a nije po prirodi trajan podatak,
prelazi na cev sa ACL-om: `RequestId`, vreme, SID pozivaoca, komanda, i zapis u dnevniku
servisa.

### 3.0-12 · Boot id i neprekidnost vremena

Checkpoint nosi i identifikator pokretanja sistema. Ako se proces restartovao a Windows nije,
trajanje se rekonstruiše; ako se Windows restartovao, period **nije meren** i tako se i
prijavljuje - ne kao prekid interneta.

### 3.0-13 · Ocena kvaliteta dokaza

Jedno mesto, ne deset `if`-ova po prikazu. Dimenzije: pokrivenost, izolacija lokalne putanje,
potvrda spolja, provera stvarne putanje, kvalitet merenja, integritet, pouzdano vreme, zdravlje
proba, pravni kontekst. Ishod je pojas (`Strong`/`Moderate`/`Limited`/`Insufficient`), a ne
procenat - `87,4 %` izgleda kao verovatnoća a nije.

### 3.0-14 · Jedan model dokumenta

```
Raw evidence → EvidenceAnalysis → ReportDocumentModel → HTML | PDF | CSV | prigovor | RATEL
```

Model nosi gotove činjenice, ne oblikovanje. Time
`PRESENTATION_NEVER_CLAIMS_MORE_THAN_RAW_EVIDENCE` postaje test nad jednim objektom umesto nad
pet renderera.

### 3.0-15 · Preuređen prozor

Četiri celine: MONITOR, EVIDENCE, CASE, SPEED. Namerno tek ovde - raspored ekrana pre nego što
se zna šta se sve prikazuje bio bi posao dva puta.

### 3.0-16 · Redigovani paket za deljenje

Izvedena kopija bez imena mreža, BSSID-a, imena mašine i privatnih adresa. Original se ne dira.
Redigovani paket nosi otisak originalnog manifesta, politiku redakcije, nov manifest i nov
potpis - pa se može dokazati da je izvedena verzija baš tog originala.

### 3.0-17 · Instalacija i izdanje

Authenticode za program, servis i instalater; SBOM; prošireni CI do prihvatnog scenarija na
čistoj virtuelnoj mašini.

---

## Prihvatni scenario za 3.0.0

Ne pušta se dok ovo ne prođe od početka do kraja:

```
čista VM → instalacija → 12 h nadzora → kratak prekid → VPN gore/dole → spavanje/buđenje
→ restart Windows-a → nastavak → merenja brzine → zatvaranje sesije → potpis manifesta
→ vremenski žig → PDF → paket za prigovor → redigovana kopija
→ provera ORIGINALA → provera REDIGOVANE KOPIJE
→ prenos na drugu mašinu → provera tamo, bez instalirane aplikacije i bez mreže
→ deinstalacija → provera PONOVO
```

Poslednji red je poenta: dokazni paket mora biti proverljiv bez originalne instalacije i bez
originalne mašine.

---

## Šta 3.0 svesno ne dobija

Nalozi, backend, telemetrija, mobilna aplikacija, dashboard preko interneta, AI dijagnoza,
poređenje operatera, automatsko slanje RATEL-u, stotine novih meta. Sve to je moguće kasnije i
ništa od toga ne povećava dokaznu vrednost onoga što se već snima.
