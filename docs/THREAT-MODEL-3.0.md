# Model pretnji 3.0

## Prvo, i pre svega ostalog: šta potpis ne dokazuje

3.0 uvodi potpisan manifest sesije i vremenski žig treće strane. To su najvidljivije funkcije
izdanja i najlakše ih je pogrešno opisati, pa ovaj dokument počinje granicom, a ne mogućnošću.

**Potpis dokazuje:**

- da paket odgovara sadržaju koji je potpisan;
- da nije neprimetno menjan posle potpisivanja;
- da je potpisan ključem **te instalacije** programa.

**Potpis ne dokazuje:**

- da korisnik nije fabrikovao ulaz pre potpisivanja;
- da Windows na kom je nastao nije bio kompromitovan;
- da je mrežni scenario bio prirodan a ne napravljen;
- da je incident nastao kod operatera.

Ključ stoji na mašini koju kontroliše sam korisnik. Iz toga sledi da operater i dalje sme da
kaže „mogli ste to snimiti u laboratoriji", i to je tačno. Ono što potpis oduzima jeste drugi
prigovor - „paket je izmenjen posle snimanja" - i to je jedini prigovor koji zatvara.

**Vremenski žig dokazuje** da je određeni otisak **postojao pre** određenog trenutka, i to
potvrđuje treća strana. Ne dokazuje da se mrežni događaj desio tada, niti da je sadržaj istinit.

Zato dve invarijante, sa kodom ili bez njega:

```
SIGNATURE_PROVES_INTEGRITY_NOT_TRUTH
TRUSTED_TIMESTAMP_PROVES_EXISTENCE_NOT_EVENT_TIME
```

2.6 je pogrešila upravo ovako, samo skromnije: tvrdila je da lanac otisaka „dokazuje da paket
nije menjan", dok su kontrolni zbirovi stajali u istom folderu u koji se piše. Ista greška na
potpisu bila bi teža, jer zvuči ozbiljnije.

---

## Ko su protivnici

**Nepažnja.** Korisnik prepiše fajl, alat za sinhronizaciju promeni sadržaj, kopiranje na
drugi disk pokvari red. Lanac ovo hvata od 2.0 i to je najčešći stvarni slučaj.

**Operater koji traži procesnu grešku.** Ne napada zapis nego tvrdnju: „merenje je preko
Wi-Fi-a", „to je bio vaš saobraćaj", „ne dokazujete da je kod nas". Ovo se ne brani
kriptografijom nego time da program ne tvrdi više nego što je izmerio - čemu je 2.7 posvećena
cela.

**Korisnik koji falsifikuje.** Ima pun pristup mašini, ključu i folderu. Protiv njega lokalna
kriptografija ne radi ništa, i ne treba se praviti da radi. Jedino što pomaže je nezavisna
treća strana: vremenski žig, i eventualno merenje kod regulatora.

**Neko drugi sa pristupa mašini.** Malver ili druga osoba na istom nalogu. Delimično se brani
time što servisni ključ nije izvozan i što `Raw/` piše samo servis - pa presnimavanje zahteva
prava koja običan korisnički proces nema.

---

## Šta 3.0 menja

| Mera | Šta zatvara | Šta ne zatvara |
|---|---|---|
| Nepromenljiv ključ u CNG-u, po mogućstvu uz TPM | Presnimavanje paketa bez traga | Fabrikovanje pre potpisa |
| Potpisuje servis, ne prozor ni konzola | Potpis iz korisničkog procesa | Isto |
| `Raw/` u ACL-u: servis piše, korisnik čita | Tiha izmena sirove evidencije | Korisnika sa administratorskim pravima |
| RFC 3161 žig | „Napravljeno naknadno, posle spora" | „Napravljeno ranije, namerno" |
| Zaseban verifikator | Proveru bez originalne instalacije | Poverenje u sam sadržaj |

---

## Verifikacija bez mreže

Paket mora biti proverljiv na drugoj mašini, bez instalirane aplikacije i **bez interneta**.
To znači da sve što je potrebno za proveru putuje u paketu:

```
Evidence/
  manifest.json
  manifest.sig
  signer/
    public-key
    certificate-chain/
  timestamp/
    timestamp.tsr
    tsa-certificates/
    revocation/
```

Jedna razlika koju treba držati na oku: **sertifikat u paketu ne postaje pouzdan time što je u
paketu.** Koren poverenja dolazi iz politike verifikatora ili iz poznatog sistemskog skladišta,
nikada iz samog materijala koji se proverava. Verifikator zato ima tri ishoda, ne dva:

- potpis i žig proveravaju se i lanac vodi do korena kome verifikator veruje;
- potpis i žig su matematički ispravni, ali koren nije poznat - **to nije „valid"**;
- nešto ne valja.

---

## Šta ostaje van dometa i u 3.0

- Merenje sa jedne tačke ne može reći gde je u mreži operatera nastao prekid. Ne pokušava.
- Program ne može znati da li je korisnikov ruter ispravan; WAN strana rutera ostaje
  neisključena, i to piše uz svaki nalaz.
- Vreme događaja počiva na sistemskom satu, uz nezavisni monotoni brojač za trajanja. Žig
  vezuje samo trenutak potpisivanja.
- Potpisivanje same aplikacije (Authenticode) menja to kome korisnik veruje pri instalaciji, ne
  koliko vredi dokaz koji ona proizvede.

---

## 3.1 Linux Threat Model — Dopune i granice (3.1-0 Draft)

U 3.1 ciklusu Linux uvodi nove platform-specifične vektore napada i granice poverenja koje moraju biti striktno definisane:

### 1. Unix Domain Socket (UDS) & Traversal kontrola
- **Pretnja:** Neautorizovani lokalni korisnik ili proces pokušava pristup IPC kontrolnom kanalu (`/run/internet-evidence-monitor/control.sock`).
- **Odbrana:** Dvoslojna filesystem kontrola. Parent direktorijum je `0750` `iem:iem-users`, a sam socket je `0660` `iem:iem-users`. Servisni nalog koristi `SupplementaryGroups=iem-users` da bezbedno postavi grupu bez `CAP_CHOWN`. Samo članovi grupe `iem-users` imaju traversal (`+x`) pravo do socket čvora.

### 2. Stale Socket Hijacking & Symlink napadi
- **Pretnja:** Zlonamerni lokalni proces postavlja symlink, FIFO ili tuđi socket na putanji `/run/internet-evidence-monitor/control.sock` kako bi preusmerio servis ili izazvao brisanje tuđih fajlova.
- **Odbrana:** Stroga procedura bezbednog kreiranja po §11.3: isključivo `lstat` / `O_NOFOLLOW`. Dozvoljen je `unlink` samo nad potvrđenim `S_IFSOCK` čvorom čiji je owner `iem`, nakon što `connect()` potvrdi `ECONNREFUSED` / `ENOTCONN`. Nikada se ne vrši `unlink` naslepo, nikada nad symlinkom i nikada van očekivanog runtime direktorijuma.

### 3. Identity Spoofing preko IPC-a
- **Pretnja:** Klijentska aplikacija ili napadač šalje lažirani UID, SID ili administrativnu rolu unutar IPC JSON payload-a.
- **Odbrana:** Sav klijentski identitetski payload se potpuno ignoriše. Autoritativni identitet se dobija isključivo iz kernela putem `SO_PEERCRED` (uid, gid, pid) i `SO_PEERGROUPS` (stvarne dopunske grupe konektovanog peer procesa). Ako se grupe ne mogu pouzdano utvrditi, autorizacija za uloge zavisne od grupa ide u fail-closed.

### 4. Storage Izolacija i Export Granica
- **Pretnja:** Malver ili kompromitovani GUI klijent pokušava direktnu modifikaciju `/var/lib/internet-evidence-monitor` ili servis kompromituje korisnički home folder.
- **Odbrana:** Kanonski store je `0700` `iem:iem` — GUI proces u Installed modu nema filesystem ACL pristup niti pravo čitanja/pisanja. `CreateExport` funkcioniše isključivo tako što servis kreira verifikovani paket u staging zoni, autorizovani klijent ga preuzima preko IPC strima, a sam klijentski proces upisuje fajl u korisnički direktorijum. Time `ProtectHome=yes` na servisu ostaje 100% netaknut.
- **Portable mod:** Koristi striktno validiran `XDG_STATE_HOME` (samo neprazne apsolutne putanje počevši sa `/`), gde je dokazni materijal jasno označen kao `UserOwned` i `SoftwareProtected`.

