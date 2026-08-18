# Šta je preostalo

Stanje na dan **18.08.2026.**, verzija **2.7.1**, ime aplikacije
**Internet Monitoring**, **559 testova prolazi** (535 u jezgru, 24 u prozoru), nula upozorenja
pri gradnji, zaključane zavisnosti prolaze. Projekat je objavljen kao open source
(MIT, GitHub Actions CI zelen).

Ovaj dokument je popis nezavršenog, a ne plan. Pisan je da bi se sutra znalo šta nije urađeno
i **zašto**, jer je to podatak koji se najbrže gubi. Sve što je ranije bilo ovde, a urađeno je
17.08., ukratko je potvrđeno na kraju dokumenta.

Redosled je po važnosti.

---

## 0a. Šta je audit 2.7.0 našao (i 2.7.1 zatvorila)

Nezavisan audit objavljenog taga našao je tri mesta koja su promakla, sva tri iste vrste:
ispravke iz 2.7.0 nisu dopirale do **fajla koji je već bio na disku**. Testovi grade podatke u
kodu, pa su nova pravila proveravali sami na sebi.

Pouka koja ostaje: **zaključak zapisan u fajl je podatak sa rokom trajanja.** Ako se pravilo
po kom je donet promeni, zapisani zaključak postaje istorijski podatak, ne nalaz. Otud dve nove
invarijante - `LEGACY_DERIVED_CONCLUSION_IS_NEVER_TRUSTED_AS_RAW_EVIDENCE` i
`UNKNOWN_NEVER_BECOMES_ZERO` - i folder `baseline/legacy-2.6/` sa stvarnim starim artefaktima
koje testovi čitaju. Isti pristup treba proširiti u 3.0: svaki izvedeni zaključak koji se
upisuje mora nositi verziju pravila po kojima je donet.

## 0. Šta 2.7.0 svesno nije uradila

Izdanje 2.7.0 je svelo tvrdnje na ono što je izmereno. Tri stvari su pri tome **namerno**
ostavljene za 3.0.0, i vredi znati zašto:

**Stvarni TCP put merenja.** Tabela ruta kaže šta bi operativni sistem izabrao, ne šta je
soket zaista uradio. Zato se ni najbolje stanje ne zove „potvrđena putanja" nego „tabela ruta
je saglasna sa izabranim adapterom". Za pravo `ActualMeasurementPathConfirmed` treba
`SocketsHttpHandler.ConnectCallback` sa zapisanim `LocalEndpoint`/`RemoteEndpoint` i adresnom
familijom - posao za sebe, jer se zapisuje u nalaz i menja format.

**Pravi gubitak paketa.** Stanje koje se ranije zvalo „gubitak paketa" broji nedostupne mete
(tri destinacije, po jedna proba). Pravo merenje traži više proba ka **istoj** meti i model
zdravlja pojedinačne mete, da se meta koja iz principa ne odgovara na ping ne meša sa vezom
koja gubi pakete.

**Potpis i vremenski žig.** Lanac dokazuje unutrašnju doslednost. Ko ima pravo upisa u folder
sesije može da preračuna i lanac i kontrolne zbirove, pa dokaz porekla traži potpis (CNG/DPAPI)
i RFC 3161 žig treće strane. Do tada svaki izveštaj to izričito piše.

**Serija merenja brzine.** „Uobičajeno dostupna brzina" po propisu znači 80 % ugovorene u 90 %
vremena - uslov o vremenu. Jedno merenje se sada svrstava u neutralan pojas i kaže šta bi bilo
potrebno za zaključak; longitudinalni model dolazi u 3.0.0.

---

## 1. Fizički Wi-Fi testovi - i dalje jedini deo koji zahteva ljudske ruke

Nepromenjeno u odnosu na 16.08., jer se ništa od ovoga ne može uraditi iz koda: traži se da
neko fizički dira ruter i dongl. Spisak je u `docs/TEST-WIFI.md`, a ostaju:

| Test | Radnja | Očekivani nalaz |
|---|---|---|
| 3 - roaming | šetanje tokom nadzora (2,4/5 GHz isti SSID) | `WifiRoaming`, bez prekida |
| 4 - **najvažniji** | ugasiti bežični radio na ruteru 2-3 min | `WifiRadioDown` (SSID nestao iz skeniranja, **a radio računara pročitan kao uključen**) |
| 6a | izvuci dongl tokom nadzora | `AdapterDown` (lokalni kvar) |
| 6b | restartovati ruter tokom nadzora | `CpeReboot` (uptime unazad) |
| 6c | izvuci WAN kabl sa rutera | `CpeUpstreamUnreachable` - jedini nalaz izolovan iza rutera |

Posle svakog: `dotnet run --project src/IEM.Cli -- --proveri <folder sesije>` - lanac mora
ostati ispravan.

Test 5 (dva bežična adaptera) nije izvodljiv na ovoj mašini - ugrađene kartice nema.

Test 7 (merenje brzine preko Wi-Fi mora reći da **ne važi** za dokazivanje brzine) sada se
može uraditi bez rutera, ali traži da se **izvuče Ethernet kabl**: dok su oba adaptera gore,
saobraćaj ide kablom. Od 17.08. merenje to i proverava i odbija da pripiše figuru pogrešnom
adapteru (odeljak „Potvrđeno zatvoreno"), pa je test sada provera te provere.

## 2. Sopstveni saobraćaj - rešeno, sa jednom svesnom granicom

Rupa opisana ujutru je zatvorena (detalji dole, u „Potvrđeno zatvoreno"). Ostaje granica koju
treba znati:

**Prag je odluka, ne mera zasićenja.** „Veza je bila zauzeta" počinje na 2 MB/s (16 Mbit/s).
Alat ne zna ugovorenu brzinu linije dok nadzire - zna samo brzinu porta, a gigabitni port ne
kaže ništa o usluzi od 50 Mbit/s. Zato je prag nizak i posledica blaga: pouzdanost prekida
pada i izveštaj kaže koliko je računar trošio, ali se prekid **ne briše** i ne prekvalifikuje.
Suprotno bi bilo gore: da alat sam odlučuje da nešto nije bio kvar, pa da se izgubi dokaz o
pravom prekidu koji se slučajno poklopio sa preuzimanjem.

Ako se ikad bude htelo strože: potrebna je procena kapaciteta linije (npr. najviši viđeni
protok kroz sesiju), a to je procena koja se lako pokvari i za koju treba sopstveni krug
provere.

## 3. Kašnjenje pod opterećenjem meri se HTTP odzivom

Regulatorne metodologije mere odziv ICMP-om ili UDP-om; ovde je HTTP zahtev bez tela ka istoj
meti. Razlog je nameran: eho paket na nekim mrežama putuje drugim redom nego pravi saobraćaj,
a ovaj alat na drugim mestima odbija da gradi nalaze na samom ICMP-u. Posledica je da izmereni
odziv sadrži i ono što sam server doda, zato se i prijavljuje **razlika** u odnosu na mirnu
vezu, gde se to poništi.

Ostaje kao poznata granica, ne kao greška. Ako se ikad bude poredilo sa NetTest brojkama,
ovde je razlog zašto apsolutni brojevi neće biti isti.

## 4. Testabilnost - ostalo je samo ono što se vidi očima

Wi-Fi sloj, kanal za status i prozor su 17.08. dobili šavove i testove (vidi dole). Ostaje
samo ono što se po prirodi ne testira kodom: **kako prozor izgleda.** Raspored, poravnanja i
da li se tekst negde preseca proveravaju se snimkom prozora, kao i do sada. Šta prozor
**odlučuje** je od 17.08. pokriveno testovima - a dijalog „O programu" se u testu i stvarno
otvara, pa se čita šta je na njemu.

Jedna pouka odatle vredi zapisati, jer se ponavlja u WPF-u: greška u rukovaocu klika **ne
prijavljuje se nigde** ako je uhvati opšti rukovalac na nivou aplikacije. Dugme naprosto ne
radi ništa. Ako se ikad pojavi dugme koje „ne reaguje", prvo mesto za pogledati je da li mu
konstrukcija prozora pada - a najlakši način je test koji taj prozor otvori.

## 5. Distribucija

Odluka vlasnika 16.08. ostaje: **bez digitalnog potpisa** (hobi za uži krug). Provera prenosa
ide SHA-256 zbirom uz svaku arhivu, SmartScreen zaobilaznica (`Unblock-File`) mora se reći uz
svaki link. Ako se ikad menja namena, polazna tačka je Azure Artifact Signing (~$10/mes, za
pojedince van USA/Kanade ne postoji).

U `artifacts/` stoje `InternetMonitoring-2.5.0-*.zip` (tekuće izdanje, x64 i arm64, sa
kontrolnim zbirovima) i prethodna izdanja 2.4.0, 2.3.0 i 2.2.0. Duplikati pod starim imenom za
2.3.0 su obrisani.

Objavljivanje oba RID-a jedno za drugim sada prolazi bez ijedne dodatne komande. Do 17.08. nije:
`IEM.App.csproj` je sam za sebe deklarisao `SelfContained`, zbog čega je SDK pri obnavljanju
paketa dodavao `Microsoft.NET.ILLink.Tasks` u zaključani spisak - u verziji koju nosi
instalirani SDK. Zaključani spisak je time postao vezan za jednu zakrpu SDK-a, pa je svaka
mašina sa malo novijim SDK-om (uključujući CI) padala na proveri, na paketu koji ovde niko nije
tražio. Kako se objavljuje odlučuje `build\publish.ps1` preko komandne linije, kao i za servis
i konzolu, pa je iz projekta uklonjeno.

---

## Potvrđeno zatvoreno u 2.7.0 (uveče 17.08.)

**Putanja merenja brzine** · `SpeedPath` je vraćao potvrdu na prvom poklapanju rute, pa je host
sa IPv4 preko Etherneta i IPv6 preko VPN-a prolazio kao ispravan; tri pozivna mesta su uz to
radila `?? true`. Sada četiri stanja, samo jedno nosi prigovor · **provereno uživo**: merenje
prijavljeno na Wi-Fi adapteru (port 144 Mbit/s) izmerilo je 445 Mbit/s jer je saobraćaj išao
kablom - zabeleženo kao `OtherRouteOnly`, nevažeće, uz objašnjenje i izlazni kod 1.

**Wi-Fi radio** · `RadioOn != false` je „nismo uspeli da pročitamo" pretvarao u „radio je
uključen" i prijavljivao kvar pristupne tačke. Sada `== true`.

**Mete bez odgovora** · procenat iz tri različite destinacije sa po jednom probom više se ne
zove gubitak paketa; poruka navodi koje mete ćute i izričito kaže da to nije merenje gubitka.

**Pripisivanje** · nigde više „kod operatera" ni „vaša oprema je isključena kao uzrok"; prikaz
kaže „izolovano iza rutera" i navodi šta nije isključeno (WAN strana rutera, firmver, PPPoE,
NAT). U lancu i bazi ništa nije promenjeno, model pripisivanja podignut na 2.1.

**Tvrdnje o lancu** · „dokazano da paket nije menjan" → „lanac je unutrašnje dosledan", uz
obaveznu ogradu o odsustvu potpisa **uz svaki** takav nalaz, ne stranu dalje.

**Pravni rokovi** · 15/15 dana je bio stari član 113, koji je važio samo dok se ne donese akt
iz čl. 140 - a donet je. Sada registar pravila sa uporištem po pravilu, citatima i datumom
provere; za nov predmet 30 / 8 / 60 / 90 dana · **provereno uživo**: predmet sa prigovorom
podnetim 25.07.2026. citira ZZP 88/2021, a isti predmet sa 10.08.2026. citira ZZP 35/2026 -
isti rok od osam dana, drugi propis · predmet iz starije verzije ne postaje stari režim, nego
se označi kao rekonstruisan.

**Tri invarianta su testovi** · nepoznato ne postaje potvrđeno (uključujući skener izvora koji
traži `?? true` i `RadioOn != false`), stari predmet ne menja značenje, prikaz ne tvrdi više
nego sirovi zapis.

---

## Potvrđeno zatvoreno 17.08. (da se ne traži po istoriji)

**Merenje u oba smera i kašnjenje pod opterećenjem** (stavka 1 od 16.08.) · slanje ide na
Cloudflare `__up`, tri paralelne veze po smeru, isti prozor od deset sekundi · odziv se meri
dok veza miruje i dok je opterećena u svakom smeru, prijavljuje se razlika sa ocenom
(neznatno / primetno / VELIKO) i objašnjenjem šta to znači za pozive i igre · ocena slanja
prema ugovorenoj brzini slanja (`--slanje`), `--bez-slanja` za veze koje se plaćaju po
gigabajtu · sve to ulazi u `MerenjeBrzine.json` i u obe verzije izveštaja.

**Zakazano merenje preuzeo servis** (stavka 3) · zahtev je fajl `merenje.json` pored
`zahtev.json`, preživljava restart, izvršava ga servis bez otvorenog prozora · propušteni
termin stariji od sat vremena se **ne** izvršava naknadno, jer je sat bio poenta zakazivanja ·
servis se ne gasi dok stoji zakazano merenje · komande `zakazi-merenje` / `otkazi-merenje`,
pipe komanda `SPEED` · prozor prosleđuje zakazivanje servisu kad je instaliran, i pri otvaranju
kaže da merenje još stoji zakazano · provereno uživo 17.08. u 02:03: bez ijedne sesije servis
je ostao pokrenut do zakazanog trenutka, sačekao da veza utihne, izmerio (479 / 197 Mbit/s,
+140 ms pod opterećenjem), zapisao nalaz, uklonio zahtev i sam se ugasio.

**Ugovorena brzina u prozoru** (stavka 4) · jedno polje koje prima „100" ili „100/20", kako i
piše u ugovoru · merenje iz prozora se od sada **ocenjuje** istim pravilima kao iz konzole;
ranije je svako merenje iz prozora zapisivano kao neupotrebljivo.

**Šavovi i testovi** (stavka 7) · `WlanLinkInspector` više ne zove `NativeWifi` direktno:
platformski pozivi su u `NativeWifiRadio` iza `IWirelessRadio`, a pravila (zapamćen SSID i
njegov rok, izbor pristupne tačke po BSSID-u, „ne znam" kao sopstveni odgovor) su u
`IEM.Core` i testirana bez radija · protokol kanala za status izdvojen u `IEM.Storage`,
zajednički za servis i prozor, sa testovima za nepoznatu komandu, nečitljiv zahtev, verziju
koja nedostaje i polja koja čitalac ne poznaje.

**Osvežena dokumentacija** (stavka 6) · nov `primer-izvestaja.html/.pdf` iz stvarne sesije, sa
sekcijom merenja · nove slike prozora sa novim imenom i novim poljem · README usklađen.

**Obrisane stare arhive** (stavka 8).

**Tracer ispravka viđena na pravom kraju sesije, pod pravim servisom** (stavka 5) · prvo u
konzolnom režimu u 01:24, pa i u pravom servisu u 10:46 posle reinstalacije: Windows
evidencija (Application) sadrži „Sesija je završena … Integritet: ispravan", „Izveštaj je
napravljen" i „Service stopped successfully" - i **nijedno** upozorenje ni grešku. Ranije se
tu rušilo sa `ObjectDisposedException`. Ograda iz jutrošnje verzije ovog dokumenta je time
uklonjena.

**Servis instaliran i proveren uživo** (stavka 2 od jutros) · prvo 2.4.0 u 10:45, pa 2.5.0 u
11:35, oba pod nalogom `NT AUTHORITY\LocalService`, automatsko pokretanje · u sesiji od 10:46
je i zakazano merenje izvršeno pod servisom (488/196 Mbit/s, +101 ms), nalaz zapisan uz sesiju
u `ProgramData` i ušao u njen izveštaj · u evidenciji nema ni jednog upozorenja o kanalu za
status, čime je i ispravka prava na kanalu potvrđena pod pravim nalogom.

**Sopstveni saobraćaj više ne tereti operatera** (stavka 3 od jutros) · uz svaki uzorak se
beleži koliko je sam računar trošio vezu (`localBps`, format zapisa 3), a prekid nosi i
najviši viđeni protok tokom njega · signal pouzdanosti „Veza nije bila zauzeta vašim
saobraćajem" je dosad **uvek** javljao da je provereno i čisto, a ništa ga nije merilo; sada
javlja tri stvari - provereno i čisto, provereno i nije, i **nije provereno** · izveštaj uz
takav prekid piše „tokom prekida je i sam računar koristio vezu (do X MB/s)" · sopstveno
merenje brzine ostavlja oznaku na disku (`merenje-u-toku.json`, sa rokom, da ne preživi
ubijen proces) koju nadzor čita i taj period upisuje kao „Merenje brzine u toku" umesto kao
pogoršanje · provereno uživo: sesija sa merenjem u sredini ima 22 uzorka `SelfTest` i **nula**
lažnih pogoršanja, gde je jutros bilo dvadesetak sekundi „Visoko kašnjenje".

**Prozor je dobio testove ponašanja** (stavka 5) · zaseban projekat `tests/IEM.App.Tests` sa
lažnim hostom: pokretanje i odbijeno pokretanje, zaustavljanje, čitanje ugovorene brzine,
zakazivanje predato servisu, prepoznavanje zakazanog merenja pri otvaranju prozora, tray
obaveštenja o prekidu i oporavku, i podaci sa ekrana iz snimka stanja · usput: prozor je
ažuriranja bez dispečera **tiho odbacivao**, pa se ni jedan od tih puteva nije mogao izvršiti
bez pokretanja celog WPF-a; sada se izvrše na mestu.

### Nađeno usput, dok je proveravano uživo

- **Kanal za status je radio samo pod nalogom `LocalService`.** Deskriptor je davao pravo
  „napravi novu instancu" samo tom nalogu, pa je servis pokrenut pod bilo kojim drugim
  nalogom (konzola, druga instalacija) dobijao prvu instancu i zatim odbijanje na svaku
  sledeću - upozorenje na svake dve sekunde i kanal sa jednim slušaocem umesto četiri.
  Sada se pravo dodeljuje i nalogu pod kojim proces stvarno radi.
- **Dugme „O programu" nije radilo ništa, bez ijedne prijavljene greške.** `StaticResource` na
  atributima korenskog elementa prozora traži resurs pre nego što se rečnik tog prozora spoji,
  pa je konstrukcija dijaloga padala - a pošto se to dešavalo unutar rukovaoca klika, opšti
  rukovalac je grešku pojeo i dugme je izgledalo mrtvo. Sada je `DynamicResource`, prozor sam
  spaja temu i stoji bez `Application` oko sebe, a test ga otvara i čita.
- **Zaključani spisak paketa bio je vezan za jednu zakrpu SDK-a.** `IEM.App.csproj` je
  deklarisao `SelfContained`, pa je SDK dodavao `Microsoft.NET.ILLink.Tasks` u
  `packages.lock.json` u svojoj verziji - i svaka mašina sa drugom zakrpom SDK-a padala je na
  proveri zaključanih zavisnosti. Prijavio se prvi CI prolaz posle objavljivanja repozitorijuma.
  Kako se objavljuje sada odlučuje samo `build\publish.ps1`, kao i za ostale projekte.
- **README je davao dve putanje koje ne postoje.** Uputstvo je govorilo da se servis objavi u
  `artifacts/service` (instalater tamo ne gleda) i da se pokreće iz
  `Program Files\InternetEvidenceMonitor\InternetEvidenceService.exe`, a instalater ga stavlja
  u podfolder `service\`. Vidi se samo ako se stvarno instalira, pa se videlo danas. Uz to je
  obrisan i zaostali `artifacts/service/` iz starog builda.
- **Izveštaj je navodio verzije modela iz tekućeg builda, a ne iz sesije.** Izveštaj napravljen
  naknadno za stariju sesiju tvrdio je da su zaključci izvedeni današnjim pravilima - što
  obesmišljava jedinu svrhu tih brojeva, da se razlikuje nesaglasnost od promenjenog
  algoritma. Verzije se sada čuvaju uz sesiju (u lancu su bile od početka, u indeksu nisu) i
  izveštaj ispisuje njih.
- **Merenje brzine nije proveravalo kroz koji adapter zaista izlazi.** Čitalo je medij i
  brzinu porta imenovanog adaptera, a prenos je puštalo kuda god operativni sistem odluči, i
  upisivalo „jedna putanja, bez VPN-a" bez ijedne provere. Na mašini sa dokom ili VPN-om to
  daje figuru obeleženu jednom vezom a izmerenu preko druge. Sada se ruta razrešava ka meti
  merenja i, ako ne izlazi kroz taj adapter, merenje nosi nedostatak `PathAmbiguous` i ne može
  stajati uz prigovor. „Ne može se utvrditi" ostaje treći odgovor i ne prelazi u „provereno".
