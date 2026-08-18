# Invarijante

Pravila koja ovaj program ne sme da prekrši, i mesto gde svako od njih ima test.

Nisu principi nego **ograničenja**. Razlika je u tome što se principi pamte a ograničenja
proveravaju: svako od ovih pravila je bar jednom već bilo prekršeno u kodu koji je prošao sve
testove i bio objavljen. Zato uz svako stoji i kako je otkriveno.

Redosled je po tome koliko štete pravi kršenje.

---

## 1. UNKNOWN_NEVER_BECOMES_CONFIRMED

**„Nisam mogao da proverim" nikada ne postaje „proverio sam i u redu je."**

Nijedna vrednost koja znači „nije utvrđeno" ne sme se svesti na potvrdu podrazumevanom
vrednošću, `??`, `!= false`, `GetValueOrDefault(true)` ili bilo kojim drugim tihim izborom.
Gde stvarno postoje tri stanja, model ih ima sva tri, sa nepoznatim kao podrazumevanim.

**Kako je otkriveno:** tri pozivna mesta su radila `?? true` nad proverom putanje merenja, a
klasifikator `RadioOn != false` — nepročitan radio je prolazio kao uključen i prijavljivao kvar
pristupne tačke. Sve to je prošlo 478 testova.

**Gde se proverava:** `SourceInvariantTests.Unknown_never_becomes_confirmed` skenira ceo `src/`
za zabranjene obrasce, uz jedan dokumentovan izuzetak (identitet fold-a). Uz njega
`SpeedPathTests` i `StateClassifierTests` po pozivnom mestu.

### 1a. UNKNOWN_NEVER_BECOMES_ZERO

Isto pravilo za brojeve. Pločica „Mete bez odgovora" pisala je `0 %` pre prvog uzorka — umirujući
odgovor na pitanje koje još niko nije postavio. Nemereno se prikazuje kao nemereno.

---

## 2. PRESENTATION_NEVER_CLAIMS_MORE_THAN_RAW_EVIDENCE

**Nijedan tekst ne tvrdi više nego što odgovarajući zapis sadrži.**

Ni na ekranu, ni u izveštaju, ni u CSV-u, ni u pismu. Ovo važi u oba smera: prikaz ne sme reći
ni da je nešto dokazano kad nije, ni da je sve u redu kad se to ne zna.

**Kako je otkriveno:** model pripisivanja je od 2.0 govorio da se sa korisnikovog računara ne
može utvrditi čija je mreža u kvaru, dok je 31 fajl i dalje pisao „Prekida kod operatera".
Kasnije, u istoj kategoriji: „Veza je bila stabilna" posle dva minuta nadzora, i konflikt koji
je zapisan u dnevnik a nigde prikazan — ekran je tvrdio da je rok utvrđen dok je zapis znao da
je osporen.

**Gde se proverava:** `AttributionWordingTests` pušta sve `NetworkState` × `FaultDomain` kroz
sve prezentacione površine; `ShortCleanSessionTests`; `CharacterizationTests` nad zamrznutim
izveštajem.

---

## 3. HISTORICAL_CASE_NEVER_CHANGES_MEANING

**Predmet razrešen pod jednim skupom pravila daje iste rokove i posle izmene registra.**

Zapis o sporu je jedina vrsta dokumenta koji ne sme tiho da promeni značenje. Registar pravnih
pravila je zato nepromenljiv — pravna promena pravi novu verziju sa novim hešom — a predmet nosi
zamrznut kontekst, ne samo identifikator.

**Kako je otkriveno:** invariant je držao pri čitanju a padao pri pisanju. `Save` je pri svakom
upisu ponovo razrešavao ceo predmet, pa je beleženje odgovora operatera pod novijim registrom
preračunavalo i rokove razrešene mesecima ranije.

**Gde se proverava:** `FrozenLegalContextTests`, četiri imenovana scenarija.

### 3a. FINAL_RESOLUTION_NEVER_CHANGES_SILENTLY

Razrešenje iz **primarnog** uporišta je konačno. Ako se datum koji je već korišćen naknadno
promeni, rok ostaje takav kakav je, a neslaganje se prijavljuje.

### 3b. FALLBACK_RESOLUTION_IS_PROVISIONAL

Razrešenje iz **fallback** uporišta je privremeno po konstrukciji: događaj od kog rok zaista
teče još se nije desio.

### 3c. PRIMARY_ANCHOR_SUPERSEDES_FALLBACK_WITHIN_FROZEN_RULESET

Kada primarno uporište postane dostupno, isti **zamrznuti** ruleset daje konačan rok i on
postaje aktuelan; privremeni se čuva uz razlog. To nije konflikt nego zakon kako je napisan.

**Kako je otkriveno:** 2.7.1 je zatvorila 3 pregrubo i dolazak stvarnog odgovora prijavljivala
kao konflikt, pa je predmet kao aktuelan držao datum za koji je već znao da je slabije zasnovan.

---

## 4. LEGACY_DERIVED_CONCLUSION_IS_NEVER_TRUSTED_AS_RAW_EVIDENCE

**Ono što je ranija verzija izmerila jeste dokaz; ono što je zaključila nije.**

Zapisan zaključak ima rok trajanja. Ako se pravilo po kom je donet promeni, on postaje
istorijski podatak — čuva se, ali se ne usvaja i ne obrće.

**Kako je otkriveno:** nalaz merenja iz 2.6 nosi `ValidForComplaint: true`, ocenu po pravilu
koje je neproverenu putanju računalo kao proverenu. Izveštaj 2.7.0 je u istoj tabeli pisao
„putanja merenja nije proverena" i, tri reda niže, „ispunjava uslove za korišćenje uz prigovor".

**Gde se proverava:** `LegacyArtefactTests`, nad stvarnim fajlovima u `baseline/legacy-2.6/`.

---

## 5. DERIVED_CLAIM_CARRIES_ITS_INTERPRETATION_VERSION

**Svaka izvedena tvrdnja koja se čuva izvan procesa koji ju je izračunao nosi identitet
semantike kojom je izvedena.**

Odnosi se na sve što nije direktno opažanje: pojas brzine, klasifikaciju stanja, pripisivanje,
ocenu valjanosti, pravno razrešenje, zaključke izveštaja — i, u 3.0, na potpisani manifest,
ocenu kvaliteta dokaza i redigovani paket.

Broj verzije nije dovoljan: dva builda mogu nositi isti broj a različit sadržaj. Identitet je
zato `{Model, Version, ContentHash}` — kako `LegalRulesetRef` već radi.

**Kako je otkriveno:** posledica nalaza 4. Da je nalaz merenja od početka nosio verziju,
problem se ne bi ni desio.

**Gde se proverava:** `CharacterizationTests`, `LegacyArtefactTests`,
`LegalTransitionTests.A_published_ruleset_is_never_edited_in_place`.

---

## 6. RAW_EVIDENCE_IS_APPEND_ONLY

**Sirova evidencija se samo dopisuje.** Nijedan zapis se ne menja ni briše posle upisa; lanac
otisaka to i čini proverljivim.

**Gde se proverava:** `HashChainTests`, `EvidenceRecorderTests`, i characterization test koji
verifikuje zamrznuti lanac iz prethodne verzije.

---

## 7. DERIVED_OUTPUT_NEVER_MUTATES_SOURCE_EVIDENCE

**Pravljenje izveštaja, izvoza, prigovora ili redigovane kopije ne dira izvor.** Indeks je
izvedeni podatak i može se obrisati i pregraditi iz lanca; izveštaj se pravi iznova bez ijedne
izmene u `Raw/`.

**Gde se proverava:** `CharacterizationTests.The_index_rebuilds_from_the_chain_to_the_same_figures`.
U 3.0 dobija i ACL koji to sprovodi, ne samo poštuje.

---

## 8. BASELINE_FIXTURES_ARE_RELEASE_ARTIFACTS

**Fixture na disku, fixture u repozitorijumu i fixture u tagu su tri različite stvari.**

Testovi koji ih čitaju **padaju** kad fajla nema — nikada ga ne preskaču. Test koji ćuti kad mu
nedostaje ulaz gori je od nepostojećeg testa, jer prijavljuje uspeh za posao koji nije uradio.

**Kako je otkriveno:** 2.7.1 je objavljena sa `baseline/` isključenim pravilom iz `.gitignore`
koje je pisano da zaštiti prave sesije. Lokalno je sve prolazilo, na CI-ju su isti testovi pali.

**Gde se proverava:** `BaselineSnapshot.Require` baca sa objašnjenjem;
`CharacterizationTests.Every_artefact_the_snapshot_promises_is_actually_there`; i korak u CI-ju
koji proverava da je svaki fajl iz `baseline/` praćen u gitu.

---

## Za 3.0: dve koje još nemaju kod

## 9. SIGNATURE_PROVES_INTEGRITY_NOT_TRUTH

Potpis dokazuje da paket odgovara potpisanom sadržaju, da nije neprimetno menjan posle
potpisivanja, i da je potpisan ključem te instalacije. **Ne dokazuje** da ulaz nije fabrikovan
pre potpisivanja, da host nije bio kompromitovan, niti da je incident nastao kod operatera.

## 10. TRUSTED_TIMESTAMP_PROVES_EXISTENCE_NOT_EVENT_TIME

Vremenski žig treće strane dokazuje da je određeni podatak **postojao pre** određenog trenutka.
Ne dokazuje da se mrežni događaj desio tada, niti da je sadržaj istinit.

Obe idu u `THREAT-MODEL-3.0.md` kao prvi pasus, ne kao dodatak — jer je 2.6 tačno tako i
pogrešila, tvrdnjom koja je zvučala jače nego što jeste.
