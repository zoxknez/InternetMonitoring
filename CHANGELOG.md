# Istorija izmena

Verzije rastu po pravilu koje ovde ima smisla: **prva cifra** je oblik dokaza, **druga**
mogućnosti koje korisnik vidi, **treća** ispravke koje ne menjaju ni jedno ni drugo.

Format zapisa u lancu (`schemaVersion`) i verzije pravila (klasifikacija, pripisivanje,
pouzdanost) navode se posebno, jer se po njima kasnije zna kojom logikom je zaključak donet.
Svaki izveštaj ih ispisuje - i to one pod kojima je sesija snimljena, ne one iz verzije koja
izveštaj pravi.

---

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
