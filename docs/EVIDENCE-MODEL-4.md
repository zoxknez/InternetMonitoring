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

## Otvorena pitanja za 3.0-1 i dalje

**Gde stoji `InterpretationRef`.** Po zapisu je najtačnije, ali napuhuje lanac; po sesiji je
jeftino, ali ne pokriva slučaj kada se pravila promene usred duge sesije. Verovatno: po sesiji
za klasifikaciju i pripisivanje (menjaju se između izdanja, ne unutar sesije), po nalazu za
merenje i pravni kontekst (nastaju u jednom trenutku).

**Šta sa `PacketLoss` kao imenom vrednosti u lancu.** Ime je zadržano radi kompatibilnosti iako
prikaz više ne govori o gubitku paketa. Kada 3.0 uvede pravi model gubitka, biće dve različite
stvari sa dva imena — a stari zapisi moraju nastaviti da znače ono što su značili.

**Da li ASSESSMENT uopšte pripada lancu.** Argument za: zapis je potpun. Argument protiv: lanac
je „šta je posmatrano", a ocena se menja sa pravilima, pa je prirodnije da živi u izvedenom
sloju koji se uvek može napraviti iznova. Naginje se drugom.
