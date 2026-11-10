These files are invented for the Drivers parser tests. They were written by hand from two sources only: the XML shape that `pnputil /enum-drivers /devices /format xml` and `pnputil /enum-devices ... /format xml` produce (as read by `PnpUtilXml`), and the edge cases the tests need. They were not captured from, or derived from, any real PC.

Every slot number, INF name, provider, device name, hardware id, problem code and status, version and date is made up (vendor names are the usual placeholder companies: Contoso, Fabrikam, Tailspin Toys and so on). Each edge case is labelled with an XML comment next to the entry that carries it.

- `drivers.xml`: 20 driver packages, 13 of them bound to at least one device.
- `disconnected.xml`: 14 disconnected devices. All dates are regional text, so none is trusted.
- `problem.xml`: 3 problem devices (one with hex code 0x1C and no driver, a scanner with code 10 and a status, and one disabled with no code).

The scenario fixtures next to this folder (`..\scenario\`) are invented in the same way; see their README.
