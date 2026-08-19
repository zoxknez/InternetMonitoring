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
klasifikator `RadioOn != false` - nepročitan radio je prolazio kao uključen i prijavljivao kvar
pristupne tačke. Sve to je prošlo 478 testova.

**Gde se proverava:** `SourceInvariantTests.Unknown_never_becomes_confirmed` skenira ceo `src/`
za zabranjene obrasce, uz jedan dokumentovan izuzetak (identitet fold-a). Uz njega
`SpeedPathTests` i `StateClassifierTests` po pozivnom mestu.

### 1a. UNKNOWN_NEVER_BECOMES_ZERO

Isto pravilo za brojeve. Pločica „Mete bez odgovora" pisala je `0 %` pre prvog uzorka - umirujući
odgovor na pitanje koje još niko nije postavio. Nemereno se prikazuje kao nemereno.

---

## 2. PRESENTATION_NEVER_CLAIMS_MORE_THAN_RAW_EVIDENCE

**Nijedan tekst ne tvrdi više nego što odgovarajući zapis sadrži.**

Ni na ekranu, ni u izveštaju, ni u CSV-u, ni u pismu. Ovo važi u oba smera: prikaz ne sme reći
ni da je nešto dokazano kad nije, ni da je sve u redu kad se to ne zna.

**Kako je otkriveno:** model pripisivanja je od 2.0 govorio da se sa korisnikovog računara ne
može utvrditi čija je mreža u kvaru, dok je 31 fajl i dalje pisao „Prekida kod operatera".
Kasnije, u istoj kategoriji: „Veza je bila stabilna" posle dva minuta nadzora, i konflikt koji
je zapisan u dnevnik a nigde prikazan - ekran je tvrdio da je rok utvrđen dok je zapis znao da
je osporen.

**Gde se proverava:** `AttributionWordingTests` pušta sve `NetworkState` × `FaultDomain` kroz
sve prezentacione površine; `ShortCleanSessionTests`; `CharacterizationTests` nad zamrznutim
izveštajem.

---

## 3. HISTORICAL_CASE_NEVER_CHANGES_MEANING

**Predmet razrešen pod jednim skupom pravila daje iste rokove i posle izmene registra.**

Zapis o sporu je jedina vrsta dokumenta koji ne sme tiho da promeni značenje. Registar pravnih
pravila je zato nepromenljiv - pravna promena pravi novu verziju sa novim hešom - a predmet nosi
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
istorijski podatak - čuva se, ali se ne usvaja i ne obrće.

**Kako je otkriveno:** nalaz merenja iz 2.6 nosi `ValidForComplaint: true`, ocenu po pravilu
koje je neproverenu putanju računalo kao proverenu. Izveštaj 2.7.0 je u istoj tabeli pisao
„putanja merenja nije proverena" i, tri reda niže, „ispunjava uslove za korišćenje uz prigovor".

**Gde se proverava:** `LegacyArtefactTests`, nad stvarnim fajlovima u `baseline/legacy-2.6/`.

---

## 5. DERIVED_CLAIM_CARRIES_ITS_INTERPRETATION_VERSION

**Svaka izvedena tvrdnja koja se čuva izvan procesa koji ju je izračunao nosi identitet
semantike kojom je izvedena.**

Odnosi se na sve što nije direktno opažanje: pojas brzine, klasifikaciju stanja, pripisivanje,
ocenu valjanosti, pravno razrešenje, zaključke izveštaja - i, u 3.0, na potpisani manifest,
ocenu kvaliteta dokaza i redigovani paket.

Broj verzije nije dovoljan: dva builda mogu nositi isti broj a različit sadržaj. Identitet je
zato `{Model, Version, ContentHash}` - kako `LegalRulesetRef` već radi.

**Kako je otkriveno:** posledica nalaza 4. Da je nalaz merenja od početka nosio verziju,
problem se ne bi ni desio.

**Gde se proverava:** `CharacterizationTests`, `LegacyArtefactTests`,
`LegalTransitionTests.A_published_ruleset_is_never_edited_in_place`.

### 5a. INTERPRETATION_IS_BOUND_TO_CLAIM_NOT_SESSION

Identitet semantike pripada **tvrdnji**, ne sesiji. Sesija sme da nosi katalog tumačenja i da
svaka tvrdnja pokazuje na unos u njemu - to je fizička deduplikacija, ne semantička.

Pretpostavka „jedna sesija = jedna verzija tumačenja" ne stoji: dvodnevni nadzor može preživeti
nadogradnju servisa. Kada se tumačenje promeni usred sesije, to je zabeležen događaj
(`InterpretationContextChanged`) i naredne tvrdnje nose novu referencu.

`FACT` nema referencu na tumačenje. Nema šta da tumači.

---

## 6. RAW_CHAIN_RECORDS_OBSERVATIONS

**Sirovi lanac beleži šta je posmatrano, ne šta je to značilo.**

Opažanja i operativni događaji: proba poslata, meta odgovorila, soket povezan sa ovih na one
adrese, sistem zaspao, sistem se restartovao, naša proba se srušila. Ništa od toga ne zastareva
kad se promeni pravilo.

## 7. DERIVED_LEDGER_RECORDS_INTERPRETATIONS

**Zaključci i ocene idu u odvojen, takođe append-only dnevnik**, sa vezom na zapise iz lanca na
koje se oslanjaju i na tumačenje koje ih je proizvelo.

Manifest hešira oba i potpis štiti oba - ali se više nikada ne mešaju. Zaključak se uvek može
napraviti iznova iz lanca; opažanje ne može ni iz čega.

**Zašto:** danas stoje u istom redu, i to je izvor nalaza 4. Kad se pravilo promeni, mora se
moći reći koji deo zapisa je i dalje tačan - a to se ne može ako su izmešani.

**Stari lanac se ne popravlja.** Zapis iz 2.x sadrži i opažanje i tadašnje tumačenje; čita se i
verifikuje po svojoj originalnoj verziji, bez retroaktivnog prevođenja.

---

## 8. LEGACY_PACKETLOSS_IS_NEVER_INTERPRETED_AS_PACKET_LOSS

**Vrednost `PacketLoss` iz 2.x zauvek znači ono što je istorijski značila** - udeo meta koje
nisu odgovorile, tri destinacije po jedna proba. Nikada se ne prikazuje kao pravi gubitak
paketa, ne preračunava se i ne preslikava na nov model.

Kada 3.0 uvede pravo merenje, to je **nov tip sa novim imenom**, i između njih nema automatskog
preslikavanja ni u jednom smeru.

**Zašto ovako izričito:** imena su slična i nekome će za dve godine delovati kao suvišna
komplikacija. Ova invarijanta postoji da taj „pojednostavljeni" mapper ne bude napisan.

---

## 9. RAW_EVIDENCE_IS_APPEND_ONLY

**Sirova evidencija se samo dopisuje.** Nijedan zapis se ne menja ni briše posle upisa; lanac
otisaka to i čini proverljivim.

**Gde se proverava:** `HashChainTests`, `EvidenceRecorderTests`, i characterization test koji
verifikuje zamrznuti lanac iz prethodne verzije.

---

## 10. DERIVED_OUTPUT_NEVER_MUTATES_SOURCE_EVIDENCE

**Pravljenje izveštaja, izvoza, prigovora ili redigovane kopije ne dira izvor.** Indeks je
izvedeni podatak i može se obrisati i pregraditi iz lanca; izveštaj se pravi iznova bez ijedne
izmene u `Raw/`.

**Gde se proverava:** `CharacterizationTests.The_index_rebuilds_from_the_chain_to_the_same_figures`.
U 3.0 dobija i ACL koji to sprovodi, ne samo poštuje.

---

## 11. BASELINE_FIXTURES_ARE_RELEASE_ARTIFACTS

**Fixture na disku, fixture u repozitorijumu i fixture u tagu su tri različite stvari.**

Testovi koji ih čitaju **padaju** kad fajla nema - nikada ga ne preskaču. Test koji ćuti kad mu
nedostaje ulaz gori je od nepostojećeg testa, jer prijavljuje uspeh za posao koji nije uradio.

**Kako je otkriveno:** 2.7.1 je objavljena sa `baseline/` isključenim pravilom iz `.gitignore`
koje je pisano da zaštiti prave sesije. Lokalno je sve prolazilo, na CI-ju su isti testovi pali.

**Gde se proverava:** `BaselineSnapshot.Require` baca sa objašnjenjem;
`CharacterizationTests.Every_artefact_the_snapshot_promises_is_actually_there`; i korak u CI-ju
koji proverava da je svaki fajl iz `baseline/` praćen u gitu.

---

## 12. OBSERVED_PATH_OUTRANKS_PREDICTED_PATH

**Ono što su soketi merenja stvarno uradili jače je od onoga što je tabela ruta predvidela.**

Kad se ne slažu, opažanje opisuje figuru. Tabela ruta koja se unapred složila **ne spasava**
merenje čije su veze otišle drugim adapterom - a merenje čije veze nisu posmatrane time **ne
postaje** neispravno, jer na nekim mašinama se soketi ne mogu pripisati adapteru uopšte.

Oba nalaza se čuvaju odvojeno i prikazuju odvojeno. „Posmatrano" i „predviđeno" su različita
pitanja, i čitalac ima pravo da vidi da su se složili - ili da nisu.

**Kako je otkriveno:** na ovoj mašini merenje označeno Wi-Fi adapterom daje 479 Mbit/s, a svih
šest veza izađe kroz kabl. Do 2.7.2 se to videlo samo kao predviđanje iz tabele ruta.

**Gde se proverava:** `ActualPathTests.What_the_sockets_did_outranks_what_the_route_table_predicted`
i `...An_unobserved_path_is_not_a_defect_of_its_own`.

## 13. UNOBSERVED_PATH_IS_NOT_AGREEMENT

**Veza koja se nije mogla pripisati nijednom adapteru nije saglasnost - ni jedna, ni sve.**

Nijedna razrešena veza → `Unknown`, ne `Match`. Ako su neke razrešene a neke ne, saglasnost se
sudi po razrešenima, ali se ostatak **uvek ispisuje**, da rečenica ne bi zvučala kao da su sve
veze pripisane.

**Kako je otkriveno:** prvo živo puštanje uhvatilo je šest veza i nijednu pripisalo, jer soket
dvostrukog steka javlja IPv4 adresu kao `::ffff:192.168.1.102`, a adapter drži `192.168.1.102`.
Da je pravilo bilo blaže, to bi izgledalo kao uredno merenje.

**Gde se proverava:** `ActualPathTests.Connections_nobody_could_place_are_not_agreement`,
`...Agreement_never_claims_the_connections_it_could_not_place`,
`...An_ipv4_connection_is_recorded_as_ipv4_however_the_socket_wrote_it`.

---

## 14. SEALED_SESSION_IS_NEVER_WRITTEN_INTO_AGAIN

**Kad je paket zapečaćen, folder sesije se više ne dira.**

Zapečaćen je onaj u kome postoji `SHA256SUMS.txt` - spisak koji opisuje tačno te fajlove, i po
kome je najverovatnije već napravljena arhiva i poslata operateru. Svaki fajl dodat posle toga
ostavlja spisak i arhivu da opisuju folder koji se u međuvremenu promenio.

Zato se pre upisa nikada ne pita „koji je folder najnoviji" nego **„koja je sesija otvorena"**.
Merenje kome nema otvorene sesije čeka u korenu izlaza, a prva sledeća sesija ga preuzima - u
folder, u zbirove i u arhivu - i izveštaj kaže da je izvršeno pre početka tog nadzora.

Preuzima se **premeštanjem**, jednom. Kopirano, isto merenje bi se pojavljivalo u svakoj sledećoj
sesiji, i svaki izveštaj bi ga predstavljao kao svoje.

**Kako je otkriveno:** posle 3.0-1a, proverom šta se dešava sa merenjem koje je zapisano dok
nijedna sesija ne teče. Ispostavilo se dvostruko: takvo merenje nijedan izveštaj nije čitao iako
je komentar u kodu tvrdio da hoće, a na mašini sa starijom sesijom u istom folderu odlazilo je
**unutra** - u paket koji je već bio prebrojan i zapakovan.

**Gde se proverava:** `MeasurementFilingTests` (pet slučajeva oko `SessionPaths.FindOpen`),
`EvidencePackageTests.A_measurement_waiting_beside_the_sessions_is_taken_up_by_the_next_one` i
`...A_measurement_taken_after_the_session_ended_is_left_where_it_is`.

---

## 15. NALAZ SE IZVODI IZ CELE GRUPE I IZ UPOREDIVE KONTROLE

**Zaključak o grupi sme se doneti samo ako je pitana cela grupa, i samo poređenjem sa nečim
istorodnim.**

Dve zabrane, jer su dva puta ista greška:

1. Pitati **jednog** člana grupe pa zaključiti o svima. „Dodeljeni DNS server ne odgovara" sme
   se reći tek kad nijedan dodeljeni resolver ne odgovori, ne kad ćuti prvi iz spiska.
2. Porediti nalaz sa **kontrolom druge vrste**. Upit ka IPv6 adresi i upit ka IPv4 adresi idu
   kroz dva različita steka; razlika između njih opisuje stekove, ne zdravlje servera.

**Kako je otkriveno:** prijavio tester 18.08.2026. Ruter mu je dostupan preko IPv6 link-local
adrese i ujedno je prvi DNS server; jedini javni resolver bio je 1.1.1.1, dakle IPv4. Program mu
je celu sesiju prijavljivao kvar DNS-a operatera, dok su mu sistemski DNS, HTTP, TCP i ping
radili bez greške. Nijedan izuzetak nije zabeležen - program nije ni znao da nešto nije u redu.

**Gde se proverava:** `StateClassifierTests.A_silent_ipv6_resolver_is_not_judged_against_an_ipv4_one`,
`...A_silent_ipv6_resolver_is_judged_against_the_ipv6_public_one`,
`...One_silent_resolver_out_of_several_is_not_all_of_them`,
`...Every_assigned_resolver_silent_is_the_finding`.

---

## Za 3.0: one koje još nemaju kod

## 16. SIGNATURE_PROVES_INTEGRITY_NOT_TRUTH

Potpis dokazuje da paket odgovara potpisanom sadržaju, da nije neprimetno menjan posle
potpisivanja, i da je potpisan ključem te instalacije. **Ne dokazuje** da ulaz nije fabrikovan
pre potpisivanja, da host nije bio kompromitovan, niti da je incident nastao kod operatera.

## 17. TRUSTED_TIMESTAMP_PROVES_EXISTENCE_NOT_EVENT_TIME

Vremenski žig treće strane dokazuje da je određeni podatak **postojao pre** određenog trenutka.
Ne dokazuje da se mrežni događaj desio tada, niti da je sadržaj istinit.

Obe idu u `THREAT-MODEL-3.0.md` kao prvi pasus, ne kao dodatak - jer je 2.6 tačno tako i
pogrešila, tvrdnjom koja je zvučala jače nego što jeste.

## 18. PLATFORM_IMPLEMENTATION_NEVER_LEAKS_INTO_EVIDENCE_SEMANTICS

**Svi dokazni ugovori, kanonski zapisi, heš-lanci, manifesti i izvedeni zaključci su 100 %
OS-neutralni.**

Operativni sistem je izvršilac merenja i provajder operativnih podataka, nikada deo semantike
dokaza. Model dokaza u `IEM.Core`, `IEM.Storage`, `IEM.Evidence` i `IEM.Legal` ne sme zavisiti od
toga da li se aplikacija izvršava na Windows-u, Linux-u ili trećoj platformi.

### 18a. PLATFORM_SOURCE_IS_PROVENANCE_NOT_SEMANTICS

**Platforma i konkretni provajder podataka SMEJU biti zabeleženi kao acquisition provenance;
oni NE SMEJU menjati značenje, stanje ili strukturu tvrdnje.**

Na primer:
```
RouteObservation:
  Acquisition:
    Provider = Linux.Rtnetlink (ili Windows.GetBestRoute2)
    ProviderVersion = 1.0.0
```
je forenzički poreklo (provenance). Struktura `RouteObservation`, `PathAgreement` i njihova
dokazna ocena ostaju identični na svim platformama.

## 19. MANIFEST_NEVER_DESCRIBES_MUTABLE_EVIDENCE

**Manifest nikada ne opisuje dokaze koji se još uvek menjaju.**

Generisanje manifesta je završni korak zapečaćenja: upisi u sirovu evidenciju moraju biti
zaustavljeni, tokovi ispražnjeni (flushed), a fajlovi evidencije zatvoreni pre nego što inventar
fajlova počne. Ako se bilo koji fajl evidencije promeni u toku izrade manifesta, izrada se
prekida uz grešku.

## 20. MANIFEST_IS_COMPLETE_OR_DOES_NOT_EXIST

**Manifest postoji u potpunosti ili uopšte ne postoji.**

Manifest se privremeno piše u `.tmp` fajl, i tek nakon uspešnog hashiranja svih fajlova,
provere integriteta i revalidacije veličina atomski preimenuje u `manifest.json`.
Parcijalni ili nekompletni manifest nikada ne sme ostati na disku kao validan artefakt.

## 21. KEY_ID_IS_DERIVED_FROM_PUBLIC_KEY

**Identifikator ključa (`KeyId`) je deterministički izveden iz javnog ključa.**

`KeyId` je definisan kao `sha256:` + Hex(SHA256(SubjectPublicKeyInfoDer)). Verifikator
nezavisno proverava i potvrđuje da `KeyId` odgovara priloženom javnom ključu bez potrebe
za eksternom tabelom mapiranja.

## 22. SIGNING_IDENTITY_NEVER_ROTATES_SILENTLY

**Identitet potpisivanja instalacije se nikada ne rotira automatski ili nečujno.**

Jedna IEM instalacija poseduje jedan trajni signing identitet. Ako postojeći ključ
(npr. TPM-backed) ne može da se otvori usled hardverske promene, operacija potpisivanja
se prekida uz grešku `SigningIdentityUnavailableException`. Sistem nikada ne kreira tiho
novi softverski ključ kao zamenu za nedostupni hardverski ključ.

## 23. SIGNATURE_IS_BOUND_TO_EXACT_MANIFEST

**Potpis je striktno i jednoznačno vezan za tačan heš manifesta.**

Vrednost `ManifestSha256` u `SignatureEnvelope`, stvarni SHA-256 heš kanonskih bajtova
`manifest.json` i podatak nad kojim je izvršeno ECDSA P-256 potpisivanje moraju biti
matematički identični.

## 24. PRIVATE_KEY_NEVER_ENTERS_EVIDENCE_PACKAGE

**Privatni ključ nikada ne ulazi u paket dokaza, logove niti izveštaje.**

Privatni ključ je neizvoziv (`CngExportPolicies.None` / TPM zaštićen). Bilo kakav bajt
privatnog ključa nikada se ne serijalizuje u `manifest.json`, `manifest.sig`, izveštaje
niti tekstualne poruke izuzetaka.

## 25. TIMESTAMP_IS_BOUND_TO_EXACT_SIGNATURE_ENVELOPE

**Vremenski žig je vezan za tačne bajtove omotnice potpisa na disku.**

RFC 3161 `MessageImprint` se računa direktno nad sirovim bajtovima datoteke `manifest.sig`.
Nije dozvoljena ponovna deserijalizacija ili kanonikalizacija objekta pre heširanja za žig,
čime se garantuje da TSA potvrđuje postojanje tačno onog potpisa koji verifikator čita.

## 26. TIMESTAMP_RESPONSE_IS_NEVER_PUBLISHED_BEFORE_SELF_VERIFICATION

**Odgovor vremenskog žiga se nikada ne upisuje bez prethodne potpune samoprovere.**

Token primljen od TSA servera se pre trajnog upisa u `timestamp.tsr` proverava prema
poslatom zahtevu (`timestamp.tsq`), `MessageImprint`-u, `nonce`-u i kriptografskom CMS potpisu.
Nevalidan ili neusklađen odgovor se nikada ne objavljuje na disku kao važeći artefakt.

## 27. PENDING_TIMESTAMP_NEVER_REBUILDS_SIGNED_EVIDENCE

**Naknadno pribavljanje vremenskog žiga nikada ne menja niti ponovo generiše potpisane dokaze.**

Ako tokom završetka sesije internet veza nije bila dostupna (`TrustedTime = Pending`),
naknadni retry vremenskog žiga operiše isključivo nad postojećim `manifest.sig`. Nije
dozvoljena regeneracija manifesta niti ponovno potpisivanje, jer bi to promenilo identitet
i vreme originalno zapečaćenog paketa.

## 28. VERIFIER_HAS_NO_PLATFORM_IMPLEMENTATION_DEPENDENCY

**Verifikator je 100% platform-neutralan i nema zavisnosti od implementacionih modula platforme.**

Paketi `IEM.Verification` i `IEM.Verifier` zavise isključivo od `net10.0` jezgra i nikada
ne referenciraju `IEM.Windows`, `IEM.Service` niti `IEM.App`. Isti verifikator se bez
promena pokreće na Windows-u i Linux-u.

## 29. VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT

**Verifikator tretira paket kao neprijateljski ulaz i nikada ne pristupa fajlovima van korena paketa.**

Sve relativne putanje u manifestu se striktno proveravaju na zabranjene obrasce: apsolutne
putanje, oznake diskova, `..` segmente i NUL bajtove. Verifikator odbija bilo koju putanju
koja bi izlazila van foldera paketa.

## 30. EMBEDDED_PUBLIC_KEY_PROVES_SIGNATURE_MATCH_NOT_EXTERNAL_IDENTITY

**Ugrađeni javni ključ dokazuje matematičko slaganje potpisa, a ne spoljni identitet autora.**

Ako potpis u `manifest.sig` prolazi sa javnim ključem priloženim u omotnici, verifikator
potvrđuje integritet i autorstvo za taj konkretan `KeyId`. Za priznavanje identiteta instalacije
potrebno je nezavisno poznavanje ključa (`--expected-key-id` ili `--trusted-key`).

## 31. OFFLINE_VERIFICATION_NEVER_SILENTLY_USES_NETWORK

**Offline provera garantuje nula mrežnih zahteva.**

Pri opciji `--offline`, verifikator ne vrši DNS upite, HTTP zahteve, niti mrežno dohvatanje
OCSP/CRL/AIA sertifikata. Ako nedostaju lokalni dokazi o opozivu, status poverenja je
`NotEstablished`, nikada lažno `Invalid`.

## 32. VERIFICATION_IS_STRICTLY_READ_ONLY

**Verifikacija je strogo operacija čitanja i nikada ne menja sadržaj paketa evidencije.**

Svi tokovi podataka se otvaraju isključivo u `FileAccess.Read` režimu. Verifikator ne kreira
privremene fajlove, logove niti izveštaje unutar direktorijuma paketa dokaza.

## 33. LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS

**Lokalni neuspeh probe nikada se ne uračunava kao mrežni gubitak paketa.**

Kada lokalni mrežni stek ne uspe da pošalje probu (npr. greška pri kreiranju soketa,
nedostatak lokalnih resursa ili dozvola), takav pokušaj se označava kao `LocalExecutionFailure`
i isključuje iz imenilaca mrežnog gubitka (`EligibleCount = ExecutedCount - LocalFailureCount`).

## 34. LOSS_RATIO_IS_NEVER_AVERAGED_ACROSS_TARGETS

**Udeo gubitka odgovora se nikada ne prosečuje preko različitih meta.**

Statistika gubitka se vodi strogo pojedinačno po meti i porodici adresa. Ako jedna meta
odbacuje ili filtrira ICMP, njen rezultat ne sme veštački uvećati procenat gubitka ostalih,
zdravih meta.

## 35. TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT

**Istek vremena (Timeout) se nikada ne pretvara u fiktivnu vrednost kašnjenja (RTT).**

Statistika kašnjenja (Min, Medijana, P95, Max) se računa isključivo iz uspešno primljenih
odgovora (`ReplyReceived`). Istek vremena nije veliko kašnjenje i ne unosi se u distribuciju RTT-a.

## 36. DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD

**Varijacija kašnjenja uvek eksplicitno navodi naziv primenjenog matematičkog metoda.**

Termin „džiter” bez specifikacije se ne koristi u ugovoru dokaza. Metrika varijacije kašnjenja
navodi tačan algoritam (npr. `ConsecutiveReplyAbsoluteDifference`) i veličinu uzorka.

## 37. PROBE_RESULT_PRESERVES_TARGET_AND_ADDRESS_FAMILY

**Rezultati proba striktno razdvajaju IPv4 i IPv6 putanje.**

Čak i kada pripadaju istom logičkom servisu (npr. Cloudflare DNS), IPv4 (`1.1.1.1`) i
IPv6 (`2606:4700:4700::1111`) predstavljaju nezavisne mrežne rute i njihovi rezultati
se nikada ne spajaju u jednu metriku.

## 38. ICMP_NO_REPLY_DOES_NOT_PROVE_PACKET_DROP_LOCATION

**Izostanak ICMP odgovora ne dokazuje tačnu lokaciju gubitka paketa.**

Evidencija beleži činjenicu da odgovor nije primljen pre isteka vremena (`NoReplyBeforeTimeout`),
ali ne tvrdi jednostrano gde je paket odbačen niti da li odredište selektivno filtrira ICMP.

## 39. ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY

**Izostanak odgovora nikada ne dokazuje da meta ne podržava ICMP ili protokol.**

Ako meta nikada nije odgovorila, njen status mogućnosti je `ResponseNotYetObserved`,
a nikada lažna tvrdnja `IcmpSupported = false`, jer izostanak odgovora može biti posledica
rute, lokalnog filtera ili politike provajdera.

## 40. TARGET_HEALTH_NEVER_REWRITES_PRIOR_EVIDENCE

**Procena zdravlja mete nikada ne prepisuje niti menja prethodno snimljene dokaze.**

Zdravlje je izvedeni zaključak (`Inference/Assessment`) zapisan kao nepromenljivi niz snimaka
(`TargetHealthSnapshot`). Promena stanja zdravlja generiše novi zapis i nikada ne menja
istorijske opservacije.

## 41. TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY

**Promena stanja mete nikada retroaktivno ne menja težinu ranijih događaja.**

Ako meta postane degradirana u trenutku $T_1$, sve prethodne opservacije do tog trenutka
zadržavaju svoju punu težinu (`EvidenceContribution = Full`). Težina se menja isključivo
za buduće klasifikacije od trenutka $T_1$.

## 42. TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED

**Suspenzija ili isključenje mete je uvek vidljivo u evidenciji uz navođenje razloga.**

Meta koja postane nepouzdana dobija status `EvidenceContribution = Suspended`, ali nikada
tiho ne nestaje iz izveštaja. Svaki izveštaj eksplicitno dokumentuje razlog i vremenski
period suspenzije.

## 43. SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT

**Zajednički pad više meta nikada se automatski ne pripisuje nezdravosti pojedinačne mete.**

Kada više referentnih meta ili sve mete istovremeno prestanu da odgovaraju, to je signal
zajedničkog lokalnog ili mrežnog prekida (`SharedNetworkFailure`) i ne inkrementira brojače
kvara specifične za pojedinačnu metu.

## 44. TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE

**Istorija zdravlja meta je u potpunosti deterministički obnovljiva iz perzistiranih dokaza.**

Brisanjem keša izvedenih zaključaka, prolazak kroz sirove probe primenom iste verzije politike
(`TargetHealthPolicy`) mora proizvesti identične snimke stanja zdravlja i razloge.

## 45. TARGET_HEALTH_IS_SCOPED_TO_ENDPOINT_AND_ADDRESS_FAMILY

**Zdravlje se prati strogo po konkretnoj IP adresi, protokolu i porodici adresa.**

IPv4 i IPv6 krajnje tačke istog logičkog servisa poseduju potpuno odvojene tokove zdravlja
i promena stanja na jednoj nikada ne utiče na drugu.

## 46. ABSENCE_OF_GATEWAY_RESPONSE_NEVER_PROVES_UNSUPPORTED_CAPABILITY

**Izostanak odgovora mrežnog prolaza nikada ne dokazuje nepodržanost sposobnosti.**

Ako ruter u početnom periodu ne odgovara na ICMP, status sposobnosti je `ResponseNotYetObserved`,
a nikada lažno `Unsupported`, jer ruter može privremeno filtrirati saobraćaj ili odgovoriti kasnije.

## 47. OBSERVED_GATEWAY_CAPABILITY_IS_ESTABLISHED_ONLY_BY_POSITIVE_EVIDENCE

**Sposobnost mrežnog prolaza se uspostavlja isključivo na osnovu pozitivnog dokaza.**

Tek kada se zabeleži bar jedan uspešan i validan odgovor sa adrese prolaza, sposobnost
dobija status `ObservedSupported`.

## 48. GATEWAY_CAPABILITY_HISTORY_IS_APPEND_ONLY

**Istorija profilisanja mogućnosti prolaza je isključivo append-only niz snimaka.**

Svaka promena u ponašanju generiše novi nepromenljivi snimak procene (`GatewayAssessmentSnapshot`)
i nikada ne briše ranije opažene činjenice.

## 49. CURRENT_GATEWAY_BEHAVIOR_NEVER_REWRITES_PRIOR_CAPABILITY_EVIDENCE

**Trenutni prestanak odgovora nikada ne poništava istorijski dokazanu sposobnost.**

Ako je ruter ranije dokazano odgovarao na ICMP pa prestane, status postaje `PreviouslyObserved`
i generiše signal `PreviouslyObservedCapabilityMissing`, a ne retroaktivno brisanje da je sposobnost
ikada postojala.

## 50. NEIGHBOR_RESOLUTION_NEVER_PROVES_GATEWAY_FORWARDING

**Uspešno razrešavanje suseda (ARP/NDP) nikada ne dokazuje rutiranje i ispravnost prolaza.**

Postojanje MAC adrese prolaza na link-layer nivou dokazuje samo lokalnu vezu, a ne
da ruter uspešno prosleđuje pakete ka internetu ili da mu je upstream link ispravan.

## 51. ROUTE_PRESENCE_NEVER_PROVES_GATEWAY_REACHABILITY

**Postojanje podrazumevane rute u operativnom sistemu ne dokazuje dostupnost prolaza.**

Default ruta u tabeli rutiranja je lokalna OS konfiguracija i ne garantuje da je fizički
uređaj prolaza uključen ili mrežni kabl povezan.

## 52. INITIAL_LEARNING_WINDOW_NEVER_FREEZES_UNKNOWN_AS_UNSUPPORTED

**Istek početnog prozora učenja nikada ne zamrzava nepoznate sposobnosti kao nepodržane.**

Nakon isteka inicijalnog učenja (npr. 5 minuta), status ostaje `ResponseNotYetObserved` / `Unknown`
i svaki kasniji pozitivan odgovor normalno uspostavlja `ObservedSupported`.

## 53. GATEWAY_CAPABILITY_IS_SCOPED_TO_GATEWAY_IDENTITY_AND_NETWORK_CONTEXT

**Sposobnost prolaza je strogo vezana za identitet prolaza, interfejs i mrežni kontekst.**

Promena Wi-Fi mreže, interfejsa ili DHCP adrese započinje novi nezavisni profil i nikada
ne nasleđuje sposobnosti prethodnog rutera.

## 54. GATEWAY_CAPABILITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE

**Profil i istorija procene prolaza su u potpunosti deterministički obnovljivi iz sirovih dokaza.**

Brisanjem keša izvedenih zaključaka, evaluacija sirovih opservacija (`GatewayCapabilityObservation`)
prema verziranoj politici mora proizvesti identične snimke stanja i razloge.

## 55. LOCAL_EXECUTION_FAILURE_IS_NEVER_REPORTED_AS_NETWORK_FAILURE

**Lokalni neuspeh izvršenja probe se nikada ne prijavljuje kao mrežni pad.**

Kada lokalna operacija OS-a ne uspe (npr. kreiranje soketa, lokalni bind, poziv resolver API-ja),
evidencija beleži `FailedLocalSystem` i takav pokušaj je nepodoban (`Ineligible`) za dokazivanje
mrežnog gubitka ili nedostupnosti servera.

## 56. AMBIGUOUS_PROBE_FAILURE_REMAINS_UNKNOWN

**Nejasan ili višeznačan neuspeh probe ostaje označen kao Unknown.**

Ako raspoloživi dokazi ne mogu jednoznačno dokazati domen greške, klasifikator dodeljuje
`FailureDomain.Unknown` umesto nagađanja najverovatnije kategorije.

## 57. TIMEOUT_DESCRIBES_OBSERVED_NON_COMPLETION_NOT_FAILURE_CAUSE

**Istek vremena opisuje izostanak odgovora u roku, a ne uzrok pada.**

`Timeout` je deskriptivno stanje nepotpunog izvršenja pre definisanog roka, a ne dokaz
da je pao provajder, ruter ili odredišni server.

## 58. NATIVE_ERROR_CODE_IS_EVIDENCE_INPUT_NOT_FINAL_SEMANTIC_CLASSIFICATION

**Izvorni sistemski kod greške je ulazni podatak dokaza, a ne konačna semantička klasifikacija.**

Windows WinSock kodovi i Linux errno vrednosti se čuvaju kao poreklo dokaza (`provenance`),
dok se semantički domen (`FailureDomain`) izvodi kroz verzirana pravila politike.

## 59. INTERNAL_PROBE_ERROR_NEVER_CONTRIBUTES_NETWORK_FAILURE_EVIDENCE

**Interna programska greška nikada ne doprinosi dokazivanju mrežnog prekida.**

Neočekivani izuzeci ili unutrašnje programske greške dobijaju status `InternalError`
i strogo se isključuju iz bilo kakve procene mrežnih performansi.

## 60. REMOTE_FAILURE_REQUIRES_POSITIVE_REMOTE_OR_PROTOCOL_FAILURE_EVIDENCE

**Kvar udaljene strane zahteva pozitivan protokolski dokaz.**

Domen `FailedRemote` se dodeljuje isključivo kada je primljen stvarni negativni protokolski
odgovor (npr. HTTP 503 ili DNS SERVFAIL) sa odredišta.

## 61. NETWORK_FAILURE_CLASSIFICATION_NEVER_IDENTIFIES_UNPROVEN_ROOT_CAUSE

**Klasifikacija mrežnog otkaza nikada ne nagađa nedokazani koren uzroka.**

Status `FailedNetwork` označava postojanje mrežnog stanja koje je sprečilo probu (npr.
nepostojeća ruta na interfejsu), ali ne tvrdi jednostrano koji je čvor kriv.

## 62. PROBE_EXECUTION_ELIGIBILITY_IS_EXPLICIT_NOT_IMPLICIT

**Podobnost probe za evidenciju je eksplicitno definisana, a ne implicitno nagađana.**

Svaki pokušaj dobija jasnu oznaku podobnosti (`Eligible`, `Limited`, `Ineligible`)
kako nijedan statistički agregator ne bi nagađao težinu neuspele probe.

## 63. SINGLE_PROBE_EXECUTION_FAILURE_NEVER_ESTABLISHES_PROBE_UNHEALTHINESS

**Pojedinačni lokalni neuspeh nikada ne proglašava mehanizam probe nezdravim.**

Zdravlje same probe prati se kroz prozore posmatranja sa histerezisom, sprečavajući
preuranjene oscilacije statusa.

## 64. PROBE_HEALTH_IS_SCOPED_TO_PROBE_IMPLEMENTATION_AND_RELEVANT_CONTEXT

**Zdravlje mehanizma probe je strogo vezano za tip probe, verziju implementacije i kontekst.**

Lokalna greška DNS resolvera ne utiče na zdravlje ICMP mehanizma, niti IPv4 greške utiču na IPv6.

## 65. PROBE_HEALTH_NEVER_REWRITES_EXECUTION_EVIDENCE

**Procena zdravlja mehanizma probe nikada ne menja sirove zapise o izvršenju proba.**

Istorija zdravlja je nepromenljiv append-only tok snimaka (`ProbeHealthSnapshot`).

## 66. PROBE_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE

**Zdravlje proba je u potpunosti deterministički obnovljivo iz perzistiranih pokušaja.**

Brisanjem izvedenog keša, ponovni prolazak kroz sirove pokušaje izvršenja (`ProbeExecutionAttempt`)
proizvodi identične snimke i klasifikacije.

## 67. SESSION_STORAGE_LAYOUT_IS_VERSIONED_AND_EXPLICIT

**Struktura skladišta sesije je verzirana i eksplicitno deklarisana u layout.json.**

Sve relativne putanje pod-direktorijuma (`Raw`, `Derived`, `Evidence`, `Exports`) i pravila
politike definisane su u kanonskom deskriptoru `layout.json`, koji se kreira jednom i nikada
ne menja značenje.

## 68. MANIFEST_SCOPE_IS_DEFINED_BY_ARTIFACT_ROLE

**Obuhvat manifesta je striktno određen semantičkom ulogom artefakta.**

Manifest štiti isključivo zaštićene artefakte (`layout.json`, `Raw/**`, `Derived/**`).
Integritetna omotnica (`Evidence/**`) i korisnički izvozi (`Exports/**`) su isključeni
iz inventara manifesta.

## 69. USER_WRITABLE_CONTENT_NEVER_BECOMES_PROTECTED_EVIDENCE

**Korisnički izmenljiv sadržaj nikada ne postaje zaštićeni dokaz.**

Zona `Exports/` je predviđena za slobodno korisničko čitanje i modifikovanje (HTML, PDF, CSV)
i nikada se ne tretira kao zaštićeni dokaz niti se uključuje u heš-lanac i manifest.

## 70. POST_SIGNATURE_WRITES_ARE_LIMITED_TO_EXPLICIT_ENVELOPE_ARTIFACTS

**Upisi nakon potpisivanja su strogo ograničeni na eksplicitne artefakte omotnice.**

Nakon pečaćenja sesije (`Sealed`), jedini dozvoljeni upisi su naknadni pokušaji dobijanja
vremenskog žiga u `Evidence/timestamp/` zoni, dok su `Raw/`, `Derived/`, `manifest.json` i `manifest.sig`
apsolutno nepromenljivi.

## 71. AUTHORITATIVE_RAW_AND_DERIVED_ARTIFACTS_ARE_APPEND_ONLY_UNTIL_SEAL

**Autoritativni sirovi i izvedeni artefakti su strogo append-only do trenutka pečaćenja.**

Nema prepisivanja postojećih zapisa u `Raw/` i `Derived/` zonama tokom aktivne sesije.

## 72. SEALED_PROTECTED_ARTIFACTS_ARE_NEVER_MUTATED_IN_PLACE

**Zapečaćeni zaštićeni artefakti se nikada ne menjaju u mestu.**

Nakon prelaska sesije u stanje `Sealed`, svi zaštićeni fajlovi su trajno zamrznuti.

## 73. LEGACY_SESSION_LAYOUT_IS_NEVER_MIGRATED_IN_PLACE

**Nasleđeni rasporedi sesija (v2.x) se nikada ne migriraju u mestu.**

Stari paketi ostaju bajt-po-bajt identični kako su snimljeni; čitač prepoznaje verziju
rasporeda i primenjuje odgovarajući resolver.

## 74. EXPORTS_NEVER_AFFECT_EVIDENCE_INTEGRITY

**Izmene u zoni izvoza nikada ne utiču na integritet dokaznog paketa.**

Korisnik može slobodno brisati, menjati ili kreirati fajlove u `Exports/` folderu bez uticaja
na forenzičku verifikaciju sesije.

## 75. USER_WRITABLE_EXPORTS_ARE_NEVER_TRUSTED_AS_EVIDENCE_INPUT

**Korisnički izvozi se nikada ne koriste kao ulazni podaci za dokazne tvrdnje.**

Servis nikada ne čita podatke iz `Exports/` foldera radi donošenja novih zaključaka.

## 76. FILESYSTEM_ACL_IS_PROTECTION_PROVENANCE_NOT_CRYPTOGRAPHIC_INTEGRITY

**Sistemska prava pristupa (ACL) su operativno poreklo zaštite, a ne zamena za kriptografski integritet.**

ACL obezbeđuje lokalnu izolaciju tokom rada, dok pravu garanciju nepromenljivosti daju
heš-lanac, digitalni potpis i vremenski žig.

## 77. PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS

**Privilegovani upisi dokaza nikada ne prate nepouzdane reparse tačke (junction/symlink).**

Pre upisa privilegovanog servisa, proverava se da nijedan segment putanje unutar sesije nije
reparse tačka koja bi preusmerila upis van predviđenog korena sesije.

## 78. PROTECTED_ARTIFACT_PATH_NEVER_ESCAPES_SESSION_ROOT

**Putanje zaštićenih artefakata nikada ne smeju izaći van korena sesije.**

Pokušaji korišćenja `..`, apsolutnih putanja, oznaka diskova ili NUL karaktera se odbacuju.

## 79. PUBLISHED_PROTECTED_ARTIFACT_IS_COMPLETE_OR_ABSENT

**Objavljeni zaštićeni artefakt je uvek potpun ili ne postoji.**

Pisanje se vrši u privremeni `.tmp` fajl i tek nakon kompletnog i uspešnog upisa atomski se
preimenuje u konačni naziv.

## 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR

**Odstupanje od bezbednosnih prava skladišta se nikada tiho ne briše popravkom.**

Svako detektovano odstupanje od očekivanog ACL-a se beleži kao operativna opservacija
(`StorageProtectionObservation`) pre eventualne popravke.

## 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY

**Dokazna sesija nikada ne počinje bez uspešno uspostavljenih granica zaštite skladišta.**

Ako servis ne može da postavi očekivana prava pristupa ili je folder nebezbedan, sesija
odbija pokretanje u dokaznom režimu.

## 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS

**Sigurnosni mehanizam fajl sistema je platformski provenance, a ne semantika dokaza.**

Windows DACL i Linux POSIX dozvole čuvaju se kao poreklo zaštite, dok je semantičko stanje
zaštite (`ProtectedWriteBoundaryEstablished`) platform-neutralno.

## 83. IPC_TRANSPORT_NEVER_DEFINES_COMMAND_SEMANTICS

**IPC transport nikada ne definiše semantiku komandi.**

Transportni sloj (Named Pipe na Windows-u ili Unix Domain Socket na Linux-u) služi
isključivo za prenos bajtova i prepoznavanje peer identiteta, dok Core odlučuje šta komanda
znači i kako se izvršava.

## 84. PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION

**Identitet pozivaoca je poreklo autentifikacije, a ne automatska autorizacija.**

Prepoznavanje Windows SID-a ili Linux UID-a je činjenica (`FACT`), dok je odluka da li taj
korisnik sme da pokrene ili zaustavi sesiju stvar autorizacione politike.

## 85. TRANSPORT_ACCESS_NEVER_IMPLIES_COMMAND_AUTHORIZATION

**Uspostavljena veza na transportu nikada ne implicira autorizaciju za izvršenje komande.**

To što je klijent uspešno otvorio konekciju na Named Pipe ili Socket ne znači da ima pravo
na izvršenje svake komande; svaka komanda se autorizuje pojedinačno.

## 86. UNKNOWN_IPC_PROTOCOL_VERSION_IS_NEVER_SILENTLY_DOWNGRADED

**Nepoznata verzija IPC protokola se nikada tiho ne spušta na nižu verziju.**

Ako klijent pošalje nepodržanu veću verziju protokola, zahtev se eksplicitno odbija statusom
`UnsupportedProtocol`.

## 87. UNKNOWN_COMMAND_IS_REJECTED_NOT_GUESSED

**Nepoznata komanda se odbija, a ne nagađa.**

Svaka komanda van eksplicitne liste dozvoljenih komandi se odbacuje statusom `UnsupportedCommand`.

## 88. IPC_MESSAGE_BOUNDARY_IS_EXPLICIT_AND_BOUNDED

**Granica IPC poruke je eksplicitno definisana dužinom i strogo ograničena.**

Poruke se prenose sa 4-bajtovnim prefiksom dužine i maksimalnim limitom (1 MB), sprečavajući
napade iscrpljivanja memorije.

## 89. IPC_EXPOSES_EXPLICIT_COMMANDS_NEVER_ARBITRARY_SERVICE_EXECUTION

**IPC izlaže isključivo eksplicitne komande, nikada proizvoljno izvršavanje servisnih metoda.**

IPC interfejs izlaže striktnu belu listu komandi (`GetServiceStatus`, `StartSession`, `StopSession`,
`FinalizeSession`, `RetryTimestamp`, `CreateExport`) i ne omogućava dinamičko pozivanje metoda.

## 90. UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED

**Neidentifikovani pozivalac se uvek bezbedno odbija (fail closed).**

Ako se identitet pozivaoca ne može nepobitno utvrditi, autorizacija vraća `Unknown` i komanda se odbija.

## 91. AUTHORIZED_COMMAND_NEVER_BYPASSES_SESSION_STATE_INVARIANTS

**Autorizovana komanda nikada ne može zaobići invarijante stanja sesije.**

Čak ni administrator ne može IPC komandom izmeniti zapečaćene sirove dokaze, jer takva
operacija ne postoji u arhitekturi.

## 92. RETRIED_STATE_CHANGING_REQUEST_NEVER_CAUSES_DUPLICATE_EFFECT

**Ponovljeni komandni zahtev koji menja stanje nikada ne izaziva dupli efekat.**

State-changing komande sa istim `RequestId` koriste idempotency keš i vraćaju raniji rezultat
bez ponavljanja akcije (npr. bez duplog potpisivanja ili otvaranja nove sesije).

## 93. EVIDENCE_AFFECTING_CONTROL_ACTIONS_ARE_AUDITABLE

**Kontrolne akcije koje utiču na dokaznu sesiju su uvek zabeležene u operativni audit trag.**

Komande poput `StartSession`, `StopSession`, `FinalizeSession` i `RetryTimestamp` kreiraju
nepromenljiv zapis `ControlCommandObserved`.

## 94. CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD

**Identitet pozivaoca se uvek preuzima iz transportnog sloja, nikada iz tela poruke klijenta.**

Bilo kakav tvrdnja o identitetu poslata u JSON payload-u klijenta se ignoriše; autoritativan
je isključivo SID/UID dobijen od operativnog sistema.

## 95. PLATFORM_CREDENTIAL_FORMAT_NEVER_CHANGES_COMMAND_AUTHORIZATION_SEMANTICS

**Format platformskih kredencijala nikada ne menja semantiku autorizacije komandi.**

Windows SID i Linux UID se mapiraju na isti autorizacioni kontekst, dajući identičnu poslovnu
semantiku na oba sistema.

## 96. CLIENT_DISCONNECT_NEVER_INTERRUPTS_A_COMMITTED_EVIDENCE_TRANSITION

**Prekid veze klijenta nikada ne prekida započetu i potvrđenu tranziciju dokaza.**

Kada proces pečaćenja ili potpisivanja sesije pređe tačku potvrde, servis završava atomsku
operaciju bez obzira da li je klijentska IPC konekcija u međuvremenu prekinuta.

## 97. SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME

**Vreme mirovanja ili spavanja računara (Suspend/Sleep) se nikada ne tumači kao mrežni prekid.**

Poređenjem `BootElapsedIncludingSuspend` i `ActiveElapsedExcludingSuspend` detektuje se
vreme kada je računar bio u sleep/hibernate stanju, i taj interval se klasifikuje kao
odsustvo izvršenja alata, a ne kao pad interneta.

## 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION

**Sistemski UTC sat nikada ne definiše proteklo trajanje merenja.**

Sistemski sat može biti ručno promenjen ili sinhronizovan preko NTP-a; trajanje se uvek
meri monotonim brojačem i nepristrasnim (unbiased) intervalima.

## 99. MONOTONIC_TIME_IS_NEVER_PRESENTED_AS_ABSOLUTE_UTC

**Monotono vreme se nikada ne prikazuje kao apsolutno UTC vreme.**

Monotoni brojač dokazuje trajanje i redosled događaja unutar istog pokretanja sistema, ali
nema apsolutnu vremensku referencu van svog domena.

## 100. BOOT_CONTINUITY_IS_NEVER_ASSUMED_WHEN_IDENTITY_EVIDENCE_IS_AMBIGUOUS

**Kontinuitet pokretanja sistema se nikada ne pretpostavlja kada su dokazi nejasni.**

Ako dokazi o pokretanju nisu usaglašeni ili su nepotpuni, stanje kontinuiteta ostaje `Ambiguous`.

## 101. BOOT_IDENTITY_CHANGE_SPLITS_TIME_CONTINUITY

**Promena identiteta pokretanja sistema prekida vremenski kontinuitet.**

Svaki novi restart sistema započinje novu vremensku osu (`BootBoundaryObserved`) i zabranjuje
izračunavanje monotonih razlika preko granice dva restarta.

## 102. BOOT_OBSERVATION_HISTORY_IS_APPEND_ONLY

**Istorija opservacija pokretanja sistema je striktno append-only.**

Ranije zabeleženi `BootObservation` zapisi se nikada ne menjaju niti brišu unazad.

## 103. CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE

**Prekid kontinuiteta sata zahteva poređenje sa nezavisnim izvorom proteklog vremena.**

Skok ili pomeranje sata se dokazuje upoređivanjem razlike UTC vremena sa monotonim proteklim vremenom.

## 104. CLOCK_DISCONTINUITY_NEVER_IDENTIFIES_AN_UNPROVEN_ADJUSTMENT_CAUSE

**Prekid kontinuiteta sata nikada ne imenuje nedokazani uzrok podešavanja.**

Evidencija beleži činjenicu skoka sata (`ForwardAdjustmentObserved`/`BackwardAdjustmentObserved`),
a ne nagađanja tipa „korisnik je promenio sat“ ili „NTP je korigovao vreme“.

## 105. EVENT_ORDER_WITHIN_A_BOOT_IS_NEVER_DERIVED_FROM_WALL_CLOCK_ALONE

**Redosled događaja unutar istog pokretanja se nikada ne izvodi samo iz sistemskog sata.**

Monotoni brojač i redosled heš-lanca su autoritativni za vremenski sled događaja čak i ako
je UTC sat vraćen unazad.

## 106. CLOCK_ADJUSTMENT_NEVER_REWRITES_PREVIOUS_EVENT_TIMESTAMPS

**Podešavanje sata nikada ne prepisuje ranije zabeležene vremenske oznake događaja.**

Istorijski zapisi ostaju nepromenjeni sa izvornim UTC i monotonim podacima u trenutku nastanka.

## 107. MONOTONIC_DURATION_IS_NEVER_COMPUTED_ACROSS_BOOT_INSTANCES

**Monotono trajanje se nikada ne računa preko granica različitih pokretanja sistema.**

Nakon restarta, monotoni brojač počinje iznova i delta između vrednosti pre i posle restarta
je semantički nevažeća (dodeljuje se nula uz `BootBoundaryObserved`).

## 108. HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION

**Pauza usled mirovanja računara nikada ne doprinosi trajanju mrežnog prekida.**

Period proveden u sleep stanju se eksplicitno isključuje iz statistike pada mreže.

## 109. SUSPEND_RESUME_NEVER_CREATES_A_NEW_BOOT_INSTANCE_BY_DEFAULT

**Buđenje iz mirovanja (resume) podrazumevano ne stvara novi identitet pokretanja sistema.**

Sleep i wake održavaju isti `BootInstanceId`, osim ukoliko dokazi ne potvrde stvarni restart OS-a.

## 110. SERVICE_RESTART_NEVER_IMPLIES_HOST_REBOOT

**Restart servisa nikada ne implicira restart celog operativnog sistema.**

Restartovanje aplikacije ili servisa generiše novi `ServiceInstanceId`, dok `BootInstanceId`
ostaje nepromenjen.

## 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY

**Nedostupan izvor vremena nikada ne sintetizuje veštačko vreme niti kontinuitet.**

Ako sistemski API za vreme ne uspe, status postaje `Unknown` uz zabeleženu grešku, a ne veštačka nula.

## 112. TIME_CONTINUITY_IS_REBUILDABLE_FROM_PERSISTED_TEMPORAL_EVIDENCE

**Vremenski kontinuitet je u potpunosti deterministički obnovljiv iz perzistiranih dokaza.**

Brisanjem keša, evaluacija sirovih `ClockSample` i `BootObservation` zapisa prema politici
proizvodi identične procene kontinuiteta.

## 113. PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS

**Platformski izvor vremena je provenance, a ne vremenska semantika.**

Windows API pozivi (`GetSystemTimePreciseAsFileTime`, `QueryUnbiasedInterruptTimePrecise`) i
Linux izvori (`CLOCK_BOOTTIME`, `CLOCK_MONOTONIC_RAW`) beleže se kao poreklo, dok je semantički
model kontinuiteta jedinstven i platform-neutralan.

## 114. EVIDENCE_QUALITY_IS_ASSESSMENT_NOT_FACT

**Kvalitet dokaza je procena (ASSESSMENT), a ne činjenica (FACT).**

Evidence Quality Engine ne izmišlja nova opažanja niti menja sirove dokaze, već procenjuje
u kojoj meri postojeći dokazi podržavaju određenu tvrdnju.

## 115. EVIDENCE_QUALITY_IS_SCOPED_TO_THE_CLAIM_OR_ASSESSMENT_PURPOSE

**Kvalitet dokaza je strogo vezan za svrhu tvrdnje i posmatrani domen.**

Ista sesija može imati snažan kvalitet (`Strong`) za dostupnost meta, a ograničen (`Limited`)
za tačno trajanje prekida usled diskontinuiteta sata.

## 116. CURRENT_HEALTH_STATE_NEVER_REWEIGHTS_PRIOR_QUALITY_INTERVALS

**Trenutno stanje na kraju sesije nikada retroaktivno ne menja težinu ranijih intervala.**

Kvalitet se računa segmentirano po intervalima na vremenskoj osi, a ne globalnim prosekom
stanja na kraju sesije.

## 117. INELIGIBLE_EVIDENCE_NEVER_REENTERS_QUALITY_AGGREGATION

**Nepodobni dokazi (Ineligible) se nikada ponovo ne uključuju u agregaciju kvaliteta.**

Pokušaji sa lokalnim/internim greškama su isključeni iz mrežne statistike.

## 118. UNKNOWN_QUALITY_INPUT_NEVER_COUNTS_AS_POSITIVE_SUPPORT

**Nepoznati ulazni podaci o kvalitetu se nikada ne računaju kao pozitivna podrška tvrdnji.**

Status `Unknown` ostaje nepoznat i ne pretvara se u pretpostavljeni uspeh.

## 119. NON_OBSERVABLE_TIME_IS_NEVER_TREATED_AS_NEGATIVE_NETWORK_EVIDENCE

**Vreme kada sistem nije merio (NotObservable) se nikada ne tretira kao negativan dokaz o mreži.**

Periodi mirovanja/spavanja (suspend) se isključuju iz imenioca aktivnog posmatranja.

## 120. REDUCED_OR_EXCLUDED_EVIDENCE_IS_ALWAYS_VISIBLE_AND_REASONED

**Smanjeni ili isključeni dokazi su uvek vidljivi i obrazloženi u izveštaju.**

Isključenje intervala (`EvidenceContributionDecision`) beleži jasne razloge i reference.

## 121. CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY

**Kritični otkaz kvaliteta se ne može neutralisati prosekom dobrih dimenzija.**

Ako je vremenski kontinuitet prekinut, tvrdnja o trajanju prekida se ograničava kroz hard gates
bez obzira na visoke ocene ostalih dimenzija.

## 122. QUALITY_COVERAGE_DENOMINATOR_IS_ALWAYS_EXPLICIT

**Imenilac pokrivenosti kvalitetom je uvek eksplicitno naveden.**

Izveštaj jasno razdvaja ukupno vreme sesije, vreme mirovanja i procenjivo aktivno vreme.

## 123. PACKAGE_INTEGRITY_NEVER_PROVES_MEASUREMENT_TRUTH

**Integritet paketa nikada ne dokazuje istinitost samih merenja.**

Digitalni potpis dokazuje nepromenljivost datoteka, a ne da su izmereni podaci tačni ili da je provajder kriv.

## 124. INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS

**Nevažeći integritet paketa se ne može kompenzovati jakim merenjima.**

Ako je heš ili potpis nevažeći (`Invalid`), ukupna forenzička upotrebljivost je `Insufficient`.

## 125. TRUST_NOT_ESTABLISHED_IS_NEVER_PRESENTED_AS_INVALID_MEASUREMENT_EVIDENCE

**Neuspostavljeni eksterni trust se nikada ne prikazuje kao nevažeće merenje.**

Izostanak TSA žiga znači `Trust = NotEstablished`, a ne da su sama merenja nevažeća.

## 126. PROVISIONAL_QUALITY_IS_NEVER_PRESENTED_AS_FINAL

**Privremeni kvalitet tokom rada se nikada ne prikazuje kao konačan.**

Aktivna sesija ima status `Provisional`, a zapečaćena `Finalized`.

## 127. EVIDENCE_QUALITY_POLICY_IS_VERSIONED_AND_HASHED

**Politika ocene kvaliteta je verzirana i identifikovana hešom.**

Ocene referenciraju tačnu verziju i heš politike ([EvidenceQualityPolicy.cs](file:///d:/ProjektiApp/testneta/src/IEM.Core/Quality/EvidenceQualityPolicy.cs)).

## 128. QUALITY_REANALYSIS_CREATES_A_NEW_ASSESSMENT_AND_NEVER_MUTATES_THE_OLD_ONE

**Reanaliza kvaliteta kreira novi snimak procene i nikada ne prepisuje stari.**

Nove verzije politike proizvode novu procenu koja referencira prethodnu (`ReanalysisOf`).

## 129. EVIDENCE_QUALITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE

**Ocena kvaliteta dokaza je u potpunosti deterministički obnovljiva iz perzistiranih dokaza.**

Brisanjem keša, ponovni prolaz kroz zapise proizvodi identične intervale i ocene.

## 130. EVIDENCE_QUALITY_NEVER_REWRITES_OR_DELETES_SOURCE_EVIDENCE

**Ocena kvaliteta nikada ne prepisuje niti briše izvorne dokaze.**

Sirovi zapisi u `Raw/` ostaju potpuno nepromenjeni.

## 131. PLATFORM_PROVENANCE_NEVER_CHANGES_EVIDENCE_QUALITY_SEMANTICS

**Platformski provenance nikada ne menja semantiku ocene kvaliteta.**

Isti nivo dokaza daje identičan band na Windows-u i Linux-u.

## 132. REPORT_RENDERERS_NEVER_CONTAIN_EVIDENCE_BUSINESS_LOGIC

**Rendereri izveštaja nikada ne sadrže poslovnu logiku dokaza.**

HTML, PDF, CSV, Complaint i RATEL rendereri su striktno zaduženi za prezentaciju gotovog
`ReportDocumentModel`-a i nikada ne računaju procente gubitka, trajanje prekida ili uzroke kvara.

## 133. REPORT_MODEL_CONSUMES_ESTABLISHED_ANALYSIS_AND_NEVER_REINTERPRETS_RAW_EVIDENCE

**Model izveštaja koristi uspostavljenu analizu i nikada ponovo ne interpretira sirove dokaze.**

Generator izveštaja troši gotov `EvidenceAnalysisSnapshot` umesto da samostalno donosi
novu interpretaciju sirovih zapisa.

## 134. DOCUMENT_PURPOSE_MAY_CHANGE_COMPOSITION_BUT_NEVER_EVIDENCE_SEMANTICS

**Svrha dokumenta može promeniti kompoziciju, ali nikada semantiku dokaza.**

Tehnički izveštaj, prigovor operatoru i RATEL podnesak mogu imati različite sekcije
([ReportCompositionProfile](file:///d:/ProjektiApp/testneta/src/IEM.Core/Reports/ReportDocumentModel.cs)), ali svaka tvrdnja nosi isto značenje.

## 135. CANONICAL_REPORT_MODEL_CONTAINS_SEMANTIC_BLOCKS_NOT_RENDERER_MARKUP

**Kanonski model izveštaja sadrži semantičke blokove, a ne markup renderera.**

Model koristi tipizirano AST stablo (`HeadingBlock`, `ParagraphBlock`, `ClaimBlock`, `MetricBlock`,
`TableBlock`, `TimelineBlock`), a ne sirovi HTML ili PDF format.

## 136. EVERY_EVIDENTIARY_REPORT_CLAIM_PRESERVES_ITS_EPISTEMIC_CLASS_AND_PROVENANCE

**Svaka dokazna tvrdnja u izveštaju čuva svoju epistemološku klasu i poreklo.**

Činjenice (`Fact`), zaključci (`Inference`) i procene (`Assessment`) ostaju eksplicitno
označeni zajedno sa referencama na izvorne dokaze.

## 137. LOCALIZATION_AND_FORMATTING_NEVER_CHANGE_REPORT_VALUE_SEMANTICS

**Lokalizacija i formatiranje nikada ne menjaju semantiku vrednosti u izveštaju.**

Brojevi i trajanja se čuvaju struktuirano (`ReportValue`), a formatiraju prikazu bez promene
osnovne numeričke vrednosti.

## 138. UNKNOWN_REPORT_VALUE_IS_NEVER_REPLACED_BY_ZERO_EMPTY_OR_INFERRED_TEXT

**Nepoznata vrednost se nikada ne zamenjuje nulom, praznim tekstom ili nagađanjem.**

`Unknown` RTT nije 0 ms, `Unknown` gubitak nije 0%, a `Unknown` uzrok nije "kvar operatora".

## 139. OVERALL_REPORT_QUALITY_NEVER_ERASES_CLAIM_SPECIFIC_QUALITY

**Opšti kvalitet izveštaja nikada ne briše specifični kvalitet pojedinačne tvrdnje.**

Čak i ako je opšti kvalitet sesije `Strong`, tvrdnja o trajanju prekida sa diskontinuitetom sata
zadržava svoju specifičnu ocenu `Limited`.

## 140. REPORT_PRESENTATION_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY

**Prezentacija izveštaja nikada ne spaja integritet, poverenje i kvalitet merenja u jedno.**

Kriptografski integritet, vremenski žig treće strane i kvalitet sondi se uvek prikazuju
kao tri zasebne ortogonalne dimenzije.

## 141. REPORT_TIMELINE_NEVER_VISUALIZES_NON_OBSERVATION_AS_NETWORK_FAILURE

**Vremenska linija nikada ne prikazuje periode mirovanja sistema kao pad mreže.**

Vreme provedeno u sleep/suspend stanju računara se prikazuje kao period bez osmatranja, a ne kao crveni mrežni prekid.

## 142. REPORT_EVENT_ORDER_PRESERVES_EVIDENCE_TIME_ORDER_NOT_WALL_CLOCK_SORTING_ALONE

**Redosled događaja u izveštaju čuva redosled dokaznog vremena, a ne samo sortiranje po UTC satu.**

Skok sistemskog sata unazad ne remeti hronološki redosled događaja koji je utvrđen monotonim brojačem i lancem.

## 143. RENDERER_LIMITATION_NEVER_CHANGES_OR_INVENTS_EVIDENCE_MEANING

**Ograničenje formata renderera nikada ne menja niti izmišlja značenje dokaza.**

Ako CSV format ne podržava vizuelne elemente, on izvozi samo podržane kolone bez dodavanja
proizvoljnih tekstualnih ocena.

## 144. NARRATIVE_TEMPLATE_NEVER_STRENGTHENS_THE_UNDERLYING_CLAIM

**Tekstualni šabloni nikada ne pojačavaju osnovnu tvrdnju.**

Šablon za prigovor sa nepoznatim uzrokom (`Cause = Unknown`) generiše formulaciju "nije moguće utvrditi",
a nikada ne svaljuje automatsku krivicu na operatora.

## 145. GENERATED_REPORT_IS_TRACEABLE_TO_THE_ANALYSIS_AND_POLICY_VERSIONS_THAT_PRODUCED_IT

**Generisani izveštaj je u potpunosti sledljiv do verzija analize i politike koje su ga stvorile.**

Izveštaj sadrži metapodatke o sesiji, profilu i hešu primenjene politike kvaliteta.

## 146. REPORT_DOCUMENT_MODEL_IS_DETERMINISTIC_FOR_IDENTICAL_SEMANTIC_INPUT

**Model dokumenta izveštaja je deterministički za identičan semantički ulaz.**

Isti snapshot analize i profil kompozicije uvek proizvode identično AST stablo dokumenta.

## 147. RENDERING_IS_STRICTLY_READ_ONLY_WITH_RESPECT_TO_REPORT_AND_EVIDENCE_MODELS

**Renderovanje je striktno read-only u odnosu na model izveštaja i dokaze.**

Nijedan renderer ne menja niti dopunjuje sadržaj modela tokom iscrtavanja.

## 148. EXPORT_RENDERING_FAILURE_NEVER_INVALIDATES_SOURCE_EVIDENCE

**Neuspeh generisanja izvoza nikada ne poništava izvorne dokaze.**

Greška u biblioteci za PDF ili nedostatak prostora za izvoz u `Exports/` nema uticaja na
integritet sesije u `Raw/` i `Evidence/`.

## 149. SENSITIVITY_METADATA_NEVER_SILENTLY_REDACTS_OR_ALTERS_AUTHORITATIVE_REPORT_CONTENT

**Metapodaci o osetljivosti nikada tiho ne cenzurišu autoritativni sadržaj izveštaja.**

Redakcija IP adresa i identifikatora je zasebna eksplicitna operacija i ne menja originalni model.

## 150. REPORT_GENERATION_CAPTURES_AN_EXPLICIT_ANALYSIS_SNAPSHOT_AND_NEVER_IMPLICITLY_TRACKS_FUTURE_STATE

**Generisanje izveštaja hvata eksplicitan snimak i nikada implicitno ne prati buduća stanja.**

Izveštaj napravljen u trenutku $T$ ostaje vezan za podatke do trenutka $T$, čak i ako se
sesija merenja nastavi.

## 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS

**Korisnički interfejs nikada ne stvara niti ponovo interpretira semantiku dokaza.**

UI samo prikazuje gotove analize i procene dobijene iz servisa.

## 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS

**Korisnički interfejs konzumira nepromenljive, verzirane snimke prezentacije.**

Ekran se osvežava atomskim snimcima (`PresentationSnapshot`) kako bi se izbeglo mešanje stanja.

## 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS

**Pogled u interfejsu nikada ne meša semantičko stanje iz različitih revizija analize.**

Svi paneli ekrana u jednom trenutku prikazuju tačno istu verziju analitičkog snimka.

## 154. UI_STATE_IS_NEVER_EVIDENCE_STATE

**Stanje korisničkog interfejsa nikada nije stanje dokaza.**

Izbor taba, pozicija skrola ili dimenzije prozora se čuvaju lokalno i nemaju nikakve veze sa dokaznim paketom.

## 155. UI_PREFERENCES_NEVER_ENTER_THE_EVIDENCE_PACKAGE

**Korisnička podešavanja interfejsa nikada ne ulaze u dokazni paket.**

Teme, jezici ili filteri se ne upisuju u `Raw/`, `Derived/` ili `Evidence/`.

## 156. UI_NAVIGATION_NEVER_CHANGES_MEASUREMENT_EXECUTION

**Navigacija kroz interfejs nikada ne menja tok izvršenja merenja.**

Promena taba, minimizovanje prozora ili otvaranje detalja ne pauzira niti resetuje sonde.

## 157. UI_COMMANDS_REACH_EVIDENCE_OPERATIONS_ONLY_THROUGH_AUTHENTICATED_IPC

**UI komande dopiru do operacija sa dokazima isključivo preko autentifikovanog IPC-a.**

UI nema direktan pristup fajlovima sesije, već šalje zahteve servisu preko IPC granice.

## 158. OPTIMISTIC_UI_NEVER_CONFIRMS_AN_UNACKNOWLEDGED_EVIDENCE_TRANSITION

**Optimistički UI nikada ne potvrđuje nepotvrđenu tranziciju dokaza.**

Dugme prikazuje "U toku...", a konačno stanje (npr. `Sealed`) tek kada servis pošalje potvrdu.

## 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED

**Nepoznata vrednost u interfejsu nikada ne postaje nula, uspeh, pad ili nepodržano.**

Nepoznata svojstva se eksplicitno prikazuju kao nepoznata (Unknown / No data yet).

## 160. SERVICE_CONNECTIVITY_FAILURE_IS_NEVER_PRESENTED_AS_NETWORK_CONNECTIVITY_FAILURE

**Prekid veze sa servisom se nikada ne prikazuje kao prekid internet konekcije.**

Gubitak lokalne IPC veze jasno ukazuje na nedostupnost servisa (`ServiceUnavailable`), a ne na pad mreže.

## 161. NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE

**Intervali mirovanja računara se nikada ne vizuelizuju kao pad mreže.**

Periodi spavanja/mirovanja se u vremenskoj liniji označavaju kao `HostSuspended`, a ne crvenom bojom za outage.

## 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY

**UI nikada ne spaja integritet, poverenje i kvalitet merenja.**

Bedževi za integritet (Verified), vremenski žig (TSA) i kvalitet sondi (Strong/Limited) ostaju jasno razdvojeni.

## 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY

**Opšti kvalitet u interfejsu nikada ne skriva specifični kvalitet pojedinačne tvrdnje.**

Prikaz sadrži tabelu sa pojedinačnim ocenama svake tvrdnje.

## 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE

**Korisničke beleške i metapodaci slučaja nikada ne menjaju izvorne dokaze.**

Korisnički unos podataka o ugovoru ostaje u radnom prostoru slučaja (`CaseWorkspaceState`).

## 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM

**Korisnička izjava se nikada ne promoviše u dokaznu tvrdnju.**

Beleške korisnika se tretiraju isključivo kao `UserStatement`, a ne kao tehnički `Fact`.

## 166. REPORT_PREVIEW_IS_A_READ_ONLY_PROJECTION_OF_THE_CANONICAL_REPORT_DOCUMENT_MODEL

**Pregled izveštaja je striktno read-only projekcija kanonskog modela dokumenta.**

Pregled prigovora ili RATEL obrasca ne modifikuje osnovni model izveštaja.

## 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT

**Neizvršeno ili odbijeno merenje brzine se nikada ne prikazuje kao 0 Mbps.**

Ako merenje nije izvršeno (`Ran = false`), UI navodi razlog odbijanja umesto lažne nule.

## 168. STALE_PRESENTATION_SNAPSHOT_NEVER_OVERRIDES_A_NEWER_REVISION

**Zastareli snimak prezentacije nikada ne prepisuje noviju reviziju.**

Snimci sa manjim revizionim brojem od trenutnog se automatski odbacuju.

## 169. UI_REFRESH_NEVER_IMPLICITLY_CREATES_NEW_MEASUREMENT_EVIDENCE

**Osvežavanje interfejsa nikada implicitno ne kreira nove dokaze merenja.**

Osvežavanje ekrana samo ponovo iscrtava postojeće stanje.

## 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE

**Vizuelni stil nikada ne menja niti stapa semantička stanja.**

Boje i bedževi strogo prate definisane semantičke tokene.

## 171. UI_TOOLKIT_IS_PRESENTATION_IMPLEMENTATION_NOT_EVIDENCE_SEMANTICS

**UI radni okvir je prezentaciona implementacija, a ne semantika dokaza.**

WPF na Windows-u i buduća Avalonia na Linux-u konzumiraju iste kanonske ugovore.

## 172. PRESENTATION_ABSENCE_OR_LOAD_FAILURE_IS_NEVER_PRESENTED_AS_MEASUREMENT_FAILURE

**Odsustvo prikaza ili greška učitavanja se nikada ne prikazuje kao mrežni neuspeh.**

Status "Čekanje na podatke" ostaje neutralan i ne generiše lažne alarme.

## 173. UI_EXPORT_OR_PREVIEW_FAILURE_NEVER_CHANGES_EVIDENCE_STATUS

**Greška u izvozu ili pregledu nikada ne menja status dokaza.**

Pucanje eksterne PDF biblioteke ne utiče na status sesije u servisu.

## 174. REDACTION_NEVER_MUTATES_SOURCE_EVIDENCE

**Redakcija nikada ne menja izvorne dokaze.**

Originalni dokazni paket u `Raw/`, `Derived/` i `Evidence/` ostaje potpuno neizmenjen i zaštićen;
redakcija isključivo generiše izvedeni paket.

## 175. REDACTED_PACKAGE_IS_ALWAYS_EXPLICITLY_DERIVED

**Redigovani paket je uvek eksplicitno označen kao izvedeni artefakt.**

Paket nosi `RedactionManifest` i oznaku da predstavlja izvedenu verziju, a nikada se ne predstavlja
kao primarni original.

## 176. REDACTED_PACKAGE_ALWAYS_BINDS_TO_THE_ORIGINAL_MANIFEST_HASH

**Redigovani paket se uvek vezuje za heš manifesta originalnog paketa.**

`RedactionManifest` čuva tačan SHA-256 originalnog `manifest.json`-a, omogućavajući verifikatoru
da matematički dokaže vezu porekla.

## 177. ORIGINAL_SIGNATURE_IS_NEVER_REPRESENTED_AS_SIGNING_REDACTED_CONTENT

**Originalni potpis se nikada ne predstavlja kao potpis nad redigovanim sadržajem.**

Redigovani paket dobija sopstveni `manifest.json` i novi digitalni potpis `manifest.sig`.

## 178. REDACTION_POLICY_IS_VERSIONED_AND_HASH_BOUND

**Politika redakcije je verzirana i vezana hešom.**

Paket referencira tačnu verziju i heš primenjene politike ([RedactionPolicy.cs](file:///d:/ProjektiApp/testneta/src/IEM.Core/Redaction/RedactionPolicy.cs)).

## 179. SAME_SOURCE_AND_POLICY_PRODUCE_THE_SAME_REDACTED_SEMANTICS

**Isti izvor i ista politika uvek daju identičnu semantiku redigovanog paketa.**

Proces redakcije je u potpunosti deterministički i bez heurističkog nagađanja.

## 180. REDACTION_NEVER_FABRICATES_REPLACEMENT_EVIDENCE

**Redakcija nikada ne izmišlja zamenske dokaze.**

Uklonjeni identifikatori se zamenjuju eksplicitnim šablonom maskiranja (npr. `[REDACTED-SSID]`),
a ne lažnim podacima koji liče na validna merenja.

## 181. REMOVED_INFORMATION_NEVER_BECOMES_UNKNOWN_MEASUREMENT_DATA

**Uklonjene informacije nikada ne postaju nepoznati rezultati merenja.**

Redigovano polje se označava kao redigovano, a ne kao da merenje nije uspelo (`Unknown`).

## 182. REDACTION_METADATA_NEVER_REVEALS_THE_REDACTED_VALUE

**Metapodaci o redakciji nikada ne otkrivaju originalnu redigovanu vrednost.**

Revizioni zapisi u manifestu redakcije čuvaju heš pređašnjeg sadržaja (`FieldHashBefore`),
a nikada izvorni tekst.

## 183. REDACTED_PACKAGE_TAMPERING_NEVER_INVALIDATES_SOURCE_EVIDENCE

**Izmena redigovanog paketa nikada ne poništava izvorne dokaze.**

Ako neko ošteti ili modifikuje redigovani fajl, samo taj izvedeni paket postaje nevažeći,
dok originalni dokazni paket ostaje 100% netaknut.

## 184. REDACTION_FAILURE_NEVER_MODIFIES_ORIGINAL_EVIDENCE_STATE

**Neuspeh u procesu redakcije nikada ne menja stanje originalnih dokaza.**

Greška pri generisanju redigovanog paketa prekida samo izvoz bez ikakvih posledica po sesiju.

## 185. USER_SHARE_POLICY_NEVER_CHANGES_CANONICAL_EVIDENCE_SEMANTICS

**Korisnička politika deljenja nikada ne menja kanonsku semantiku dokaza.**

Odabir profila redakcije utiče samo na obim maskiranja pri deljenju trećim licima.

## 186. REDACTION_SCOPE_IS_EXPLICIT_AND_AUDITABLE

**Opseg redakcije je eksplicitan i podložan reviziji.**

Svaka pojedinačna izmena se beleži kao `RedactionEntry` u `redaction-manifest.json`.

## 187. UNRECOGNIZED_SENSITIVE_FIELDS_FAIL_CLOSED_WHEN_POLICY_REQUIRES_COMPLETE_REDACTION

**Neprepoznata osetljiva polja postupaju po fail-closed principu pri striktnoj anonimizaciji.**

Ako profil nalaže potpunu privatnost, sumnjiva polja se maskiraju ili uklanjaju.

## 188. REDACTED_DERIVATIVE_HAS_ITS_OWN_INTEGRITY_IDENTITY_AND_SIGNATURE

**Redigovani izvedeni paket poseduje sopstveni integritetski identitet i potpis.**

Verifikacija redigovanog paketa utvrđuje i validnost sopstvenog potpisa i poreklo do originala.

## 189. REDACTION_CHAIN_NEVER_LOSES_PROVENANCE_TO_THE_CANONICAL_SOURCE

**Lanac redakcije nikada ne gubi poreklo do kanonskog izvora.**

Korisnik ili sud uvek mogu uporediti otisak originala sa stvarnim originalom i potvrditi da
redigovana verzija verno odražava stvarna merenja.

## 190. REDACTED_EXPORT_IS_NEVER_PRESENTED_AS_THE_CANONICAL_EVIDENCE_PACKAGE

**Redigovani izvoz se nikada ne prikazuje kao kanonski dokazni paket.**

Zaglavlja, oznake i izveštaji jasno nose oznaku "REDACTED DERIVATIVE COPY".

## 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND

**Identitet artefakata izdanja je eksplicitan i vezan za verziju.**

Svi binarni fajlovi dele isti `ReleaseIdentity`.

## 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY

**Svi artefakti jednog izdanja dele jedan kanonski identitet izdanja.**

Aplikacija, servis, instalater, SBOM i manifest izdanja pokazuju na iste Git heš i Build parametre.

## 193. RELEASE_IDENTITY_NEVER_CHANGES_AFTER_ARTIFACT_SIGNING

**Identitet izdanja se nikada ne menja nakon potpisivanja artefakata.**

Potpisani binarni fajlovi se ne mogu naknadno prepakivati ili preimenovati bez rušenja potpisa.

## 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED

**Nepotpisani obavezni izvršni fajl se nikada ne objavljuje.**

Ako ijedan `.exe` fajl nema digitalni potpis, CI gate automatski odbija izdanje.

## 195. AUTHENTICODE_SIGNATURE_IS_VERIFIED_BEFORE_RELEASE_ACCEPTANCE

**Authenticode potpis se obavezno verifikuje pre prihvatanja izdanja.**

Proces ne podrazumeva samo pokretanje alata za potpisivanje već i verifikaciju potpisa i lanca.

## 196. RELEASE_SIGNING_FAILURE_ALWAYS_FAILS_CLOSED

**Neuspeh u potpisivanju izdanja uvek postupa po fail-closed principu.**

Ako dođe do greške tokom potpisivanja, ceo release proces se momentalno prekida.

## 197. TIMESTAMP_FAILURE_NEVER_SILENTLY_DEGRADES_TO_UNTIMESTAMPED_RELEASE

**Neuspeh vremenskog žiga nikada tiho ne degradira izdanje na nepotpisano žigom.**

Authenticode žig je obavezan kako bi potpis ostao važeći i nakon isteka sertifikata.

## 198. SIGNED_ARTIFACT_IS_NEVER_MUTATED_AFTER_SIGNING

**Potpisani artefakt se nikada ne menja nakon potpisivanja.**

Svaka izmena bajta na potpisanom fajlu poništava digitalni potpis.

## 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS

**Manifest izdanja hešira tačne distribuirane artefakte.**

`release-manifest.json` sadrži SHA-256 heševe svih fajlova koji se isporučuju korisniku.

## 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED

**SBOM se generiše direktno iz izdanja koje se distribuira.**

Softverski spisak komponenti (SBOM) se generiše automatski iz stvarnih zavisnosti.

## 201. SBOM_FAILURE_NEVER_PRODUCES_A_FALSE_COMPLETE_SBOM

**Neuspeh generisanja SBOM-a nikada ne proizvodi lažno kompletan SBOM.**

Ako SBOM ne može da se generiše, release gate odbija izdanje.

## 202. RELEASE_MANIFEST_AND_EVIDENCE_MANIFEST_ARE_SEPARATE_TRUST_DOMAINS

**Manifest izdanja i dokazni manifest su potpuno odvojeni domeni poverenja.**

Manifest izdanja osigurava integritet distribuiranog softvera, a dokazni manifest integritet merenja.

## 203. SOFTWARE_RELEASE_METADATA_NEVER_ENTERS_EVIDENCE_SEMANTICS_AS_MEASUREMENT_DATA

**Metapodaci o izdanju softvera nikada ne ulaze u semantiku dokaza kao podaci merenja.**

Verzija aplikacije je podatak o poreklu (provenance), a ne mrežna činjenica.

## 204. INSTALL_OR_UPGRADE_NEVER_MUTATES_EXISTING_CANONICAL_EVIDENCE

**Instalacija ili nadogradnja nikada ne menja postojeće kanonske dokaze.**

Instalater ažurira binarne fajlove u programskom folderu, a korisničke sesije u `AppData/` ostaju netaknute.

## 205. UNINSTALL_NEVER_SILENTLY_DELETES_USER_EVIDENCE

**Deinstalacija nikada tiho ne briše korisničke dokaze.**

Uklanjanje aplikacije uklanja samo binarne datoteke, čuvajući sakupljene dokazne pakete.

## 206. INSTALLER_FAILURE_NEVER_LEAVES_A_FALSE_RUNNING_SERVICE_STATE

**Neuspeh instalatera nikada ne ostavlja servis u lažnom stanju rada.**

Ako instalacija padne, rollback obezbeđuje čist povratak.

## 207. SERVICE_AND_APPLICATION_RELEASE_VERSIONS_NEVER_SILENTLY_DIVERGE

**Verzije servisa i aplikacije se nikada tiho ne razilaze.**

Aplikacija i servis uvek proveravaju kompatibilnost verzija preko IPC-a.

## 208. RELEASE_ACCEPTANCE_REQUIRES_FRESH_INSTALL_RUNTIME_VERIFICATION

**Prihvatanje izdanja zahteva runtime verifikaciju na čistoj instalaciji.**

CI testira pokretanje na čistom sistemu pre davanja konačnog statusa `Accepted`.

## 209. FAILED_RELEASE_GATE_NEVER_PUBLISHES_A_RELEASE_AS_ACCEPTED

**Neuspeli release gate nikada ne objavljuje izdanje kao prihvaćeno.**

Ako bilo koji korak u proveri integriteta padne, izdanje se označava kao odbijeno.

## 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET

**Distribuirani artefakti su bit-po-bit identični verifikovanom setu izdanja.**

Korisnik dobija tačno one binarne fajlove koji su prošli sve bezbednosne kapije.


















