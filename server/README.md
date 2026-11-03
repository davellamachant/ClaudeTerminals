# User count service

Copies of the app whose users said yes to *May we count you?* send one request an hour while they are open:

```
GET https://count.llamachant.com/ping?id=<32 hex>&v=<app version>&os=windows&osv=<system version>&projects=<n>&sessions=<n>
```

This Azure Function app ([`Count`](Count)) records it, and adds the numbers up for us.

## What it keeps

One row per copy per UTC hour in the `pings` table: the hour, the id, the app's version, the operating system's name
and version, and how many projects and sessions were open. A copy that pings twice in an hour is still one row.
Projects and sessions are **only counts**: the app never sends their names, folders or anything in them.

**Nothing else is kept**: not the IP address, the user agent or any other header, and there's no Application Insights
or request log. Anything that isn't exactly an id, two version numbers, a system name and two counts (up to 10,000)
gets a `400` and isn't stored. Rows are deleted after 400 days (the `forget` function, daily at 03:30 UTC).

The id is random, made when the user said yes and forgotten when they say no, so nothing links a row to a person.

## Reading the numbers

`GET https://count.llamachant.com/stats?days=30&code=<function key>` returns JSON:

- `users`: different copies in the period, each counted once as last seen.
- `days`: per day, `users`; `userHours` (a copy open three hours is three); `averageProjects` and `averageSessions`
  (the most each user had open at once that day, averaged); and users by version. A day with nobody is `0`.
- `hours` (for 7 days or fewer): per hour, the users and the projects and sessions open between them.
- `versions`, `systems`, `systemVersions`: the period's users by app version, by system (`windows`) and by system
  and version (`windows 10.0.26300`).

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
