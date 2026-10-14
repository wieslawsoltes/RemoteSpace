# Dependency auditing

Keep NuGet auditing enabled for the Uno application, including transitive
packages. Its project promotes NU1901 through NU1904 to errors: a reported
vulnerable dependency must be investigated instead of silently shipping with a
warning. This is a package-restore gate, not proof that the application has no
security defects or that every upstream advisory is applicable to a reachable
RemoteSpace code path.

## Cryptography dependency review

The 2026-10-14 desktop CI restore resolved System.Security.Cryptography.Pkcs and
System.Security.Cryptography.Xml 10.0.12 through the desktop host dependency
graph. NuGet reported the following Microsoft advisories, published 2026-10-13:

- Pkcs: [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7)
  and [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx).
- Xml: [GHSA-7hqp-7c5m-4vq6](https://github.com/advisories/GHSA-7hqp-7c5m-4vq6),
  [GHSA-c399-9853-6jmj](https://github.com/advisories/GHSA-c399-9853-6jmj), and
  [GHSA-r955-8q5j-3h83](https://github.com/advisories/GHSA-r955-8q5j-3h83).

Microsoft identifies 10.0.13 as the patched .NET 10 package version. Explicit
references for both application targets select that patch rather than the older
transitive versions. A multi-target restore audits every restored graph even
when a command builds only the desktop framework. Desktop-only overrides left
an older version eligible in another restored graph, so the overrides now apply
to both targets. This does not enable new cryptographic operations in the browser.
Auditing remains enabled; both target builds must restore without vulnerability
findings and pass the full runtime suites.

A clean package audit does not update the runtime on a deployed machine. Keep
the host .NET runtime and SDK serviced too, and rebuild/redeploy self-contained
applications after upstream runtime security fixes. Use Microsoft's advisory
instructions and the supported deployment platform's update process. The bridge
still requires separately operated authentication, target allowlisting, valid
certificates and trusted administration.

## Validation

Run the ordinary Uno desktop build and browser publish, without disabling audit
or adding NU190x suppressions. The full browser workspace and authorized
RDP interoperability suites must remain passing. Review the exact revision's
build logs for resolved package diagnostics; source references alone are not
executed validation. Upstream advisories and dependencies change over time, so
future failures must be reviewed rather than treated as permanent exemptions.
