These files are invented for the Drivers tests. They were written by hand from two sources only: the XML shape that `pnputil /enum-drivers /devices /format xml` and `pnputil /enum-devices ... /format xml` produce (as read by `PnpUtilXml`), and the cases the tests need. They were not captured from, or derived from, any real PC.

Every slot number, INF name, provider, device name, instance id, hardware id, problem code and status, version and date is made up. Most vendor names are the usual placeholder companies (Contoso, Fabrikam, Litware, Tailspin Toys and so on). Real vendor words appear only where a test checks PC Tidy's vendor catalog: NVIDIA (graphics tool), AMD (a non-graphics device must not suggest AMD's graphics tool), Logitech (hardware with no G HUB installed), Razer (an installed tool), Realtek (the board-page note) and MSI (the board support link). Each case is labelled with an XML or JSON comment next to the entry that carries it.

- `drivers.xml`: 20 third-party packages (`oem103.inf` to `oem364.inf`) plus one defensive entry that isn't an `oemNNN.inf` name.
- `devices.xml`: 12 devices, with matching drivers for the rollback cases.
- `disconnected.xml`: 9 disconnected devices. Their dates are regional text and never trusted; the typed dates are in `machine.json`.
- `problem.xml`: 3 problem devices (code 28 with no driver, code 37 with a status, and one turned off with no code; `devices.xml` writes the code 28 in hex).
- `machine.json`: everything else (WMI problem codes, Windows Update offers, installed apps, board, package sizes, last driver change, typed device facts).

The test clock is 10 Nov 2026 12:00 UTC. Dates are only chosen to fall on the right side of the 14-day cleanup rule and the 30-day old-device rule.
