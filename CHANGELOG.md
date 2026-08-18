# Istorija izmena

Verzije rastu po pravilu koje ovde ima smisla: **prva cifra** je oblik dokaza, **druga**
mogućnosti koje korisnik vidi, **treća** ispravke koje ne menjaju ni jedno ni drugo.

Format zapisa u lancu (`schemaVersion`) i verzije pravila (klasifikacija, pripisivanje,
pouzdanost) navode se posebno, jer se po njima kasnije zna kojom logikom je zaključak donet.
Svaki izveštaj ih ispisuje - i to one pod kojima je sesija snimljena, ne one iz verzije koja
izveštaj pravi.

---

## 2.7.2 - 18.08.2026.

Format zapisa 3, pravila nepromenjena. Targeted review taga `v2.7.1` prošao je jedanaest od
dvanaest tačaka; dvanaesta je otkrila da je F3 zatvoren **pregrubo**.

### Fallback uporište je privremeno, ne konačno

Dok odgovor operatera nije stigao, rok za obraćanje Regulatoru računa se od dana **do kog je
odgovor bio dužan** - to je fallback uporište, i po prirodi je privremeno: događaj od kog rok
zaista teče još se nije desio. Kada odgovor stigne, ista zamrznuta pravila daju drugi datum.

2.7.1 je taj slučaj tretirala kao **konflikt** i zadržavala privremeni rok. Predmet je time
držao kao aktuelan datum za koji je već znao da je izveden iz slabijeg uporišta - a poruka je
uz to savetovala da se „pokrene nov predmet", što je pravno pogrešan savet.

Uzrok je bio i u modelu: `AppliedRule.Anchor` beležio je **primarno** uporište pravila i onda
kada je rok bio razrešen iz fallback-a, pa su se dva datuma prividno razilazila.

Sada:

| Slučaj | Ponašanje |
|---|---|
| Fallback → primarno uporište postalo dostupno | Rok se ponovo razrešava **istim zamrznutim rulesetom**; nov datum je aktuelan, prethodni se čuva kao `Superseded` sa razlogom |
| Datum koji je već korišćen se promeni | Konflikt. Rok ostaje, ništa se ne preračunava |
| Registar se promenio | Nikakva promena; ako zamrznuta pravila nisu dostupna, ostaje `Unresolved` |
| Nova činjenica razrešava potpuno nerazrešen rok | Razrešava se normalno, opet zamrznutim rulesetom |

Invariant `RESOLVED_MILESTONE_NEVER_CHANGES` razložen je na precizniji par -
`FINAL_RESOLUTION_NEVER_CHANGES_SILENTLY` i `FALLBACK_RESOLUTION_IS_PROVISIONAL` - uz
`PRIMARY_ANCHOR_SUPERSEDES_FALLBACK_WITHIN_FROZEN_RULESET`.

Uživo, na predmetu iz 2.6:

```
pre odgovora:    01.10.2026.  [privremen, računat od 02.08 - roka za odgovor]
posle odgovora:  04.10.2026.  [konačan, računat od 05.08 - dana prijema]
                 Pravila predmeta nisu promenjena.
```

### Konflikt i zamena više nisu nevidljivi

`Conflict` se u 2.7.1 upisivao u dnevnik i **nigde nije prikazivao** - ekran je tvrdio da je
rok utvrđen dok je zapis znao da je osporen. Sada se i zamena i konflikt vide u `--predmet`, u
`Rokovi.txt` (konzola i prozor) i u liniji predmeta u prozoru, sa različitim tekstom za dva
različita slučaja.

Iz poruke o konfliktu uklonjen je savet da se pokrene nov predmet - da li je pravno potreban
nov predmet nije procena koju ovaj program ima osnova da donese - i ispravljena dupla tačka.

## 2.7.1 - 18.08.2026.

Format zapisa 3, pravila nepromenjena (klasifikacija 2.3.0, pripisivanje 2.1).

Nezavisan audit taga `v2.7.0` - na čistom checkout-u, ne na radnom stablu - našao je tri
propuštena mesta. Sva tri su ista greška koju 2.7.0 lovi, samo na putu kojim nijedan test nije
išao: **kroz fajl koji je već na disku.** Testovi grade podatke u kodu, pa su ispravljena
pravila proveravali sami na sebi.

### Zaključak iz 2.6 više ne govori kroz 2.7 prikaz

Nalaz merenja (`MerenjeBrzine.json`) čuva i **zaključke**, ne samo brojeve. Nalaz iz 2.6 nosi
`ValidForComplaint: true` - ocenu donetu po pravilu koje je neproverenu putanju računalo kao
proverenu - i `BandLabel` kao tekst, sa regulatornim izrazom koji je 2.7 ukinula. Izveštaj je
zato u istoj tabeli pisao „putanja merenja nije proverena" i, tri reda niže, „ispunjava uslove
za korišćenje uz prigovor".

- Nalaz sada nosi `FindingSchemaVersion`. Bez nje je stariji, pa se njegovi **brojevi uzimaju
  onakvi kakvi su zapisani**, a svaki zaključak izvodi iznova.
- Ranija ocena se ne usvaja niti obrće u „ne ispunjava" - i to bi bio gubitak podatka. Čuva se
  kao istorijska, uz rečenicu zašto se ne preuzima.
- Nova invarijanta: `LEGACY_DERIVED_CONCLUSION_IS_NEVER_TRUSTED_AS_RAW_EVIDENCE`. Ono što je
  ranija verzija **izmerila** jeste dokaz; ono što je **zaključila** nije.

### „Veza je bila stabilna" - popravljeno na sve tri površine

U 2.7.0 sam ovo ispravio u konzoli i prijavio kao zatvoreno. `SessionVerdict` je i dalje vraćao
taj naslov, pa su ga prozor i oba izveštaja i dalje pisali - i piše na snimku ekrana u README-u
iz 2.7.0, posle dva minuta nadzora. Sada:

> **Nije zabeležen nijedan prekid.** Tokom ove sesije (2m 0s) nisu zabeleženi događaji koji
> ukazuju na prekid veze. Rezultat opisuje samo posmatrani period i ne govori o vremenu koje
> nije nadzirano.

Konzola više nema svoju kopiju te odluke nego koristi isti `SessionVerdict`. Regresioni test
koristi baš scenario koji je rupu otkrio - vrlo kratku čistu sesiju - i proverava naslov u
izveštaju, ne samo u funkciji.

### Zamrznut pravni kontekst se više ne odmrzava pri upisu

`CaseJournalStore.Save` je pri **svakom** upisu ponovo razrešavao ceo predmet. Invariant je
držao pri čitanju, a padao pri pisanju: dovoljno je bilo zabeležiti odgovor operatera pod
novijim registrom pa da se preračunaju i rokovi razrešeni mesecima ranije.

- Razrešen rok se prenosi **nepromenjen**. Nov podatak razrešava samo korak koji ranije nije
  mogao biti razrešen, i to **unutar zamrznutih pravila** predmeta - nikad posezanjem za
  današnjim registrom.
- Ako pravila pod kojima je predmet razrešen više nisu u registru, nov rok se ne izvodi;
  predmet to kaže umesto da napravi mešavinu dva režima pod jednim identifikatorom.
- Ako se promeni datum od kog je rok već računat, rok **ostaje** takav kakav je, uz zabeležen
  konflikt. Tiho preračunavanje bi promenilo značenje predmeta na osnovu izmene koju niko nije
  video.
- `Rokovi.txt` i prijava RATEL-u više ne razrešavaju sami; izvor istine je kontekst iz dnevnika.
- Četiri imenovana testa: `LEGAL_RESOLVED_MILESTONE_NEVER_CHANGES_ON_SAVE`,
  `NEW_ANCHOR_ONLY_RESOLVES_DEPENDENT_MILESTONE`, `REGISTRY_UPDATE_ALONE_NEVER_CHANGES_CASE_MEANING`
  i slučaj sa promenjenim uporišnim datumom.

Puna istorija pravnih razrešenja - svako sa svojim poreklom, umesto jednog konteksta koji se
dopunjava - ostaje za 3.0.

### Sitnije, ali iste vrste

- **Nemereno nije nula.** Pločica „Mete bez odgovora" pisala je `0 %` pre prvog uzorka. Sada
  „nije mereno". `UNKNOWN_NEVER_BECOMES_ZERO`.
- „nema osnova za prigovor" pri odbijanju automatske pripreme → „nema dovoljno evidentiranih
  nalaza za automatsku pripremu prigovora". Program ne daje negativan pravni zaključak zato što
  njegov automat nema dovoljno podataka.

### baseline/legacy-2.6/

Stvarni artefakti iz 2.6 sada stoje u repozitorijumu i testovi ih čitaju. Obe greške iz F1 bile
su nevidljive dok su svi testovi gradili podatke u kodu.

## 2.7.0 - 17.08.2026.

Format zapisa **3** (nepromenjen). Pravila: klasifikacija **2.3.0**, pripisivanje **2.1**,
pouzdanost 1.1.

Ovo izdanje ne dodaje nijednu mogućnost. Uvodi jedno pravilo, svuda gde je bilo prekršeno:

> **„Nisam mogao da proverim" nikada ne postaje „proverio sam i u redu je."**

Tri invarianta su postala testovi koji padaju ako se pravilo prekrši, a ne rečenice u
dokumentaciji: nepoznato ne postaje potvrđeno, stari predmet ne menja značenje, i prikaz ne
tvrdi više nego što sirovi zapis sadrži.

### Putanja merenja brzine: četiri stanja umesto dva (P0-3)

`SpeedPath` je vraćao „ispravno" na **prvom** poklapanju rute, uz komentar da je „jedna
poklopljena ruta dovoljna". Nije: host sa IPv4 preko Etherneta i IPv6 preko VPN-a davao je
potvrdu, a `HttpClient` je slobodno mogao da uzme IPv6. Uz to su tri pozivna mesta - konzola,
servis i prozor - radila `?? true`, pa je i „nije moglo da se utvrdi" postajalo potvrda.

- `MeasurementRouteState`: `AllResolvedRoutesMatch`, `MixedRoutes`, `OtherRouteOnly`,
  `Unknown`. Samo prvo stanje nosi prigovor; ostala tri daju svoj nedostatak
  (`PathAmbiguous`, `PathElsewhere`, `PathUnverified`).
- Uz stanje se zapisuje i koja adresna familija odlazi drugom rutom, pa poruka kaže šta da se
  promeni umesto „putanja je dvosmislena".
- Ni najbolje stanje se **ne** zove „potvrđena putanja merenja", nego „tabela ruta je saglasna
  sa izabranim adapterom" - stvarni TCP put se ne vidi pre 3.0.0.

Neka merenja koja su ranije prolazila sada ne prolaze. To je i svrha.

### Wi-Fi: radio mora biti dokazano uključen (P0-4)

Uslov je bio `RadioOn != false`, pa je `null` - „nismo uspeli da pročitamo stanje radija" -
prolazio kao „radio je uključen" i nestanak SSID-a se prijavljivao kao kvar pristupne tačke.
Sada je `RadioOn == true`; nepoznato pada dalje kroz lanac i završava kao `AdapterDown`, koji
tvrdi samo ono što je adapter prijavio.

### „Gubitak paketa" nije bio gubitak paketa (P0-5)

Stanje je računalo procenat iz **tri različite destinacije** sa po jednom probom, pa je jedan
resolver koji iz principa ne odgovara na ping davao „33,3 % gubitka paketa" u prigovoru.
Sada: „Deo meta ne odgovara", sa imenima meta, i izričito da to nije merenje gubitka paketa.
Vrednost `"PacketLoss"` u lancu ostaje nepromenjena; pravi model gubitka ide u 3.0.0.

### Operater više nije proglašen krivim (P0-2)

Model pripisivanja od 2.0 kaže da se sa korisnikovog računara ne može utvrditi čija je mreža u
kvaru - a tekst je i dalje pisao „Prekida kod operatera", „Nedostupnost operatera" i „vaša
oprema je isključena kao uzrok". Sve troje tvrdi više nego što merenje daje: WAN strana samog
rutera, firmver, PPPoE sesija i NAT tabela odavde izgledaju isto kao i prekid dalje u mreži.

- Prikaz sada svuda kaže **„izolovano iza rutera"**, a objašnjenje navodi i šta je izmereno i
  šta nije isključeno.
- U lancu i bazi se ništa ne menja - `FaultAttribution.Upstream` i sve vrednosti ostaju, pa
  stara sesija i dalje čita isto. Podignut je samo broj modela pripisivanja na **2.1**.

### Lanac otisaka dokazuje doslednost, ne poreklo (P0-6)

„Dokazano je da paket nije menjan nakon snimanja" je bilo pretenciozno: `SHA256SUMS.txt` stoji
u istom folderu u koji se piše, pa ko može da izmeni zapis može da preračuna i lanac i zbirove.
Sada stoji šta lanac jeste - unutrašnja doslednost - i uz **svaki** takav nalaz ide ograda da
nezavisan dokaz vremena i porekla traži potpis i vremenski žig treće strane, što ovo izdanje ne
radi.

### Jedno merenje brzine ne dokazuje vremenski kriterijum (P0-7)

„Uobičajeno dostupna brzina" po propisu znači najmanje 80 % ugovorene **u 90 % vremena** - uslov
o vremenu, koji jedno merenje ne može da ispuni. Pojasevi su sada neutralni (`ispod 70 %`,
`70-80 %`, `80-90 %`, `90 % i više`), a objašnjenje kaže šta bi bilo potrebno za zaključak.
Isto važi i u drugom smeru: dobro merenje više ne tvrdi „nema osnova za prigovor", jer veza
koja pada svako veče izmeri se uredno pre podne.

### Pravni rokovi su postali podaci sa poreklom (P0-1)

Rokovi su bili konstante u zapisu čiji je komentar obećavao da su podesive - a nijedan
produkcijski poziv nikada nije prosledio drugu vrednost. Dve su bile pogrešne za nov predmet:
15 dana za odgovor operatera i 15 dana za obraćanje Regulatoru potiču iz **člana 113. ranijeg
zakona**, koji je ostao na snazi samo dok se ne donese akt iz čl. 140 važećeg - a donet je
(Pravilnik, „Sl. glasnik RS" 58/2024, primena od 1.1.2025).

- `LegalCitation`, `LegalRule`, `LegalRuleset`, `LegalRegistry`: svako pravilo nosi **svoje
  vremensko uporište**, uslov na koga se odnosi, granice primene i izvore sa URL-om i datumom
  provere.
- Za predmet pokrenut danas: prigovor **30 dana**, odgovor potrošaču **8 dana** (ZZP), pravnom
  licu **30**, obraćanje Regulatoru **60 dana** (ZEK čl. 139), odluka **90 dana** (čl. 140).
- Režim se ne bira za ceo predmet. Postupak pred Regulatorom počinje **prijemom zahteva**, pa
  prigovor operateru iz novembra 2024. ne povlači zahtev iz januara 2025. u stari režim. Kad
  rok pada preko granice, a zahtev nije podnet, odgovor je **nije utvrđeno** - ne izbor jedne
  od dve mogućnosti.
- Prigovor na **iznos računa** računa se od dospeća računa, a na **kvalitet** od dana
  nemogućnosti korišćenja. Oba su 30 dana, ali od različitog datuma - zato se pamti i uporište,
  ne samo broj.
- Uporišni datum nosi i **poreklo**: sesija sa tri prekida ima tri kandidata, pa program
  predlaže prvi i to kaže, a `--prekid <broj>` bira drugi. Bez izabranog kandidata rok se
  računa, ali je označen kao rekonstruisan.
- Predmet čuva **zamrznut** pravni kontekst - identifikator, verziju, heš i svaki primenjen
  rok sa datumom od kog je brojan. Registar je nepromenljiv: izmena objavljene verzije pada na
  testu, jer bi inače tiho promenila značenje starih predmeta.
- Predmet napravljen starijom verzijom **ne** postaje automatski stari režim. Njegovi datumi se
  čitaju kao preuzeti, rokovi se označe kao rekonstruisani, i to piše u izlazu.
- Nov `--prijavljeno <datum>` beleži dan kada je zahtev stigao Regulatoru. Do sada to nijedno
  mesto u programu nije upisivalo, pa je taj rok bio trajno prijavljen kao propušten.
- 48 sati je rok operateru da otkloni uzrok, a ne najkraće trajanje nadzora - a tako je stajalo
  i u prozoru i u savetu „48 sati je minimum koji se ne može osporiti".

### Sitnije

- Tri identične privatne kopije prelamanja teksta svedene na jednu (`TextWrap`).
- Obrisani mrtvi duplikati u `ComplaintCommand` čiji su se tekstovi već razišli od originala.
- „ostalo jos 1 dana" - grana za jedninu je dodata i u prozoru.

## 2.6.0 - 17.08.2026.

Format zapisa 3, pravila nepromenjena.

### O programu

Prozor je dobio dugme **O programu** u zaglavlju (i istu stavku u meniju ikone u sistemskoj
traci): verzija, licenca, autor, i tri mesta za greške i predloge - GitHub, Discord i mejl. Uz
to i upozorenje šta folder sesije sadrži, jer se to mora znati **pre** nego što se evidencija
pošalje nekome.

Isti tekst ispisuje i `iem --pomoc`. Sve dolazi iz `AppInfo`, jedne klase, pa se prozor,
konzola i README ne mogu raziđi - istekli Discord poziv na jednom mestu a ne na drugima gori
je od nijednog.

- Dijalog je pokriven testovima koji ga stvarno otvore i pročitaju šta je na njemu: verzija,
  licenca, autor, sva tri kanala, i upozorenje o sadržaju sesije.
- Usput nađeno i ispravljeno: `StaticResource` na atributima korenskog elementa prozora traži
  resurs pre nego što se rečnik tog prozora spoji, pa je dijalog padao pri pravljenju - a
  pošto se to dešavalo unutar rukovaoca klika, dugme naprosto **nije radilo ništa**. Sada je
  `DynamicResource` i prozor sam spaja temu, pa stoji i bez `Application` oko sebe.

## 2.5.0 - 17.08.2026.

Format zapisa **3**. Pravila: klasifikacija 2.2.0, pripisivanje 2.0, pouzdanost 1.1.

### Sopstveni saobraćaj više ne tereti operatera

Najveća ispravka u ovom izdanju, i nađena je merenjem uživo: sesija pokrenuta dok je u pozadini
išlo preuzimanje od 25 MB/s dala je za sedamdesetak sekundi osamnaest kratkih „prekida", od
kojih su neki pripisani operateru. Ni jedan nije bio kvar veze - baferi su bili puni sopstvenim
saobraćajem.

- Uz svaki uzorak beleži se koliko je sam računar trošio vezu (`localBps`).
- Prekid nosi najviši viđeni protok tokom njega, i izveštaj to piše: „tokom prekida je i sam
  računar koristio vezu (do X MB/s), pa se ovaj prekid ne može bez rezerve pripisati operateru".
- Signal pouzdanosti „Veza nije bila zauzeta vašim saobraćajem" je do sada **uvek** javljao da
  je provereno i čisto, a ništa ga nije merilo. Sada javlja i „nije provereno".
- Sopstveno merenje brzine ostavlja oznaku na disku (sa rokom, da ne preživi ubijen proces)
  koju nadzor čita, pa taj period upisuje kao „Merenje brzine u toku" umesto kao pogoršanje.

### Izveštaj navodi verzije modela iz same sesije

Izveštaj napravljen naknadno za stariju sesiju tvrdio je da su zaključci izvedeni pravilima
tekuće verzije. Time se gubila jedina svrha tih brojeva - da se razlikuje nesaglasnost od
promenjenog algoritma. Verzije se sada čuvaju uz sesiju i ispisuju iz nje.

### Ostalo

- Testovi ponašanja prozora: nov projekat `tests/IEM.App.Tests` (20 testova, lažni host).
- Prozor je do sada **tiho odbacivao** svako ažuriranje kada nema WPF dispečera; sada se
  izvršava na mestu, čime su ti putevi postali proverljivi.
- README je davao dve putanje koje ne postoje (`artifacts/service`, exe bez podfoldera
  `service\`) - vidi se samo kada se stvarno instalira.
- Zaključani spisak paketa bio je vezan za jednu zakrpu SDK-a, jer je `IEM.App.csproj` sam
  deklarisao `SelfContained` i time uvlačio `Microsoft.NET.ILLink.Tasks` u lock fajl. Sada o
  načinu objavljivanja odlučuje samo `build/publish.ps1`.
- Projekat je otvoren: MIT licenca, CI na GitHub Actions, pravila za doprinose, prijavu
  propusta i ponašanje, spisak tuđeg rada u `NOTICE.md`.

## 2.4.0 - 17.08.2026.

Format zapisa 2.

### Merenje brzine u oba smera i kašnjenje pod opterećenjem

Do sada se merilo samo preuzimanje. Regulatorne metodologije (FCC Measuring Broadband America,
Ofcom/SamKnows, BEREC) opterećuju vezu u oba smera i prate kašnjenje pod opterećenjem - i to je
bila jedina merna veličina koju su njihovi alati imali a ovaj nije.

- Slanje ide na Cloudflare `__up`, tri paralelne veze po smeru, po deset sekundi.
- Odziv se meri dok veza miruje i dok je opterećena u svakom smeru; prijavljuje se **razlika**,
  sa ocenom (neznatno / primetno / VELIKO) i objašnjenjem šta znači za pozive i igre.
- Ocena slanja prema ugovorenoj brzini slanja: `--slanje`, ili par `--brzina 100/20`.
- `--bez-slanja` prepolovljuje utrošak podataka.

### Zakazano merenje preuzeo servis

Zakazivanje je dotad živelo u procesu koji ga je napravio: prozor se gasi zatvaranjem, konzola
traži otvoren prozor. Time je jedini slučaj zbog kog zakazivanje postoji - „izmeri u tri
ujutru" - bio jedini koji nije mogao da se opsluži.

- Zahtev je fajl (`merenje.json`) i preživljava restart; izvršava ga servis.
- Propušten termin stariji od sat vremena se **ne** izvršava naknadno.
- Komande `zakazi-merenje` i `otkazi-merenje`, pipe komanda `SPEED`.

### Ugovorena brzina u prozoru

Jedno polje koje prima „100" ili „100/20", kako i piše u ugovoru. Do tada je svako merenje iz
prozora zapisivano kao neupotrebljivo, jer nije imalo sa čim da se uporedi.

### Ispravke nađene proverom uživo

- **Kanal za status radio je samo pod nalogom `LocalService`.** Pod bilo kojim drugim nalogom
  prva instanca prođe, a sve ostale dobijaju odbijanje na svake dve sekunde.
- **Merenje brzine nije proveravalo kroz koji adapter zaista izlazi.** Čitalo je imenovani
  adapter, a prenos puštalo kuda god sistem odluči, i upisivalo „jedna putanja, bez VPN-a" bez
  provere. Sada nosi nedostatak `PathAmbiguous` kada ne izlazi tim adapterom.

### Šavovi za testiranje

- `WlanLinkInspector` ne zove više `NativeWifi` direktno: platformski pozivi su u
  `NativeWifiRadio` iza `IWirelessRadio`, a pravila su u `IEM.Core` i testiraju se bez radija.
- Protokol kanala za status izdvojen u `IEM.Storage`, zajednički za servis i prozor.

## 2.3.0 - 16.08.2026.

- Ime aplikacije: **Internet Monitoring**.
- Merenje brzine: red čekanja dok je veza zauzeta (`--cekaj`), zakazivanje (`--zakazi`),
  „Izmeri odmah" u prozoru, tri paralelna toka, proxy isključen, sekcija u izveštajima sa
  ocenom valjanosti.
- Pravni modul: dnevnik predmeta, prijava RATEL-u iz konzole i prozora, rok za RATEL 60 → 15
  dana po ZEK čl. 113 st. 6.
- Lažne brojke u napuštenim sesijama (rekonstrukcija isteklih sesija), DNS lažni uspeh (QR bit
  i pravi A zapisi), per-adapter skeniranje i BSS, fallback skeniranja, dvostruki dispose u
  traceru, CGNAT u privatnim skokovima, tray obaveštenja o prekidu i oporavku.
- Distribucija: bez digitalnog potpisa, SHA-256 zbir uz svaku arhivu.

## 2.2.0 - 14.08.2026.

Format zapisa 2. Dve revizije koda pronašle su osam ozbiljnih problema; zajednička nit je bila
ista - alat je na nekoliko mesta bio samouvereniji nego što su merenja dozvoljavala.

- Probe rade na sopstvenim petljama; otkucaj čita policu i ništa ne čeka.
- Keširan uspeh više ne dokazuje da veza radi u trenutku neuspeha.
- Ruta se razrešava po meti; TCP, UDP i ICMP izlaze sa poznate izvorišne adrese.
- Pauza nadzora preseca prekid; spavanje računara se ne računa kao nedostupnost operatera.
- Bez sirovog lanca nema izveštaja; indeks se rekonstruiše iz lanca.
- „Potvrđeni prekidi na strani operatera" → „Prekidi izolovani iza vaše opreme".
- Otvoreni prekid se rekonstruiše iz repa lanca posle pada procesa.
- Pouzdanost je postala dvodimenzionalna (podrška i pokrivenost) i prikazuje se kao pojas.

## 2.1.0 i ranije

Merni engine, klasifikacija kvarova, konzolni pokretač, hash-lanac i SQLite indeks, izveštaji u
HTML-u i PDF-u, Windows servis sa nastavkom sesije posle restarta, grafički interfejs sa tray
ikonom i grafikonima, bežični sloj (RSSI, BSSID, skeniranje SSID-a), trasa, IPv6, UPnP status
rutera.

PowerShell verzija 1.0 stoji u `legacy/` - odatle je projekat počeo.
