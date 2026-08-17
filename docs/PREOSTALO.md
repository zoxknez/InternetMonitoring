# Šta je preostalo

Stanje na dan **17.08.2026. (podne)**, verzija **2.5.0**, ime aplikacije **Internet Monitoring**,
**492 testa prolaze** (472 u jezgru, 20 u prozoru), nula upozorenja pri gradnji, zaključane
zavisnosti prolaze, servis instaliran i proveren na tekućoj verziji.

Ovaj dokument je popis nezavršenog, a ne plan. Pisan je da bi se sutra znalo šta nije urađeno
i **zašto**, jer je to podatak koji se najbrže gubi. Sve što je ranije bilo ovde, a urađeno je
17.08., ukratko je potvrđeno na kraju dokumenta.

Redosled je po važnosti.

---

## 1. Fizički Wi-Fi testovi - i dalje jedini deo koji zahteva ljudske ruke

Nepromenjeno u odnosu na 16.08., jer se ništa od ovoga ne može uraditi iz koda: traži se da
neko fizički dira ruter i dongl. Spisak je u `docs/TEST-WIFI.md`, a ostaju:

| Test | Radnja | Očekivani nalaz |
|---|---|---|
| 3 - roaming | šetanje tokom nadzora (2,4/5 GHz isti SSID) | `WifiRoaming`, bez prekida |
| 4 - **najvažniji** | ugasiti bežični radio na ruteru 2-3 min | `WifiRadioDown` (SSID nestao iz skeniranja) |
| 6a | izvuci dongl tokom nadzora | `AdapterDown` (lokalni kvar) |
| 6b | restartovati ruter tokom nadzora | `CpeReboot` (uptime unazad) |
| 6c | izvuci WAN kabl sa rutera | `CpeUpstreamUnreachable` - jedini nalaz koji tereti operatera |

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
**odlučuje** je od 17.08. pokriveno testovima.

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
