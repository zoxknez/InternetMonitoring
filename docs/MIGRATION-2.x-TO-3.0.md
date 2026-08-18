# Prelaz sa 2.x na 3.0

Šta se dešava sa onim što je već snimljeno.

## Pravilo

**Nijedan zapis iz 2.x ne sme postati nečitljiv, i nijedan ne sme promeniti značenje.**

To nije obećanje nego test: `baseline/v2.7.2/` sadrži pravu sesiju snimljenu verzijom 2.7.2 -
lanac, indeks, izveštaje, nalaz merenja i dnevnik predmeta - a `CharacterizationTests` je čita
pri svakom pokretanju. Ako 3.0 pokvari bilo šta od toga, pada test, ne pregled.

## Šta se čita kako

| Artefakt | Iz 2.5-2.6 | Iz 2.7.x |
|---|---|---|
| `SirovaEvidencija.jsonl` | Verifikuje se, format zapisa 3 | Isto |
| `sesija.db` | Izvedeno; briše se i pregrađuje iz lanca | Isto |
| Verzije modela u sesiji | Ispisuju se **one pod kojima je snimljena** | Isto |
| `MerenjeBrzine.json` | Bez `FindingSchemaVersion` → brojevi se uzimaju, zaključci izvode iznova | Sa verzijom → čita se po vrednosti |
| `DnevnikSlucaja.json` | Bez `SchemaVersion` → datumi „preuzeti", rokovi rekonstruisani i tako označeni | Schema 2, zamrznut pravni kontekst se poštuje |
| `Provera-lanca.txt`, `SHA256SUMS.txt` | Čitaju se; tekst tvrdnji je od 2.7 precizniji | Isto |

## Tri pravila koja to sprovode

**Sirov podatak je večan, izveden nije.** `61.4 Mbit/s` ne zastareva. „Ispunjava uslove za
prigovor" zastareva čim se promeni pravilo po kom je doneto. Zato se izvedeni zaključak iz
starijeg zapisa čuva kao istorijski podatak i **ne usvaja se**, ali se ni ne obrće u suprotan -
i jedno i drugo bi bio gubitak podatka.

**Zaključak bez verzije semantike je zaključak starije verzije.** Odsustvo
`FindingSchemaVersion` je informacija, ne propust. Model 4 to formalizuje kroz
`InterpretationRef {Model, Version, ContentHash}`, jer se dva builda mogu poklopiti brojem a
razlikovati sadržajem.

**Pravni kontekst se ne odmrzava.** Predmet nosi pravila pod kojima je razrešen. Nova činjenica
razrešava samo ono što ranije nije moglo biti razrešeno, i to unutar tih istih pravila; izuzetak
je fallback uporište, koje je privremeno po konstrukciji i ustupa mesto primarnom kada ono
postane dostupno. Promena registra sama po sebi ne menja ništa.

## Šta korisnik vidi

Sesija iz 2.6 otvorena u 3.0 daje isti verdikt i iste brojeve, u novijim formulacijama -
„izolovano iza rutera" umesto „kod operatera", pojas umesto regulatornog naziva. Merenje iz te
sesije neće nositi ocenu valjanosti nego objašnjenje zašto se ranija ocena ne preuzima.

Predmet iz 2.6 dobija rokove po **važećem** režimu, uz jasnu napomenu da su rekonstruisani iz
datuma u fajlu. Ne dobija stari režim samo zato što je fajl star - to bi bio petnaestodnevni rok
koji je prestao da postoji početkom 2025.

## Šta se ne prenosi unazad

Sesija snimljena verzijom 3.0 **neće** biti čitljiva u 2.x kada se pojavi format 4. To je
prihvaćeno: nadole se ne garantuje ništa, nagore se garantuje sve.

Potpisan manifest i vremenski žig postoje samo za sesije snimljene 3.0. Starije se time ne
obezvređuju - njihov lanac se i dalje verifikuje - ali ne dobijaju poreklo naknadno. Potpisati
2026. paket 2027. ključem značilo bi tvrditi nešto što se ne zna.

## Za onoga ko piše 3.0 kod

Pre izmene bilo čega što dodiruje format:

1. `dotnet test --filter "FullyQualifiedName~Characterization"` mora proći **pre** izmene;
2. posle izmene mora proći opet, bez diranja `baseline/`;
3. ako mora da se promeni, `baseline/v3.0.0/` se dodaje **pored**, a stari se ne dira.

`baseline/` se ne regeneriše rutinski. Piše ga `BaselineSnapshotWriter` samo kada je postavljeno
`IEM_WRITE_BASELINE=1`, i njegov izlaz se pregleda kao svaki drugi komit - snapshot koji se sam
prepisuje pri svakom pokretanju ne može uhvatiti ono zbog čega postoji.
