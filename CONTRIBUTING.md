# Kako doprineti

Hvala što gledate ovaj kod. Projekat je mali i ima jedno pravilo iz kog izlaze skoro sva
ostala:

> **Alat sme da tvrdi samo ono što je izmerio.**

Sve u ovom repozitorijumu služi tome da izveštaj koji korisnik pošalje operateru preživi
čitanje nekog ko traži grešku u njemu. Ako izmena čini alat samouverenijim nego što merenja
dozvoljavaju, biće odbijena čak i ako je tehnički lepša.

## Šta se traži za pokretanje

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows. Servis, prava na imenovanom kanalu i Native Wi-Fi sloj nemaju parnjaka drugde.

```bash
dotnet build
dotnet test
```

Testovi ne traže ni mrežu ni bežičnu karticu. Ako neki test počne da ih traži, to je greška u
testu.

## Pravila koja se ne pregovaraju

**„Nisam mogao da proverim" nije „proverio sam i u redu je."** Svako čitanje koje može da
izostane je `null`, ne `false` i ne nula. Ovo je najčešći način da se u dokaz uvuče tvrdnja
koju niko nije izmerio - i najveći deo pređašnjih ispravki u ovom projektu su upravo takve
greške.

**Trajanja se ne računaju iz zidnog sata.** Za svako proteklo vreme koristi se monotoni
brojač (`IClock.MonotonicTicks`). NTP korekcija ili ručna izmena sata ne smeju da promene
zabeleženo trajanje prekida.

**Operater se tereti samo kad se sve bliže isključi.** Nova stanja i nova pripisivanja idu
kroz `NetworkState` i `FaultDomain`, sa testom za slučaj koji dokazuju i za slučaj koji im
najviše sliči a nije to.

**Sirova evidencija je izvor istine.** SQLite indeks je izvedena kopija i sme da se obriše u
svakom trenutku. Ako izmena zahteva podatak koji postoji samo u bazi, prvo ide u lanac.

**Format zapisa ima verziju.** Menja se polje u lancu - `EvidenceModelVersion.SchemaVersion`
raste, i staro se i dalje čita. Isto za pravila klasifikacije, pripisivanja i pouzdanosti:
izveštaj mora moći da kaže kojom logikom je zaključak donet.

## Stil

Kod i komentari su na **engleskom**. Sve što korisnik vidi - poruke, izveštaji, imena fajlova
u paketu - na **srpskom** (latinica, `sr-Latn-RS`).

Komentar objašnjava **zašto**, ne šta. Najkorisniji komentari u ovom repozitorijumu opisuju
grešku koja je jednom napravljena, pa se ne ponavlja - ako ispravljate takvu grešku, upišite
je tu, u punoj rečenici.

Obična crta `-` u tekstu, ne duga crta. Bez emodžija u kodu i porukama.

## Test uz svaku izmenu ponašanja

Ime testa je tvrdnja, ne opis metode:
`One_busy_sample_stops_it_being_ruled_out`, ne `TestObserve2`. Za ispravku greške ide test
koji pada pre ispravke.

Gradnja je sa `TreatWarningsAsErrors`, pa upozorenje = pad gradnje. To je namerno.

## Pull request

1. Grana od `main`.
2. `dotnet build` i `dotnet test` prolaze lokalno.
3. U opisu: šta je bilo, šta je sada, i **šta bi se izgubilo da izmene nema**.
4. Ako izmena menja ono što korisnik vidi, pomenite i da li izveštaj i prozor sada govore istu
   stvar - ne smeju da se razlikuju.

## Šta je nedovršeno

`docs/PREOSTALO.md` je uvek tačan popis nezavršenog, sa razlogom zašto nije urađeno. Ako uzmete
nešto odatle, tu i upišite šta je od toga urađeno.
