# User count service

Copies of the app whose users said yes to *May we count you?* send one request a day:

```
GET https://<count address>/ping?id=<32 hex>&v=<app version>&os=<Windows version>
```

This Azure Function app ([`Count`](Count)) records it, and adds the numbers up for us.

## What it keeps

One row per copy per UTC day in the `pings` table: the day, the id, the app's version and the Windows version. A copy
that pings twice in a day is still one row. **Nothing else is kept**: not the IP address, the user agent or any other
header, and there's no Application Insights or request log. Anything that isn't exactly an id and two version numbers
gets a `400` and isn't stored. Rows are deleted after 400 days (the `forget` function, daily at 03:30 UTC).

The id is random, made when the user said yes and forgotten when they say no, so nothing links a row to a person.

## Reading the numbers

`GET https://<count address>/stats?days=30&code=<function key>` returns JSON:

- `users`: different copies in the period, each counted once under the version it ran last.
- `days`: users per day (a day with nobody is `0`), by version.
- `versions`, `windows`: the period's users by app version and by Windows version.

`days` goes up to 400. The key is the function app's default host key:
`az functionapp keys list -g <resource group> -n <app> --query functionKeys.default -o tsv`.

## Deploying

[`azure-pipelines-count.yml`](../azure-pipelines-count.yml) builds, tests and deploys it with
[`count.bicep`](count.bicep): a storage account for it and a Flex Consumption function app (.NET 10), at most 40
instances. The pipeline's header lists the one-time setup.

Then give it an address of ours. In the function app, **Settings › Custom domains › Add custom domain**, for example
`count.llamachant.com`, with the CNAME and `asuid` TXT records it asks for and a free App Service managed certificate.
The app's `TelemetryService.PingUrl` is that address plus `/ping`.

## Running it locally

```powershell
npx azurite --silent --location $env:TEMP\azurite     # the storage emulator, in another window
cd server\Count
func start                                            # Azure Functions Core Tools
# http://localhost:7071/ping?id=1868c4d72ca345de80adb48806e14052&v=1.2.0&os=10.0.26300
# http://localhost:7071/stats?days=7   (no key needed locally)
```

`dotnet test server\Count.Tests` runs the tests; the storage ones run only while Azurite is.
