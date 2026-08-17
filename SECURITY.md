# Bezbednost i prijava propusta

## Šta ovaj program radi sa podacima

Vredi znati pre nego što se traži propust: **program ne šalje nikakve podatke nigde.** Nema
naloga, nema servera, nema telemetrije. Sve što snimi ostaje u folderu sesije na disku, a
korisnik sam odlučuje kome će to poslati.

Ka mreži izlaze samo merenja: ping i TCP ka javnim metama (Cloudflare, Google, Quad9), DNS
upiti, HTTP zahtev, i - kada se merenje brzine pokrene - prenos ka Cloudflare-ovom javnom
servisu za merenje. Ništa od toga ne nosi podatke o korisniku.

Servis radi pod nalogom `NT AUTHORITY\LocalService`, ne `LocalSystem`. Imenovani kanal za
status je dostupan interaktivnim korisnicima i nalogu pod kojim servis radi, i **samo za
čitanje stanja** - preko njega se nadzor ne može zaustaviti.

## Šta je ozbiljan propust

- Podatak iz sesije koji izlazi sa mašine, na bilo koji način.
- Način da se preko imenovanog kanala nešto pokrene, zaustavi ili upiše.
- Način da se sirova evidencija izmeni tako da provera lanca to ne prijavi. To je srž
  projekta: ako se lanac može podvaliti, dokaz ništa ne vredi.
- Podizanje privilegija preko instalacione skripte ili prava na folderu sesija.
- Bilo šta što tera alat da u izveštaj upiše tvrdnju koju merenja ne podržavaju - naročito
  prekid pripisan operateru koji se nije dogodio. To je isto tako propust, jer je izveštaj
  namenjen postupku.

## Kako prijaviti

Koristite **Security → Report a vulnerability** na GitHubu (privatna prijava, vidi je samo
vlasnik repozitorijuma). Ako to nije dostupno, otvorite issue **bez detalja o iskorišćavanju**
i recite da čekate privatni kanal.

Molim vas da ne otvarate javni issue sa opisom kako se propust iskorišćava, dok ne bude
ispravljen.

Korisno u prijavi: verzija (`iem --pomoc` je ispisuje), šta je očekivano, šta se dogodilo, i
ako je moguće folder sesije na kome se to vidi - **bez** fajlova koje ne želite da delite,
jer evidencija sadrži imena vaših mreža i adrese vaše opreme.

## Odgovor

Ovo je hobi projekat jednog čoveka, bez tima koji dežura. Realno: odgovor u nekoliko dana,
ispravka kada bude jasno šta je ispravno uraditi. Ako je propust ozbiljan a ispravka nije
očigledna, u `docs/PREOSTALO.md` će stajati šta je poznato i šta korisnik u međuvremenu treba
da izbegava.

## Verzije

Održava se poslednja objavljena verzija. Starije se ne popravljaju - arhive stoje u
`artifacts/` uz SHA-256 zbirove, pa se svaka može proveriti i uporediti.
