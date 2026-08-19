# Plan Razvoja — Internet Evidence Monitor 3.1

> **Status:** Planiran budući ciklus (nakon formalnog izdanja v3.0.0).  
> **Osnova:** 3.0.0 Release Candidate je zamrznut sa 210 invarijanti i 781 automatskim testom.

---

## 1. Fokus 3.1 Ciklusa

Ciklus 3.1 se bavi proširenjima i platformskim adaptacijama, bez razvodnjavanja ili retroaktivnog menjanja zamrznutih dokaznih ugovora iz 3.0:

1. **Linux Avalonia UI (`IEM.App.Linux`)**:
   - Kompletiranje desktop GUI-ja za Linux distribucije (Ubuntu, Debian, Fedora, Arch) koristeći Avalonia UI radni okvir.
   - Povezivanje na Linux `IEM.Service.Linux` preko Unix Domain Socket-a.
   - Poštovanje Invarijante 171 (`UI_TOOLKIT_IS_PRESENTATION_IMPLEMENTATION_NOT_EVIDENCE_SEMANTICS`).

2. **Automatsko prepoznavanje ISP ugovornih profila**:
   - Proširenje kataloga paketa i minimalnih garantovanih brzina za domaće i regionalne operatere (MTS, Yettel, SBB, A1).
   - Automatsko popunjavanje parametara prigovora na osnovu izabranog komercijalnog paketa.

3. **Lokalizacija i regulatorni profili za EU / BEREC**:
   - Podrška za engleski i regionalne jezike u `ReportDocumentModel` rendererima.
   - Profil usklađenosti sa EU regulativom o otvorenom internetu (BEREC Net Neutrality Guidelines).

4. **Napredna telemetrija lokalnih smetnji**:
   - Analiza zagušenja Wi-Fi kanala u realnom vremenu (ko-kanalne i susedne smetnje).
   - Detekcija lokalnih mrežnih zagušenja (npr. drugi uređaji na lokalnoj mreži koji generišu zasićenje).

---

*Ovaj dokument je otvoren za buduće planiranje. Zamrznuti 3.0 ugovori i invarijante ostaju nepromenjeni temelj projekta.*
