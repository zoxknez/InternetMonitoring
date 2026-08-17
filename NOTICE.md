# Tuđi rad u ovom programu

Ovaj projekat je pod MIT licencom (`LICENSE`). Ovde stoji sve što nije naše, uz licencu pod
kojom dolazi. Spisak je deo obaveze, ali i deo iste priče kao i sve ostalo u ovom repozitorijumu:
mora se moći utvrditi šta je tačno ugrađeno u program koji pravi dokaz.

Tačne verzije su zaključane u `Directory.Packages.props` i u `packages.lock.json` po projektu,
pa se ista gradnja može ponoviti i godinama kasnije.

## Fontovi ugrađeni u izveštaj

**Liberation Sans i Liberation Mono** - SIL Open Font License 1.1.
Licenca putuje sa fontovima, pa stoji na dva mesta: u izvoru
(`src/IEM.Evidence/Fonts/LICENSE-LiberationFonts.txt`) i pored binarnih fajlova u svakoj
objavljenoj arhivi.

Fontovi su ugrađeni u sam program namerno. PDF izveštaj ide operateru i RATEL-u, a naša slova
(č, ć, š, ž, đ) ne smeju da zavise od toga šta je instalirano na mašini na kojoj se dokument
otvara.

## Biblioteke

| Paket | Licenca | Čemu služi |
|---|---|---|
| [PDFsharp](https://github.com/empira/PDFsharp) | MIT | PDF izveštaj. Izabran jer je potpuno upravljan (bez native binarnih fajlova po platformi). |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | MIT | SQLite indeks nad sirovom evidencijom. |
| [ManagedNativeWifi](https://github.com/emoacht/ManagedNativeWifi) | MIT | Windows Native Wi-Fi API: signal, BSSID, skeniranje mreža. |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | Vezivanje podataka u prozoru. |
| [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) | MIT | Ikona u sistemskoj traci. |
| Microsoft.Extensions.Hosting.WindowsServices | MIT | Windows servis. |
| Microsoft.Win32.SystemEvents | MIT | Obaveštenja o spavanju računara. |
| [xunit](https://github.com/xunit/xunit) | Apache-2.0 | Testovi (ne ulazi u objavljeni program). |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT | Pokretanje testova (ne ulazi u objavljeni program). |

## Mete merenja

Program šalje ping i HTTP zahteve ka javnim metama trećih strana - Cloudflare (1.1.1.1),
Google (8.8.8.8) i Quad9 (9.9.9.9) - a brzinu meri preko Cloudflare-ovog javnog servisa za
merenje (`speed.cloudflare.com`). Te mete nisu deo ovog projekta i njihova dostupnost ne
zavisi od nas. Izabrane su zato što su u tri različite mreže, pa jedan pad ne izgleda kao
prekid internet veze.
