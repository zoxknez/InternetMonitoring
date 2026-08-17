# Provera bežičnog sloja na pravom hardveru

Do 16.08. ceo bežični sloj je prošao samo kroz testove i kroz putanju „nema adaptera".
Ovo je spisak šta pokrenuti i šta pokazati kad se ubaci USB Wi-Fi dongl. Svaka stavka ima
očekivan ishod - ako se ishod razlikuje, to je nalaz, zabeleži šta je rađeno i šta je izašlo.

Pretpostavka: dongl je poželjno drugačiji adapter od ugrađenog (ako ga ima), da bi test 5
imao smisla.

## Priprema

```powershell
dotnet run --project src/IEM.Cli -- --wifi
```

Očekivano: lista vidljivih mreža, signal (RSSI) i BSSID pristupne tačke na kojoj si,
kanal, i polje „radio" stanja adaptera. Ako izbaci da nema adaptera dok je dongl priključen,
to je prvi nalaz: provera otvaranja adaptera ne radi za taj uređaj (drajver, stari WLAN API
sloj).

## Šta pokazati, redom

1. **RSSI i BSSID se čitaju.** Pokreni `--wifi` pored računara i dalje od rutera;
   razlika u broju dBm treba da postoji. Očekivano: -40 do -60 blizu, slabije dalje.

2. **Skeniranje vidi susedne mreže.** `--wifi` lista sve SSID-ove u okruženju, ne samo
   naš. Ako lista samo našu mrežu, skeniranje verovatno vraća samo povezanu mrežu, a to bi
   značilo da `WlanScanCache` ne radi na pravom drajveru.

3. **Roaming (prelazak na drugu pristupnu tačku).** Ako ruter ima 2,4 i 5 GHz sa istim
   SSID-om: nadziraj (`-t 10m`) i šetaj se između spratova/soba dok alat radi.
   Očekivano: događaj `WifiRoaming` (promena BSSID-a pod istim SSID-om), bez prijave
   prekida. Ako se roaming prikaže kao prekid - klasifikacija ne prepoznaje stvarni
   BSSID skok na pravom hardveru.

4. **Nestanak SSID-a = kvar rutera, ne operatera.** Najvredniji test. Nadziraj preko
   Wi-Fi dok ruteru (iz njegovih podešavanja) ugasiš bežični radio, pa vrati posle
   2-3 minuta. Očekivano: nalaz `WifiRadioDown` (mreža nestala iz skeniranja), trajanje
   pokriva period gašenja. Ako izađe `InternetDown` ili nulti nalaz - razlikovanje koje
   je poenta alata ne radi uživo.

5. **Dva adaptera, radio samo jednom.** Ako mašina ima i ugrađenu karticu i dongl:
   nadziraj dongl (`-i <ime dongla>`), ugasiti radio ugrađenoj kartici tokom nadzora.
   Očekivano: **nijedan** nalaz `WifiRadioDown` - ispravka od 15.08. vezuje radio stanje
   za nadzirani adapter, a ovo je scenario zbog kog je rađena i nikad nije izvršena.

6. **Injekcija kvarova** (ostalo iz `PREOSTALO.md`, koristi priliku dok je dongl u):

   | Stanje | Kako | Očekivano |
   |---|---|---|
   | `AdapterDown` | fizički izvuci USB dongl tokom nadzora | nalaz lokalnog kvara, ne operatera |
   | `CpeReboot` | restartuj ruter tokom nadzora | nalaz restarta rutera (uptime izašao unazad) |
   | `CpeUpstreamUnreachable` | ruter radi, WAN pada (izvuci kabl od ISP-a iz rutera) | **jedini nalaz koji tereti operatera** |
   | `WifiRadioDown` | vidi test 4 | nalaz kvara rutera |

7. **Merenje brzine preko Wi-Fi (negativna provera).** `--brzina` preko dongla treba da
   izmeri, ali i da kaže da merenje **ne može** za dokazivanje brzine (jer je bežično), i to
   i u konzoli i u izveštaju (`MerenjeBrzine.json` + sekcija sa nedostatkom „nije preko
   kabla").

   Pre ovog testa **izvuci Ethernet kabl**. Dok su oba adaptera gore, saobraćaj ide kablom
   iako je imenovan dongl, i merenje to od 17.08. prepoznaje: prijavljuje da izlazi kroz
   drugi adapter i dodaje nedostatak „putanja nije jednoznačna". Ako se to javi dok je kabl
   izvučen - provera rute gleda pogrešan adapter, i to je nalaz.

8. **Kašnjenje pod opterećenjem preko Wi-Fi.** Isto merenje prijavljuje i koliko odziv
   poraste dok je veza opterećena. Preko Wi-Fi u tom broju je i sam bežični link, pa se
   očekuje veći porast nego preko kabla. Vredi zabeležiti obe brojke sa istog mesta u stanu:
   razlika kabl/Wi-Fi je podatak koji kasnije objašnjava „pozivi zapinju samo bežično".

## Posle testa

- Pokreni `--proveri <folder sesije>` na svakoj sesiji iz testa - lanac mora biti ispravan
  i posle gašenja radio stanja, vađenja dongla i restarta rutera usred rada.
- Napravi izveštaj (`--izvestaj <folder>`) i pogledaj da li se nalazi poklapaju sa onim
  što si stvarno radio (redosled, trajanja, pripisivanje).
- Šta god nije urađeno kako je ovde opisano - upiši u `docs/PREOSTALO.md`, odeljak 2.
