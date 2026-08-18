# baseline/

Zamrznuti artefakti prethodnih verzija. **Testovi ih čitaju** - nisu primeri ni dokumentacija.

| Folder | Šta sadrži |
|---|---|
| `legacy-2.6/` | Nalaz merenja i dnevnik predmeta u obliku koji je pisala 2.6, bez verzija semantike |
| `v2.7.2/sesija/` | Cela sesija snimljena verzijom 2.7.2: lanac, indeks, izveštaji, nalaz, predmet, kontrolni zbirovi |

## Zašto postoje

Dve greške u 2.7.0 nisu se videle ni u jednom od 545 testova, jer svi grade podatke u kodu. Obe
su bile u tome kako **već zapisan fajl** prolazi kroz nov prikaz. Artefakt na disku je jedina
vrsta ulaza koja to hvata.

## Pravila

**Ne menjati.** Ako novoj verziji treba drugačiji slučaj, dodaje se nov fajl ili nov folder;
postojeći ostaju kakvi jesu, jer opisuju šta je stvarno bilo na disku.

**Fajlovi su obavezni.** Test koji ih čita **pada** kad fajla nema - nikada ga ne preskače.
Provera koja tiho ne uradi ništa prijavljuje uspeh za posao koji nije obavljen. CI uz to
odbija fajl iz `baseline/` koji postoji lokalno a nije u gitu; tačno to se desilo u 2.7.1,
gde ih je pojelo `.gitignore` pravilo pisano da zaštiti prave sesije.

**Nema privatnih podataka.** `v2.7.2/sesija/` je prava evidencija - pravi rekorder, pravi lanac,
pravi izveštaji - ali od sintetičkih uzoraka: mašina je `TEST-PC`, adrese su dokumentacione.
Piše je `BaselineSnapshotWriter`, i to samo kada je postavljeno `IEM_WRITE_BASELINE=1`:

```bash
dotnet test tests/IEM.Core.Tests -e IEM_WRITE_BASELINE=1 --filter "FullyQualifiedName~BaselineSnapshotWriter"
```

Izlaz se pregleda kao svaki drugi komit. Snapshot koji se sam prepisuje pri svakom pokretanju
ne može uhvatiti ono zbog čega postoji.
