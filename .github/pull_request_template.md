# Šta ovo menja

<!-- Šta je bilo, šta je sada, i šta bi se izgubilo da izmene nema. -->

## Provereno

- [ ] `dotnet build` prolazi bez upozorenja (gradnja ih tretira kao greške)
- [ ] `dotnet test` prolazi
- [ ] Ako menja ponašanje: ima test koji bi pao pre ove izmene
- [ ] Ako menja ono što korisnik vidi: prozor, konzola i izveštaj govore istu stvar

## Ako dodaje ili menja nalaz

- [ ] „Nisam mogao da proverim" je i dalje odvojeno od „proverio sam i u redu je"
- [ ] Operater se tereti samo kada je sve bliže isključeno
- [ ] Trajanja idu iz monotonog brojača, ne iz zidnog sata

## Ako menja format zapisa

- [ ] `EvidenceModelVersion` je podignut i stari zapisi se i dalje čitaju
- [ ] `docs/PREOSTALO.md` je dopunjen ako je nešto ostalo nedovršeno
