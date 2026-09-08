# SamsungController licensing

SamsungController uses **MIT + Commons Clause v1.0 with Paid Services Exception
v1.0** starting with the repository change that introduces this document. This
is one combined license, not a choice of MIT or Commons Clause. The operative
terms are in [LICENSE](../LICENSE); this guide explains them and does not add or
replace legal terms.

The standard [Commons Clause v1.0 text](https://commonsclause.com/) is included
alongside the MIT permission grant and warranty disclaimer. A separately labeled,
project-specific exception expressly permits businesses to charge for services
performed using the tool. This combination is not unmodified MIT, not plain
MIT + Commons Clause, and not an OSI-approved open-source license. Describe it
as **source-available** and link the complete license instead of labeling it
`MIT` in package metadata or badges.

## What you may do

- Use the app for personal projects or inside a business, without a license fee.
- Charge customers for display calibration or other work performed using the
  app, even when the app provides a substantial part of that service's value.
- Charge for genuine installation, configuration, troubleshooting, training,
  consulting, and support work. The charge must be for the service, not for a
  software copy, license, or right to access the tool.
- Modify the app and keep those changes private. There is no requirement under
  this combined license to publish your source or contribute changes upstream.
- Redistribute the app or modified versions without charge, preserving the
  complete combined license and copyright notices.
- Give a customer a free copy alongside a paid service, provided access to that
  copy and the right to use it are available without buying that service or any
  other product. Charging for installation labor is different from charging for
  the copy itself.
- Deliver calibration reports, settings files, or other results of your work.
  Creating an output with the app does not by itself place that output under
  this software license. Any pre-existing third-party rights still apply.

## What the license restricts

- Selling a download, copy, or license of SamsungController itself.
- Merely changing its name, branding, packaging, or a few features and selling
  what is still substantially the same tool.
- Charging customers to access the tool itself as a hosted or subscription app.
  Running it remotely yourself while providing a calibration service is allowed
  by the paid-services exception.
- Disguising payment for a software copy, license, or access as a service fee.
  A required purchase cannot make a purportedly free copy qualify for the
  exception.
- Removing the required copyright or combined license notices from distributed
  copies or substantial portions of the software.

For example, a technician may charge for calibrating a customer's TV with the
app and provide a report. A reseller may not charge for a download of the same
app just by calling the payment a setup or support fee when no distinct service
is being sold.

## Larger products and derivatives

Commons Clause is not a universal prohibition on selling every product that
contains some of this code. Its restriction turns on whether a product or
service derives its value entirely or substantially from the software's
functionality. Its [official FAQ](https://commonsclause.com/) allows sufficiently
value-added larger products. The paid-services exception does not remove that
boundary or create a blanket ban on every commercial derivative.

This also means that a proposed integration may require a case-specific reading
of the actual terms. There is no automatic safe harbor based on a percentage of
changed lines or simply renaming the application.

## Earlier MIT versions remain MIT

The versions published before this licensing change, including v0.3.0 and the
working branch through commit
[`1d7acb7269c6f159ab2ad8ff9d7c7d82ee92a80b`](https://github.com/whipstein/SamsungController/blob/1d7acb7269c6f159ab2ad8ff9d7c7d82ee92a80b/LICENSE),
were offered under MIT alone. Those existing permissions are not revoked.
Someone may continue using, modifying, and selling those MIT versions under
their original terms. This change does not rewrite old tags, published release
archives, or the license on previously received copies.

Use the license shipped with the particular version you received. Merely
changing a license file cannot withdraw the MIT grant on previously released
code, and this change does not claim exclusive new restrictions over that code.

## Third-party code and contributions

Dependencies and separately licensed third-party materials retain their own
licenses. This license does not replace their notices, grant rights their
authors have not granted, or remove their source-sharing obligations. The
SamsungController copyright notice is preserved in the MIT portion of the
combined license.

If you contribute changes for inclusion in future SamsungController versions,
offer them under this combined license and identify any third-party material
and its applicable terms. This request is not a transfer of your copyright or
an assertion of permission over contributions made under earlier terms.

## Packages and scope

Official release packaging includes the complete `LICENSE` and this guide.
Build/publish output and NuGet packages also include the combined license;
NuGet metadata references the license file rather than asserting `MIT` alone.
Redistributors must keep the full notice, including the paid-services exception,
with copies or substantial portions of the software.

The MIT warranty disclaimer remains in place; this software is provided as is.
This is a best-effort, project-specific licensing choice, not a legal opinion
or a guarantee of enforceability. The additional exception has not been
reviewed by an attorney. Nothing here replaces the operative `LICENSE`.
