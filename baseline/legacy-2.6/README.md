# Artefakti verzije 2.6

Fajlovi koje je pisala 2.6, sačuvani onakvi kakvi jesu.

Nisu primeri niti dokumentacija - testovi ih čitaju. Postoje zato što se dve greške u 2.7.0
nisu videle ni u jednom testu koji gradi podatke u kodu: obe su bile u tome kako **već
zapisan** fajl prolazi kroz nov prikaz.

| Fajl | Šta u njemu vredi |
|---|---|
| `MerenjeBrzine.json` | Nema `RouteState` ni `FindingSchemaVersion`. Nosi `ValidForComplaint: true` (ocena po pravilu koje je neproverenu putanju računalo kao proverenu) i `BandLabel` sa regulatornim izrazom koji je 2.7 ukinula. |
| `DnevnikSlucaja.json` | Nema `SchemaVersion` ni zamrznut pravni kontekst; `RegulatorFiledDate` stoji pored predmeta, a ne na njemu. |

Ne menjati. Ako zatreba nov slučaj, dodaje se nov fajl - ovi opisuju šta je stvarno bilo na
disku i time proveravaju da nova verzija ne menja značenje starog zapisa.
