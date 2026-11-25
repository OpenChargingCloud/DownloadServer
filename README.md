# DownloadServer

The download server for charging stations and local controllers as a
[WWCP_Node](https://github.com/OpenChargingCloud/WWCP_Node): it offers files -
firmware images first of all, and whatever else they are pointed to - by HTTP
GET on a port of its own and over SFTP on the node's SSH server, takes a
download that stopped up where it stopped, records who fetched what, and shows
both in its web interface, its JSON API and its command line.

## Areas and files

The files lie in areas: one directory each below the files directory, and the
first part of the path a file is fetched by - `/firmware/station-4.2.1.bin`.
The disk is what is offered: a file is offered as soon as it is in its area's
directory, and no longer once it is gone. Files are published

- over SFTP, by an account that may publish them (the `publisher` role),
- through the web interface or `PUT /api/v1/downloadServer/files/{area}/{name}`,
  up to what the web interface's HTTP server takes as one request (8 MiB by
  default) - a larger one in parts, smaller ones over a line too slow for half
  a minute each -, or
- by putting them in the area's directory.

A file published over the API or SFTP is written under a hidden name and moved
into place once it is complete, so that nobody downloads half of it.

## Downloads

| | |
|---|---|
| HTTP | `GET http(s)://<host>:8365/<area>/<file name>`, signed in as a `downloader` with HTTP Basic Auth (or without credentials where `"anonymousHTTP": true`). One `Range` of bytes a request (206), 416 beyond the end, `If-Range` by the file's SHA-256 or its time of writing, HEAD. Sent from the disk as it is read, never held in memory, for as long as the client takes. HTTPS with a certificate of its own (`tls`), not the web interface's. |
| SFTP | `sftp://<host>:22365/<area>/<file name>`, signed in as a `downloader` with an SSH key of its own, or its password where the `ssh` section says `"passwords": true`. |

Every download is recorded in `downloads.jsonl` beside the areas: who, which
file, from which byte on, how many bytes, how large the file was, from where.

## Configuration

```json
"downloadServer": {
    "directory":      "files",
    "areas":          [ "firmware" ],
    "http":           true,
    "httpListen":     "any",
    "httpPort":       8365,
    "tls":            { "certificate": "store" },
    "publicURL":      "https://firmware.example.org:8365/",
    "anonymousHTTP":  false,
    "sftp":           true,
    "maxFileSize":    1073741824
}
```

`tls` is written as `web.tls` is - the certificate store's TLS server identity
for the listener `download` (or one told no listener), or `{ "fullchain":
"<file>", "privateKey": "<file>" }` - but without `redirectPort`; left out,
the download port speaks plain HTTP. `publicURL` is the URL charging stations
are told; left out, it is guessed from `httpListen`, or the name of the
machine where that is `any`.

The web interface listens on 2365, its SSH server - and with it SFTP - on 22365.

## Roles

| Role | May |
|---|---|
| `downloader` | download files, and nothing else - a charging station, a local controller |
| `publisher` | see, publish and take away files, and download them - a build pipeline |
| `operator` | look at everything, publish and take away files, ask whether the network works |

## The source

| | |
|---|---|
| `DownloadServer/DownloadServerNode.cs` | the node: the `downloadServer` section, the areas, the download port, the SFTP areas |
| `DownloadServer/DownloadServerNode.Files.cs` | the files offered, published and taken away |
| `DownloadServer/DownloadServerNode.HTTPDownloads.cs` | downloads by HTTP GET, with ranges, sent from the disk as they are read |
| `DownloadServer/DownloadPortCertificate.cs` | the certificate of the download port - its own, not the web interface's |
| `DownloadServer/DownloadServerNode.Downloaders.cs` | the accounts in the `downloader` role |
| `DownloadServer/Downloads/` | the record of the downloads |
| `DownloadServer/HTTPAPI/` | the JSON API below `/api/v1/downloadServer` |
| `DownloadServer/CommandLine/` | `downloadServer`, `files`, `downloads` and `downloaders` |
| `DownloadServer/Frontend/` | the web interface: TypeScript and SCSS, bundled by webpack and embedded |
| `DownloadServerTests/` | its tests, and WWCP_Node's conformance suite asked of it |

```
dotnet test DownloadServerTests/DownloadServerTests.csproj
```

Open Source under the **Affero GPL 3.0 license**.
